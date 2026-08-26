using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>b2-irdiskcache：(逻辑文件名, 标签名) 索引键比较器——文件名 OrdinalIgnoreCase（对齐 SCIgnoreCase）+ 标签名按 StrComper（对齐 labelDic）。</summary>
internal sealed class IrIndexKeyComparer : IEqualityComparer<(string File, string Label)>
{
	public static readonly IrIndexKeyComparer Instance = new();
	private IrIndexKeyComparer() { }
	public bool Equals((string File, string Label) x, (string File, string Label) y) =>
		string.Equals(x.File, y.File, Config.Config.SCIgnoreCase) && Config.Config.StrComper.Equals(x.Label, y.Label);
	public int GetHashCode((string File, string Label) obj) =>
		HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.File), Config.Config.StrComper.GetHashCode(obj.Label));
}

/// <summary>
/// b2-irdiskcache（B 线 B2，阶段① 函数粒度 blob 库）— 浅 IR 磁盘缓存的格式 / 读写 / 失效判定。
///
/// v1 IR 深度 = 函数粒度文本 blob：每个函数缓存「@ 签名行 + # 签名行 + 函数体启用行」的原始逻辑行文本
///（行号 + 文本，均为 <c>EraStreamReader.ReadEnabledLine</c> 之后的启用行，即已过滤 [ 预处理、已 rename、
/// 已做行连接）。物化时以 blob 文本流作为 <c>ReparseFunction</c> 的输入，复用既有单函数解析循环 —— 第二次
/// 启动不重读源文件、不重扫全文件。
///
/// 文件布局（单文件 + 尾部内部索引）：
/// <code>
/// [文件头] [blob 区（每函数一个 blob，按文件加载序）] [名字索引区]
/// </code>
/// 启动只读「文件头 + 索引区」（索引区整体读入驻留，几 MB 量级）；blob 本体（几十 MB）启动完全不读，物化时按需
/// seek 读单个 blob。详见 design.md §4。
///
/// 失效 = 全量失效（v1）：引擎版本键 或 内容指纹（文件集合 + 解析相关配置）任一不匹配 → 整库判废 → 回退 B1 源路径
/// → 加载收尾重建。详见 design.md §6。
/// </summary>
internal static class ErbIrCache
{
	private const string Magic = "ERIR";
	/// <summary>IR 布局版本（IR 布局演进时自增，独立于引擎版本）。</summary>
	private const int FormatVersion = 1;
	/// <summary>边界硬上限（design.md §8，v1 文本 IR 体积 &lt; 源总大小，估 &lt;80MB；eraTW 实测后微调）。超限即判废不生成。</summary>
	internal const long MaxIrFileBytes = 200L * 1024 * 1024;

	// ===== 失效：引擎版本键 / 内容指纹 =====

	private static long? _engineVersionKeyCache;
	/// <summary>引擎版本键（哈希）；升级引擎 → 值变 → IR 判废。</summary>
	internal static long EngineVersionKey =>
		_engineVersionKeyCache ??= Fnv1a64(AssemblyData.EmueraVersionText);

	/// <summary>
	/// 生成期文件集合的一条指纹输入。仅用逻辑名 + 字节长度 + 修改时间戳（mtime Tick 精度），绝不读文件内容 ——
	/// 全内容哈希 = 每启动重读 104MB，吃掉整个 IR 收益（design.md §6）。
	/// </summary>
	internal readonly record struct FileStamp(string Logical, long Length, long MtimeTicks);

