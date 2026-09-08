using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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
/// 文件布局（v2，单文件 + 尾部内部索引 + firstDir 清单区）：
/// <code>
/// [文件头] [blob 区（每函数一个 blob，按文件加载序）] [名字索引区] [firstDir 清单区]
/// </code>
/// 启动只读「文件头 + 索引区」（索引区整体读入驻留，几 MB 量级）；blob 本体（几十 MB）启动完全不读，物化时按需
/// seek 读单个 blob。详见 design.md §4。
///
/// v2（diagnostic-knobs/issues/12 M4）：新增 firstDir 清单区——建库轮 <see cref="ErbLoader.BuildPendingList"/> 扫描到的
/// <c>*#*</c> 目录相对名有序清单写盘，命中轮读本地替代 SAF 通配扫描（真机冷树 ≈15.9s/进程）。
/// count=0 是合法状态（无 firstDir 的游戏照常受益——扫描成本恰恰来自「零命中仍付全款」）；
/// 「有无清单」以 formatVersion==2 为准。清单进内容指纹（集合语义）：任何 <c>*#*</c> 目录增删 → fp 变 →
/// miss → 命中轮回退全扫重建，自愈。与 issues/08（blob `$` 清单）同 v2 窗口发布，用户只付一次整库重建。
///
/// 失效 = 全量失效（v1）：引擎版本键 或 内容指纹（文件集合 + 解析相关配置）任一不匹配 → 整库判废 → 回退 B1 源路径
/// → 加载收尾重建。详见 design.md §6。
/// </summary>
internal static class ErbIrCache
{
	private const string Magic = "ERIR";
	/// <summary>IR 布局版本（IR 布局演进时自增，独立于引擎版本）。v2 = issues/12 M4 firstDir 清单区。</summary>
	private const int FormatVersion = 2;
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
	/// 内容指纹 = FNV-1a 滚动哈希（文件集合 + firstDir 清单 + 解析相关配置集 + Rename 宏表）。
	/// 文件集合先按（逻辑名, 物理路径）排序再哈希——指纹语义是「集合 + 配置」，与加载序无关；
	/// SAF 上 pending 序 = Preload 读取完成序（并行 degree&gt;1 时逐轮随机），顺序直接进哈希曾使
	/// 指纹逐轮漂移、IR 永远 miss + 每轮白付整库重建写（2026-09-04 模拟器实测，回归钉
	/// ErbIrCacheTests.Fingerprint_is_invariant_under_input_order）。
	/// v2（issues/12 M4）：firstDir 清单同样按集合语义（Ordinal 排序）进哈希——任何 <c>*#*</c>
	/// 目录增删 → fp 变 → 命中轮 miss → 全扫重建自愈。建库轮写入序（权威加载序）不进哈希。
	/// </summary>
	internal static long ComputeContentFingerprint(IReadOnlyList<(string Logical, string Physical)> files,
		IReadOnlyDictionary<string, string>? renameDic, IReadOnlyList<string>? firstDirs = null)
	{
		ulong h = Fnv1a64Init;
		// 文件集合：每文件（逻辑名, 长度, mtime）——脚本增删/改名/改动 → 指纹变化
		foreach (var (logical, physical) in files.OrderBy(f => f.Logical, StringComparer.Ordinal)
			.ThenBy(f => f.Physical, StringComparer.Ordinal))
		{
			long len = 0, mtime = 0;
			SafCompat.TryGetFileStats(physical, out len, out mtime); // content URI（无 stat）→ 按 0 降级
			h = Fnv1a(h, logical);
			h = Fnv1a(h, len.ToString(System.Globalization.CultureInfo.InvariantCulture));
			h = Fnv1a(h, mtime.ToString(System.Globalization.CultureInfo.InvariantCulture));
		}
		// firstDir 清单（issues/12 M4）：先哈希条数再逐条（消 ["ab"] 与 ["a","b"] 的拼接歧义）。
		// null（v1 语义/无清单）与空清单（v2 确认无 *#* 目录）语义不同，分别进哈希。
		if (firstDirs != null)
		{
			h = Fnv1a(h, $"FD{firstDirs.Count}");
			var dirs = new List<string>(firstDirs);
			dirs.Sort(StringComparer.Ordinal);
			foreach (var d in dirs)
				h = Fnv1a(h, d);
		}
		// 解析相关配置集（design.md §6 白名单）：影响预解析/函数签名/比较的配置。
		h = Fnv1a(h, Config.Config.IgnoreCase ? "IC1" : "IC0");
		h = Fnv1a(h, Config.Config.UseRenameFile ? "RF1" : "RF0");
		h = Fnv1a(h, Config.Config.UseReplaceFile ? "RP1" : "RP0");
		h = Fnv1a(h, ((int)Config.Config.ReduceArgumentOnLoad).ToString(System.Globalization.CultureInfo.InvariantCulture));
		h = Fnv1a(h, Config.Config.NeedReduceArgumentOnLoad ? "NR1" : "NR0");
		h = Fnv1a(h, Config.Config.SystemIgnoreTripleSymbol ? "T3" : "T0");
		// SearchSubdirectory（issues/12 M4 起）：firstDir 清单是扫描结果的存储形态，其口径受本配置影响
		//（TopDirectoryOnly 只扫 erb 根级 *#*）——不改则清单跨配置陈旧、firstDir 优先序漂移。
		h = Fnv1a(h, Config.Config.SearchSubdirectory ? "SS1" : "SS0");
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

	// ===== 方案 B（diagnostic-knobs/issues/05）：content-URI（SAF）游戏缓存搬迁到 app 私有存储 + 孤儿治理 =====
	// SAF 流不可 seek：IR 缓存留在游戏目录时 ReadBlob 每函数触发整库缓冲重读（31.5MB × 2724 次 ≈ 143s+），
	// 且 SAF 无原子替换（delete+copy，崩溃窗口留半成品）。正解 = 分平台放置：
	//   - content-URI 游戏 → <AppDataPaths.Directory>/IRCache/<gameKey>/era_ir.dat（真实文件系统，可 seek + 原子替换）
	//   - 本地路径（桌面 MAUI / CLI / Server）→ <游戏目录>/_IRCache/era_ir.dat 现状（可 seek/可原子替换，缓存随游戏目录）
	// gameKey = 归一化 ExeDir 的 Fnv1a64，多游戏隔离（硬需求：appdata 是跨游戏共享目录）。

	/// <summary>IR 缓存根目录（app 私有存储下）。仅 content-URI 游戏使用；本地路径缓存仍在游戏目录。</summary>
	internal static string IrCacheRoot => Path.Combine(AppDataPaths.Directory, "IRCache");

	/// <summary>归一化 ExeDir 作缓存键输入：去尾部目录分隔符；本地路径取全路径（content URI 保持原样参与哈希）。</summary>
	private static string NormalizeExeDirForKey(string exeDir)
	{
		var s = exeDir.TrimEnd('/', '\\');
		if (!SafPath.IsContentUri(s))
		{
			try { s = Path.GetFullPath(s).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
			catch { /* 非法路径按原样参与哈希 */ }
		}
		else
		{
			// issues/13 M5：content URI 统一按**解码形态**参与哈希——同一游戏的目录串在不同来源间
			// 编码形态不一致（引擎侧 SAF 树 URI 保持 %3A/%2F 编码；扫库侧 rootDir/拼接路径出现裸
			// `:` `/`），编码形态哈希使「加载侧缓存键」与「扫库侧探测键」分叉：TryGetCacheInfo 恒
			// false（徽章假阴）、TryDeleteCache 删空。解码后两侧收敛（%2F 解为 `/` 恰好同构）。
			// 键只用于哈希不作路径，双解码歧义（%252F 类）概率可忽略。
			try { s = Uri.UnescapeDataString(s); }
			catch { /* 残留编码形态按原样参与哈希 */ }
		}
		return s;
	}

	/// <summary>游戏目录的缓存键（Fnv1a64 十六进制）。content URI 与本地路径统一语义；多游戏隔离。</summary>
	internal static string ComputeGameKey(string exeDir)
		=> Fnv1a64(NormalizeExeDirForKey(exeDir)).ToString("x16", System.Globalization.CultureInfo.InvariantCulture);

	/// <summary>
	/// 方案 B：IR 缓存文件路径。content-URI（SAF）→ <c>AppDataPaths/IRCache/&lt;gameKey&gt;/era_ir.dat</c>
	/// （真实文件系统可 seek）；本地路径 → <c>&lt;游戏目录&gt;/_IRCache/era_ir.dat</c>（现状）。
	/// </summary>
	internal static string GetIrCachePath(string exeDir)
	{
		if (SafPath.IsContentUri(exeDir))
			return Path.Combine(IrCacheRoot, ComputeGameKey(exeDir), "era_ir.dat");
		string dir = SafCompat.ResolveSubPath(exeDir, "_IRCache");
		return SafCompat.CombinePath(dir, "era_ir.dat");
	}

	/// <summary>
	/// 存在性提示（非有效性承诺）：IR 缓存文件存在 + 大小 + 构建时间（era_ir.dat mtime）。
	/// 指纹有效性要等加载期才能算，库扫描期不算也不该算（design/issues/05「提示语义 = 存在性而非有效性」）。
	/// </summary>
	internal static bool TryGetCacheInfo(string exeDir, out long bytes, out DateTime lastWriteUtc)
	{
		bytes = 0;
		lastWriteUtc = default;
		if (string.IsNullOrEmpty(exeDir))
			return false;
		string path = GetIrCachePath(exeDir);
		if (!SafCompat.TryGetFileStats(path, out bytes, out long mtimeTicks) || bytes <= 0)
			return false;
		lastWriteUtc = new DateTime(mtimeTicks, DateTimeKind.Utc);
		return true;
	}

	// ── issues/13 M5：脚本清单 sidecar（era_ir.list）───────────────────────────────────────
	// 设计（issues/13 方案 §1 + 实施修订）：IR 旁文件存两个清单——
	// ① 建库轮最终 pending 的（逻辑名, 相对根名）有序对：命中轮 BuildPendingList 回放（权威合并序，
	//    零 SAF IPC——砍掉 15.6s 冷 TreeEnum）。逻辑名与相对根名分开存：firstDir 文件逻辑名是相对
	//    firstDir 的（≠ 相对 ERB 根），回放时物理路径只能从相对根名重建。
	// ② ERB 根全树（目标扩展名过滤后）相对名清单：命中轮播种 Preload 树枚举 memo（实施修订——
	//    IndexTree 之外 ErhLoader 的 .erh 枚举也走同一 memo，不播种则 ERH 段反而冷枚举 15.6s）。
	// 选 sidecar 而非 IR v3 加区：零格式 churn（v2 窗口留给 10-B）；重扫 = 删文件；陈旧 sidecar 算出的
	// fp 必 ≠ IR 存的 fp → miss 重建重写（「清单与 IR 不同步」唯一可检形态，入 IR 则恒真不可检）。
	// 物理路径不入文件（URI 基座跨重授可能变）：命中轮经 CombinePath(root, rel) 重建。
	// 新鲜度 = 路线 2（显式失效，用户是权威）：SAF 上 fp 本就内容盲，fp 自证恒真是设计语义。
	// 布局：[int32 pendingCount]×(UTF8 逻辑名, UTF8 相对根名) [int32 treeCount]×UTF8 相对根名。
	// 缺失/损坏/越界 → false（调用方回退真实枚举，安全方向；仅 SAF 游戏读写，本地路径不生成）。

	/// <summary>sidecar 路径：与 era_ir.dat 同目录（SAF 游戏 = appdata 真实文件系统，可原子替换）。</summary>
	internal static string GetFileListPath(string irCachePath)
		=> Path.Combine(Path.GetDirectoryName(irCachePath)!, "era_ir.list");

	/// <summary>sidecar 条数上界（防损坏文件把 List 撑爆；真实规模 ~2.5K）。</summary>
	private const int FileListMaxEntries = 1_000_000;

	internal static bool TryReadFileList(string irCachePath,
		out List<(string Logical, string RelToRoot)> pendingRels, out List<string> treeRels)
	{
		pendingRels = [];
		treeRels = [];
		try
		{
			var path = GetFileListPath(irCachePath);
			if (!File.Exists(path))
				return false;
			using var r = new BinaryReader(File.OpenRead(path));
			int pendingCount = r.ReadInt32();
			if (pendingCount is < 0 or > FileListMaxEntries)
				return false;
			var pending = new List<(string, string)>(Math.Min(pendingCount, 1 << 20));
			for (int i = 0; i < pendingCount; i++)
			{
				string logical = r.ReadString();
				string relToRoot = r.ReadString();
				if (logical.Length == 0 || relToRoot.Length == 0)
					return false;
				pending.Add((logical, relToRoot));
			}
			int treeCount = r.ReadInt32();
			if (treeCount is < 0 or > FileListMaxEntries)
				return false;
			var tree = new List<string>(Math.Min(treeCount, 1 << 20));
			for (int i = 0; i < treeCount; i++)
			{
				string rel = r.ReadString();
				if (rel.Length == 0)
					return false;
				tree.Add(rel);
			}
			if (r.BaseStream.Position != r.BaseStream.Length)
				return false; // 尾部杂字节 = 半截/损坏
			pendingRels = pending;
			treeRels = tree;
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>写 sidecar（tmp + 原子替换；同目录为真实文件系统）。失败返回 false（无 sidecar = 命中轮回退枚举，无害）。</summary>
	internal static bool TryWriteFileList(string irCachePath,
		IReadOnlyList<(string Logical, string RelToRoot)> pendingRels, IReadOnlyList<string> treeRels)
	{
		try
		{
			var path = GetFileListPath(irCachePath);
			var tmp = path + ".tmp";
			using (var w = new BinaryWriter(File.Create(tmp)))
			{
				w.Write(pendingRels.Count);
				foreach (var (logical, rel) in pendingRels)
				{
					w.Write(logical);
					w.Write(rel);
				}
				w.Write(treeRels.Count);
				foreach (var rel in treeRels)
					w.Write(rel);
			}
			if (!SafCompat.TryAtomicReplaceLocal(tmp, path))
			{
				try { File.Delete(tmp); } catch { /* 残留 tmp 无害（下次写覆盖） */ }
				return false;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>
	/// issues/13 M5：重扫按钮后端——删除本游戏的 IR 缓存与清单 sidecar，下次加载强制全扫重建。
	/// 双布局：SAF（appdata IRCache/&lt;gameKey&gt;/）与本地（&lt;游戏目录&gt;/_IRCache/）。
	/// 尽力而为：删除失败（如 IR 读句柄被运行中的会话持有）返回 false，调用方提示关闭游戏后重试；
	/// 部分删除安全——缺 sidecar = 命中轮回退枚举（fp 照旧按枚举清单算），等价于 M4 前现状。
	/// </summary>
	internal static bool TryDeleteCache(string exeDir)
	{
		if (string.IsNullOrEmpty(exeDir))
			return false;
		bool ok = true;
		try
		{
			if (SafPath.IsContentUri(exeDir))
			{
				string irPath = GetIrCachePath(exeDir);
				string dir = Path.GetDirectoryName(irPath)!;
				foreach (var name in new[] { Path.GetFileName(irPath), Path.GetFileName(GetFileListPath(irPath)) })
				{
					try { var p = Path.Combine(dir, name); if (File.Exists(p)) File.Delete(p); }
					catch { ok = false; }
				}
			}
			else
			{
				string dir = SafCompat.ResolveSubPath(exeDir, "_IRCache");
				foreach (var name in new[] { "era_ir.dat", "era_ir.list" })
				{
					try
					{
						var p = SafCompat.CombinePath(dir, name);
						if (File.Exists(p))
							File.Delete(p);
					}
					catch { ok = false; }
				}
			}
			return ok;
		}
		catch
		{
			return false;
		}
	}

	/// <summary>孤儿清理宽限期：孤儿 key 目录 mtime 在此期限内不删（SD 卡暂拔/权限暂失的游戏回归时免白付重建）。</summary>
	private static readonly TimeSpan OrphanGracePeriod = TimeSpan.FromDays(30);

	/// <summary>
	/// 库扫描时顺带清理 appdata IRCache 下的孤儿 key 目录（不在当前库 keys 集合内的）。
	/// 宽限期 + 活动会话（<paramref name="activeExeDir"/>，Windows 上读句柄未关时删除会失败）保护；
	/// 删除清单落日志自证。壳层只调本 API，不懂目录布局；gameKey 归一化逻辑单点在 Core。
	/// </summary>
	internal static void CleanupOrphans(IReadOnlyCollection<string> currentExeDirs, string? activeExeDir)
	{
		try
		{
			var keepKeys = new HashSet<string>(StringComparer.Ordinal);
			foreach (var dir in currentExeDirs)
			{
				if (!string.IsNullOrEmpty(dir))
					keepKeys.Add(ComputeGameKey(dir));
			}
			if (!string.IsNullOrEmpty(activeExeDir))
				keepKeys.Add(ComputeGameKey(activeExeDir));

			if (!Directory.Exists(IrCacheRoot))
				return;
			var removed = new List<string>();
			foreach (var dir in Directory.EnumerateDirectories(IrCacheRoot))
			{
				string key = Path.GetFileName(dir);
				if (key.Length == 0 || keepKeys.Contains(key))
					continue;
				try
				{
					if (DateTime.UtcNow - Directory.GetLastWriteTimeUtc(dir) < OrphanGracePeriod)
						continue; // 宽限期内不删
				}
				catch { continue; }
				try
				{
					Directory.Delete(dir, recursive: true);
					removed.Add(key);
				}
				catch (Exception ex)
				{
					EmueraLog.Warn("ErbIrCache", $"IR orphan cleanup failed for {key}: {ex.GetType().Name}: {ex.Message}");
				}
			}
			if (removed.Count > 0)
				EmueraLog.Info("ErbIrCache", $"IR orphan cleanup removed {removed.Count}: {string.Join(",", removed)}");
		}
		catch (Exception ex)
		{
			EmueraLog.Warn("ErbIrCache", $"IR orphan cleanup skipped: {ex.GetType().Name}: {ex.Message}");
		}
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
			ReadHeader(r, out int formatVersion, out long engineKeyOnDisk, out long fingerprintOnDisk, out int functionCount, out int indexOffset, out int indexLength, out int blobBytes, out _, out _);
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

	/// <summary>
	/// 读单个函数 blob：签名文本行（@ 行 + # 行 + 函数体启用行）。损坏 → 抛（调用方转回退/占位）。
	/// 走调用方传入的流（hit 会话常驻句柄 seek+局部读；content-URI 镜像在 app 私有存储，真实文件系统可 seek）。
	/// </summary>
	internal static IrBlob ReadBlob(Stream stream, in IrIndexEntry entry)
	{
		var buffer = new byte[entry.BlobLength];
		stream.Seek(entry.BlobOffset, SeekOrigin.Begin);
		int read = 0;
		while (read < buffer.Length)
		{
			int n = stream.Read(buffer, read, buffer.Length - read);
			if (n <= 0)
				throw new IOException("IR blob truncated");
			read += n;
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

	// ===== 生成 / 原子写（增量流式，内存 O(单文件大小)）=====

	/// <summary>写单个函数 blob 的序列化原语（blob 区 / 索引区共用）。</summary>
	private static void WriteBlob(BinaryWriter w, IrBlob b)
	{
		w.Write(b.Lines.Length);
		foreach (var l in b.Lines)
		{
			w.Write(l.LineNo);
			w.Write(l.Text);
		}
	}

	/// <summary>
	/// 增量 IR 写器（design §5.1「blob 缓冲按文件批量落盘」）：loader 生成期逐文件调 <see cref="AppendFile"/>，
	/// 单文件 blob 文本写完即由调用方释放，峰值内存 O(单文件大小) 而非 O(IR 总大小)；索引区/文件头在
	/// <see cref="Complete"/> 收尾回填，全程不把整份 IR 组装进内存（消除了旧 Write 的整库 MemoryStream + ToArray 两倍峰值）。
	/// 失败静默（磁盘满/IO 异常）→ 删 tmp、不产生最终文件；进程继续走 B1 源路径，下次再试（design §5.1 异常策略）。
	/// </summary>
	internal sealed class IrCacheWriter : IDisposable
	{
		/// <summary>文件头固定占位长度（对齐 <see cref="ErbIrCache.WriteHeader"/> 恰好 48 字节；v2 = v1 的 40 + firstDir 两字段 8）。</summary>
		private const int HeaderBytes = 48;
		private readonly string _path;
		private readonly string _tmpPath;
		private Stream? _stream;
		private BinaryWriter? _w;
		private bool _poisoned;
		private int _blobBytes;
		private int _functionCount;
		/// <summary>索引偏移表（每个函数一条；67k 量级 ≈ 几 MB，驻留量与 IR 索引同量级，可接受）。</summary>
		private readonly List<(long Offset, int Length, string File, string Label, int LineNo, bool Invalid, string? ErrMes)> _offsets = [];

		internal IrCacheWriter(string path)
		{
			_path = path;
			_tmpPath = path + ".tmp";
			_stream = SafCompat.OpenWrite(_tmpPath);
			_w = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
			_w.Write(new byte[HeaderBytes]); // 文件头占位（Complete 回填）
		}

		/// <summary>写一个文件的全部函数 blob（按出现序），记录索引偏移。写毕由调用方清空该文件的 blob 缓冲以释放内存。</summary>
		internal void AppendFile(string logical, IReadOnlyList<IrBlob> blobs)
		{
			if (_w == null || blobs.Count == 0)
				return;
			try
			{
				for (int i = 0; i < blobs.Count; i++)
				{
					var b = blobs[i];
					long off = _stream!.Position;
					WriteBlob(_w, b);
					int len = checked((int)(_stream.Position - off));
					_offsets.Add((off, len, logical, b.Label, b.LineNo, b.Invalid, b.InvalidErrMes));
					_blobBytes += len;
					_functionCount++;
				}
			}
			catch (Exception ex)
			{
				// 写盘失败（磁盘满等）→ 毒化：停写、清 tmp；加载不受影响（B1 已跑完）。不在此抛新异常。
				EmueraLog.Warn("ErbIrCache", $"IR append skipped: {ex.GetType().Name}: {ex.Message}");
				Poison();
			}
		}

		/// <summary>
		/// 写索引区 + firstDir 清单区（issues/12 M4，<paramref name="firstDirs"/> = 建库轮扫描到的相对名
		/// 有序清单；null/空清单均写 count=0 区）+ 回填文件头 + fsync + 原子替换。
		/// 超边界/失败 → 清 tmp、不产生最终文件，返回 false。
		/// </summary>
		internal bool Complete(long contentFingerprint, IReadOnlyList<string>? firstDirs = null)
		{
			if (_poisoned)
			{
				Clear();
				return false;
			}
			try
			{
				int indexOffset = checked((int)_stream!.Position);
				_w!.Write(_functionCount);
				foreach (var (off, len, file, label, lineNo, invalid, err) in _offsets)
				{
					_w.Write(label);
					_w.Write(file);
					_w.Write(lineNo);
					_w.Write(invalid);
					if (invalid)
						_w.Write(err ?? "");
					_w.Write(off);
					_w.Write(len);
				}
				int indexLength = checked((int)_stream.Position - indexOffset);
				// firstDir 清单区（v2）：int32 条数 + 逐条相对名。count=0 照常写区（占位语义：
				// 命中轮读侧凭 formatVersion==2 判「有清单」，空清单 = 确认无 *#* 目录 → 命中轮零扫描）。
				int firstDirOffset = checked((int)_stream.Position);
				int firstDirCount = firstDirs?.Count ?? 0;
				_w!.Write(firstDirCount);
				for (int i = 0; i < firstDirCount; i++)
					_w.Write(firstDirs![i]);
				int firstDirLength = checked((int)_stream.Position - firstDirOffset);
				if (_stream.Length > MaxIrFileBytes)
				{
					EmueraLog.Info("ErbIrCache", $"IR skipped: size {_stream.Length} exceeds bound {MaxIrFileBytes}");
					Clear();
					return false;
				}
				_stream.Seek(0, SeekOrigin.Begin);
				WriteHeader(_w, _functionCount, indexOffset, indexLength, _blobBytes, firstDirOffset, firstDirLength, contentFingerprint, EngineVersionKey);
				_w.Flush();
				_stream.Flush();
#if !HEADLESS
				_stream.Flush(flushToDisk: true); // fsync
#endif
				CloseStream();
				// 桌面文件系统：原子替换（delete-dest + rename）；content URI 无原子 rename → 降级删目标 + 拷内容。
				if (!SafCompat.TryAtomicReplaceLocal(_tmpPath, _path))
				{
					var bytes = SafCompat.ReadAllBytes(_tmpPath);
					SafCompat.Delete(_path);
					if (bytes != null)
					{
						using var s = SafCompat.OpenWrite(_path);
						s.Write(bytes, 0, bytes.Length);
					}
					SafCompat.Delete(_tmpPath);
				}
				return true;
			}
			catch (Exception ex)
			{
				// IR 生成失败（磁盘满/IO 异常）→ 静默放弃（进程继续走 B1 路径，下次再试），不抛新异常。
				EmueraLog.Warn("ErbIrCache", $"IR write skipped: {ex.GetType().Name}: {ex.Message}");
				Clear();
				return false;
			}
		}

		/// <summary>放弃本次生成并清 tmp（异常路径）。</summary>
		internal void Abort() => Clear();

		private void Poison()
		{
			_poisoned = true;
			CloseStream();
			SafCompat.Delete(_tmpPath);
		}

		private void Clear()
		{
			CloseStream();
			SafCompat.Delete(_tmpPath);
		}

		private void CloseStream()
		{
			var w = _w;
			var s = _stream;
			_w = null;
			_stream = null;
			try { w?.Dispose(); } catch { /* 关闭失败不掩盖主流程 */ }
			try { s?.Dispose(); } catch { }
		}

		public void Dispose() => CloseStream();
	}

	// ===== 序列化原语 =====

	private static void WriteHeader(BinaryWriter w, int functionCount, int indexOffset, int indexLength, int blobBytes, int firstDirOffset, int firstDirLength, long contentFingerprint, long engineKey)
	{
		w.Write(Magic[0]); w.Write(Magic[1]); w.Write(Magic[2]); w.Write(Magic[3]);
		w.Write(FormatVersion);
		w.Write(engineKey);
		w.Write(contentFingerprint);
		w.Write(functionCount);
		w.Write(indexOffset);
		w.Write(indexLength);
		w.Write(blobBytes);
		w.Write(firstDirOffset); // v2（issues/12 M4）
		w.Write(firstDirLength);
	}

	private static void ReadHeader(BinaryReader r, out int formatVersion, out long engineKey, out long fingerprint, out int functionCount, out int indexOffset, out int indexLength, out int blobBytes, out int firstDirOffset, out int firstDirLength)
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
		firstDirOffset = r.ReadInt32(); // v2（issues/12 M4）
		firstDirLength = r.ReadInt32();
	}

	/// <summary>
	/// v2（issues/12 M4）：读 IR 头存的 firstDir 相对名清单（建库轮写入序 = 权威加载序）。
	/// 命中轮预判定 / LoadErbDir 在构建待载清单前调用，用清单替代 SAF <c>GetDirectories("*#*")</c>
	/// 冷树通配扫描（真机 ≈15.9s/进程；本地 appdata 文件，µs 级）。
	/// 返回 false = 无 IR / 非 v2 / 引擎版本不符 / 头或清单区损坏——调用方回退 SAF 扫描；
	/// 返回 true 且空清单 = v2 IR 确认无 <c>*#*</c> 目录（合法，命中轮零扫描）。
	/// 不校验内容指纹——清单正确性由调用方把清单喂进 <see cref="ComputeContentFingerprint"/> 后
	/// 经 <see cref="TryLoad"/> 判定，陈旧清单必然 fp miss → 全扫重建自愈。
	/// </summary>
	internal static bool TryReadFirstDirs(string path, out List<string> firstDirs)
	{
		firstDirs = [];
		try
		{
			using var stream = SafCompat.OpenRead(path);
			if (stream == null)
				return false;
			using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
			ReadHeader(r, out int formatVersion, out long engineKeyOnDisk, out _, out _, out _, out _, out _, out int firstDirOffset, out int firstDirLength);
			if (formatVersion != FormatVersion)
				return false;
			if (engineKeyOnDisk != EngineVersionKey)
				return false;
			if (firstDirOffset < 0 || firstDirLength < 4 || firstDirOffset + firstDirLength > stream.Length)
				return false;
			stream.Seek(firstDirOffset, SeekOrigin.Begin);
			int count = r.ReadInt32();
			// 边界防御：条数与区域长度必须自洽（每条至少 1 字节头），损坏即判废回退扫描
			if (count < 0 || firstDirLength - 4 < count)
				return false;
			var dirs = new List<string>(count);
			for (int i = 0; i < count; i++)
				dirs.Add(r.ReadString());
			firstDirs = dirs;
			return true;
		}
		catch
		{
			return false;
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