	/// <summary>
	/// 内容指纹 = FNV-1a 滚动哈希（文件集合 + 解析相关配置集 + Rename 宏表）。文件集合按 pending 序（文件加载序）
	/// 输入，保证同集合同序 → 同指纹。
	/// </summary>
	internal static long ComputeContentFingerprint(IReadOnlyList<(string Logical, string Physical)> files,
		IReadOnlyDictionary<string, string>? renameDic)
	{
		ulong h = Fnv1a64Init;
		// 文件集合：每文件（逻辑名, 长度, mtime）——脚本增删/改名/改动 → 指纹变化
		foreach (var (logical, physical) in files)
		{
			long len = 0, mtime = 0;
			SafCompat.TryGetFileStats(physical, out len, out mtime); // content URI（无 stat）→ 按 0 降级
			h = Fnv1a(h, logical);
			h = Fnv1a(h, len.ToString(System.Globalization.CultureInfo.InvariantCulture));
			h = Fnv1a(h, mtime.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}
		// 解析相关配置集（design.md §6 白名单）：影响预解析/函数签名/比较的配置。
		h = Fnv1a(h, Config.Config.IgnoreCase ? "IC1" : "IC0");
		h = Fnv1a(h, Config.Config.UseRenameFile ? "RF1" : "RF0");
		h = Fnv1a(h, Config.Config.UseReplaceFile ? "RP1" : "RP0");
		h = Fnv1a(h, ((int)Config.Config.ReduceArgumentOnLoad).ToString(System.Globalization.CultureInfo.InvariantCulture));
		h = Fnv1a(h, Config.Config.NeedReduceArgumentOnLoad ? "NR1" : "NR0");
		h = Fnv1a(h, Config.Config.SystemIgnoreTripleSymbol ? "T3" : "T0");
		// Rename 宏表内容（_Replace/_Rename 预处理依赖；按 key 排序保证同表同序）。
		if (renameDic is { Count: > 0 })
		{
			var keys = new List<string>(renameDic.Keys);
			keys.Sort(StringComparer.Ordinal);
			foreach (var k in keys)
			{
				h = Fnv1a(h, k);
				h = Fnv1a(h, renameDic[k]);
			}
		}
		return unchecked((long)h);
	}

	// ===== 读取 / 校验 =====

	/// <summary>
	/// 校验 IR 并建索引。任一校验失败返回 false（回退 B1 源路径，整轮回退）。启动只读「头 + 索引区」，blob 区不读。
	/// </summary>
	internal static bool TryLoad(string path, long contentFingerprint, out Dictionary<(string File, string Label), IrIndexEntry> index)
	{
		index = [];
		long engineKey = EngineVersionKey;
		try
		{
			using var stream = SafCompat.OpenRead(path);
			if (stream == null)
				return false;
			using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
			ReadHeader(r, out int formatVersion, out long engineKeyOnDisk, out long fingerprintOnDisk, out int functionCount, out int indexOffset, out int indexLength, out int blobBytes);
			if (formatVersion != FormatVersion)
				return false;
			if (engineKeyOnDisk != engineKey)
				return false;
			if (fingerprintOnDisk != contentFingerprint)
				return false;
			if (blobBytes > MaxIrFileBytes)
				return false;
			// seek 到索引区（blob 区跳过，不读）。首字段 = 函数数（int32），随后 funcCount 条 (file,label)→offset 记录。
			stream.Seek(indexOffset, SeekOrigin.Begin);
			if (r.ReadInt32() != functionCount)
				return false;
			var entries = new Dictionary<(string, string), IrIndexEntry>(functionCount, IrIndexKeyComparer.Instance);
			for (int i = 0; i < functionCount; i++)
			{
				var e = ReadIndexEntry(r);
				entries[(e.File, e.Label)] = e;
			}
			if (stream.Position != indexOffset + indexLength)
				return false;
			index = entries;
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>读单个函数 blob：签名文本行（@ 行 + # 行 + 函数体启用行）。损坏 → 抛（调用方转回退/占位）。</summary>
	internal static IrBlob ReadBlob(string path, in IrIndexEntry entry)
	{
		var buffer = new byte[entry.BlobLength];
		using (var stream = SafCompat.OpenRead(path) ?? throw new IOException("IR file missing"))
		{
			stream.Seek(entry.BlobOffset, SeekOrigin.Begin);
			int read = 0;
			while (read < buffer.Length)
			{
				int n = stream.Read(buffer, read, buffer.Length - read);
				if (n <= 0)
					throw new IOException("IR blob truncated");
				read += n;
			}
		}
		using var ms = new MemoryStream(buffer, writable: false);
		using var r = new BinaryReader(ms, Encoding.UTF8, leaveOpen: false);
		int lineCount = r.ReadInt32();
		var lines = new IrLine[lineCount];
		for (int i = 0; i < lineCount; i++)
		{
			int lineNo = r.ReadInt32();
			string text = r.ReadString();
			lines[i] = new IrLine(lineNo, text);
		}
		return new IrBlob("", 0, false, null, lines);
	}

	// ===== 生成 / 原子写 =====

	/// <summary>
	/// 组装单文件 IR 并原子写（tmp + fsync + rename）。失败静默（进程继续走 B1 路径，下次再试）。
	/// <paramref name="blobsByFile"/>：按文件加载序的（逻辑文件名 → 该文件内按出现序的函数 blob）。
	/// </summary>
	internal static void Write(string path, long contentFingerprint,
		IReadOnlyList<(string Logical, List<IrBlob> Blobs)> blobsByFile)
	{
		try
		{
			long engineKey = EngineVersionKey;
			using var ms = new MemoryStream(64 * 1024);
			using (var w = new BinaryWriter(ms, Encoding.UTF8, leaveOpen: true))
			{
				// 占位文件头（indexOffset/indexLength/blobBytes 最后回填；WriteHeader 恰好 40 字节）
				w.Write(new byte[40]);
				// [blob 区]：逐文件、逐函数写 blob，记录偏移
				int blobBytes = 0;
				var offsets = new List<(long Offset, int Length, string File, string Label, int LineNo, bool Invalid, string? ErrMes)>();
				foreach (var (logical, blobs) in blobsByFile)
				{
					for (int i = 0; i < blobs.Count; i++)
					{
						var b = blobs[i];
						long off = ms.Position;
						WriteBlob(w, b);
						int len = checked((int)(ms.Position - off));
						offsets.Add((off, len, logical, b.Label, b.LineNo, b.Invalid, b.InvalidErrMes));
						blobBytes += len;
					}
				}
				// [索引区]
				int indexOffset = checked((int)ms.Position);
				w.Write(offsets.Count);
				foreach (var (off, len, file, label, lineNo, invalid, err) in offsets)
				{
					w.Write(label);
					w.Write(file);
					w.Write(lineNo);
					w.Write(invalid);
					if (invalid)
						w.Write(err ?? "");
					w.Write(off);
					w.Write(len);
				}
				int indexLength = checked((int)ms.Position - indexOffset);
				// 回填文件头
				ms.Seek(0, SeekOrigin.Begin);
				WriteHeader(w, functionCount: offsets.Count, indexOffset, indexLength, blobBytes, contentFingerprint, engineKey);
			}
			if (ms.Length > MaxIrFileBytes)
			{
				EmueraLog.Info("ErbIrCache", $"IR skipped: size {ms.Length} exceeds bound {MaxIrFileBytes}");
				return;
			}
			AtomicWrite(path, ms.ToArray());
		}
		catch (Exception ex)
		{
			// IR 生成失败（磁盘满/IO 异常）→ 静默放弃（进程继续走 B1 路径，下次再试），不抛新异常。
			EmueraLog.Warn("ErbIrCache", $"IR write skipped: {ex.GetType().Name}: {ex.Message}");
		}
	}

	private static void AtomicWrite(string path, byte[] bytes)
	{
		string tmp = path + ".tmp";
		using (var stream = SafCompat.OpenWrite(tmp))
		{
			stream.Write(bytes, 0, bytes.Length);
			stream.Flush();
#if !HEADLESS
			stream.Flush(flushToDisk: true); // fsync
#endif
		}
		// 桌面文件系统：原子替换（delete-dest + rename）；content URI 无原子 rename → 降级删除+重写。
		if (!SafCompat.TryAtomicReplaceLocal(tmp, path))
		{
			SafCompat.Delete(path);
			using var s = SafCompat.OpenWrite(path);
			s.Write(bytes, 0, bytes.Length);
		}
	}

	// ===== 序列化原语 =====

	private static void WriteHeader(BinaryWriter w, int functionCount, int indexOffset, int indexLength, int blobBytes, long contentFingerprint, long engineKey)
	{
		w.Write(Magic[0]); w.Write(Magic[1]); w.Write(Magic[2]); w.Write(Magic[3]);
		w.Write(FormatVersion);
		w.Write(engineKey);
		w.Write(contentFingerprint);
		w.Write(functionCount);
		w.Write(indexOffset);
		w.Write(indexLength);
		w.Write(blobBytes);
	}

	private static void ReadHeader(BinaryReader r, out int formatVersion, out long engineKey, out long fingerprint, out int functionCount, out int indexOffset, out int indexLength, out int blobBytes)
	{
		var mag = new char[4];
		mag[0] = r.ReadChar(); mag[1] = r.ReadChar(); mag[2] = r.ReadChar(); mag[3] = r.ReadChar();
		if (mag[0] != Magic[0] || mag[1] != Magic[1] || mag[2] != Magic[2] || mag[3] != Magic[3])
			throw new InvalidDataException("bad magic");
		formatVersion = r.ReadInt32();
		engineKey = r.ReadInt64();
		fingerprint = r.ReadInt64();
		functionCount = r.ReadInt32();
		indexOffset = r.ReadInt32();
		indexLength = r.ReadInt32();
		blobBytes = r.ReadInt32();
	}

	private static void WriteBlob(BinaryWriter w, IrBlob b)
	{
		w.Write(b.Lines.Length);
		foreach (var l in b.Lines)
		{
			w.Write(l.LineNo);
			w.Write(l.Text);
		}
	}

	private static IrIndexEntry ReadIndexEntry(BinaryReader r)
	{
		string label = r.ReadString();
		string file = r.ReadString();
		int lineNo = r.ReadInt32();
		bool invalid = r.ReadBoolean();
		string? errMes = invalid ? r.ReadString() : null;
		long offset = r.ReadInt64();
		int length = r.ReadInt32();
		return new IrIndexEntry(file, label, lineNo, invalid, errMes, offset, length);
	}

	// ===== FNV-1a 64 =====

	private const ulong Fnv1a64Basis = 14695981039346656037UL;
	private const ulong Fnv1a64Prime = 1099511628211UL;
	private const ulong Fnv1a64Init = 14695981039346656037UL;
	private static ulong Fnv1a(ulong h, string s)
	{
		foreach (char c in s)
		{
			h ^= c;
			h *= Fnv1a64Prime;
		}
		return h;
	}
	private static long Fnv1a64(string s)
	{
		ulong h = Fnv1a64Basis;
		foreach (char c in s)
		{
			h ^= c;
			h *= Fnv1a64Prime;
		}
		return unchecked((long)h);
	}
}

/// <summary>索引区条目。键 = (File, Label)。BlobOffset/BlobLength 指向 blob 区内本函数的文本行。</summary>
internal readonly record struct IrIndexEntry(string File, string Label, int LineNo, bool Invalid, string? ErrMes, long BlobOffset, long BlobLength);

/// <summary>单个函数 blob：@{＋#＋函数体启用行，逐行 (原文件行号, 文本)。</summary>
internal readonly record struct IrLine(int LineNo, string Text);

/// <summary>
/// 生成期收集的单个函数 IR blob：Label / @ 行行号 / 是否非法 @（InvalidLabelLine）+ 对应报错文案 / 启用行序列。
/// </summary>
internal readonly record struct IrBlob(string Label, int LineNo, bool Invalid, string? InvalidErrMes, IrLine[] Lines)
{
	internal static IrBlob Valid(string label, int lineNo, IEnumerable<IrLine> lines)
		=> new(label, lineNo, false, null, ToArray(lines));
	internal static IrBlob InvalidFunc(string label, int lineNo, string? errMes)
		=> new(label, lineNo, true, errMes, []);

	private static IrLine[] ToArray(IEnumerable<IrLine> lines)
	{
		// 惰性构造：多数函数体行数少，List→数组即可；启动内存 O(单文件)。
		var list = new List<IrLine>();
		foreach (var l in lines)
			list.Add(l);
		return list.ToArray();
	}
}