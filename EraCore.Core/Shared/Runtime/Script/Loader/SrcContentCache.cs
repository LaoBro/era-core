using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// issues/10 10-B：CSV/ERH 源内容跨会话磁盘缓存（era_src.dat）——命中轮免 SAF 逐文件懒读的剩余大头。
///
/// 真机口径（2026-09-08 首测）：命中轮 LoadBaseCsv 7.67s + ERH 5.63s ≈ 13.3s 读窗，324 文件 / 376KB，
/// 瓶颈是每文件 SAF OpenRead 固定开销（~40ms/文件），不是带宽。本缓存把内容（解码行）序列化到
/// app 私有存储（与 era_ir.dat 同目录），命中轮一次本地读 + 查表直接回填。
///
/// 新鲜度 = **路线 2（显式失效，用户是权威）**——与 issues/13 清单 sidecar 同一哲学（2026-09-09 用户裁定）。
/// 曾实现过逐目录 size/mtime 元数据校验（M1 探针证实的真失效检测，真机 3.2s/轮），裁定废弃：
/// 安卓端是回放设备——内容由整合者打包、用户不打 mod，暖缓存期内容变更场景≈不存在；
/// 且校验 miss 的结局（ERB 仍路线 2 命中 → 混合数据 + 缓存删除后无重建机制）与不校验相同，
/// 最终都要用户重扫，校验只是让每轮多付 3.2s。重扫按钮（<see cref="ErbIrCache.TryDeleteCache"/>）
/// 三件套同清：era_ir.dat + era_ir.list + era_src.dat。
///
/// 消费 = 轮内快照，**不回填 Preload.files**：回填会让 <see cref="Preload.GetFilesFromCache"/> 的
/// 「缓存非空但无 .erb」返回空列表而非 null，命中轮无 sidecar 时 BuildPendingList 误判「确实没有
/// .erb」→ 空 IR 事故。快照经 <see cref="EraStreamReader.OpenOnCache"/>（CSV/ERH/GAMEBASE/CHARA/
/// VarExt/.als 的唯一咽喉）在 Preload miss 后消费；生命周期 = 轮（<see cref="Preload.Clear"/> 轮首清 +
/// 重水化前自清）。
///
/// 范围：CsvDir 根树 .csv/.als + ErbDir 根树 .erh。仅 SAF 游戏（content URI 门控）——本地路径
/// 读盘本来就快（2026-09-08 用户裁定 Windows 保留现状）。
/// </summary>
internal static class SrcContentCache
{
	private const string Magic = "ERSC";
	/// <summary>v2 = 路线 2 极简格式（扁平相对名 + 行，无元数据；v1 的 size/mtime 校验形态随裁定废弃）。</summary>
	private const int FormatVersion = 2;
	/// <summary>条目 / 行数 / 体积硬上界（防损坏文件把内存撑爆；真实规模 ~324 文件 / 376KB）。</summary>
	private const int MaxEntries = 100_000;
	private const int MaxLineCount = 1_000_000;
	private const long MaxFileBytes = 64L * 1024 * 1024;

	/// <summary>CsvDir 根树可缓存扩展名（GAMEBASE/固定名 CSV/CHARA*/VarExt*/alias .als 全走 OpenOnCache 咽喉）。</summary>
	private static readonly string[] CsvRootExts = [".csv", ".als"];
	/// <summary>ErbDir 根树可缓存扩展名。</summary>
	private static readonly string[] ErbRootExts = [".erh"];

	/// <summary>轮内快照：全路径（与消费方请求串同构，重水化时经 CombinePath 重建）→ 解码行。
	/// 整体换引用（重水化构建新字典后原子赋值），读侧无锁；轮界由 <see cref="ClearRound"/> 清空。
	/// 键比较 OrdinalIgnoreCase——SAF provider 的 displayName 是磁盘真实大小写（如 <c>GameBase.csv</c>），
	/// 而引擎固定名请求恒全大写（<c>GAMEBASE.CSV</c>），懒读靠 provider 大小写不敏感解析命中；
	/// 快照查表必须镜像该语义（真机 R11 取证：Ordinal 下 16 个固定名 CSV 全 miss，懒读回潮）。</summary>
	private static Dictionary<string, string[]>? s_lines = new(StringComparer.OrdinalIgnoreCase);

	/// <summary>OpenOnCache 咽喉消费点：Preload miss 后查轮内快照。未重水化 / 无该文件 → null（照旧懒读）。
	/// 快照在但 miss 的前 2 次（每轮）打 Info——键漂移排障样本（真机 R11 取证，保留）。</summary>
	internal static string[]? TryGetLines(string fullPath)
	{
		if (s_lines == null)
			return null;
		if (s_lines.TryGetValue(fullPath, out var lines))
			return lines;
		if (_missSamples < 2
			&& (fullPath.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
				|| fullPath.EndsWith(".erh", StringComparison.OrdinalIgnoreCase)))
		{
			_missSamples++;
			EmueraLog.Info("SrcContentCache", $"snapshot-miss sample {_missSamples}: {fullPath}");
		}
		return null;
	}
	private static int _missSamples;

	/// <summary>轮界清空（<see cref="Preload.Clear"/> 轮首调用）。任何时刻快照不得跨轮存活。</summary>
	internal static void ClearRound()
	{
		s_lines = null;
		_missSamples = 0;
	}

	/// <summary>缓存路径：与 era_ir.dat 同目录（SAF = appdata IRCache/&lt;gameKey&gt;/，真实文件系统可原子替换）。</summary>
	internal static string GetSrcCachePath(string exeDir) => ErbIrCache.GetSrcCachePath(exeDir);

	// ── 写入（建库轮收尾）───────────────────────────────────────────────────────────────

	/// <summary>
	/// 非命中分支（完整 Preload）收尾写缓存：直接取 Preload.files 的 CSV/ERH 条目（行 + 相对名），
	/// 零 accessor 参与。门控：旋钮 + content URI + IR 启用（命中轮存在才有意义）。
	/// 失败静默（无缓存 = 命中轮懒读现状，无害）。
	/// </summary>
	internal static void TryWriteAfterPreload(LoaderEnv env)
	{
		try
		{
			if (!Config.Config.SrcContentCache
				|| !ErbIrLoadPipeline.ShouldUseIrCache(Config.Config.IrDiskCacheLoad, Config.Config.LazyFunctionLoad, env.AnalysisMode)
				|| !SafPath.IsContentUri(env.ErbDir))
				return;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			var csvEntries = CollectSection(env.CsvDir, CsvRootExts);
			var erhEntries = CollectSection(env.ErbDir, ErbRootExts);
			if (csvEntries.Count + erhEntries.Count == 0)
				return; // 无可缓存内容（空 CSV/ERH）不生成——命中轮本就无懒读可省
			if (TryWrite(GetSrcCachePath(Program.ExeDir), csvEntries, erhEntries))
				EmueraLog.Info("SrcContentCache",
					$"written: csv={csvEntries.Count} erh={erhEntries.Count} ms={sw.ElapsedMilliseconds}");
		}
		catch (Exception ex)
		{
			EmueraLog.Warn("SrcContentCache", $"write skipped: {ex.GetType().Name}: {ex.Message}");
		}
	}

	/// <summary>收集一个根树下的可缓存条目（相对名 + 行）。行来源 = Preload.files（建库轮已全量装载）。</summary>
	private static List<(string Rel, string[] Lines)> CollectSection(string root, string[] exts)
	{
		var result = new List<(string Rel, string[] Lines)>();
		if (string.IsNullOrEmpty(root) || !GamePaths.Current?.DirAccessor?.DirectoryExists(root) == true)
			return result;
		foreach (var key in Preload.GetAllCachedKeys())
		{
			if (!IsCacheableExt(SafPath.GetLogicalFileName(key), exts)
				|| !SafPath.IsUnderRoot(root, key))
				continue;
			var lines = Preload.TryGetFileLines(key);
			if (lines == null)
				continue; // 枚举有键但内容未载（异常路径）——不入缓存，命中轮照旧懒读
			result.Add((SafPath.GetRelativePathFromRoot(root, key), lines));
		}
		return result;
	}

	private static bool IsCacheableExt(string name, string[] exts)
	{
		foreach (var ext in exts)
			if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
				return true;
		return false;
	}

	private static bool TryWrite(string path, List<(string Rel, string[] Lines)> csvEntries, List<(string Rel, string[] Lines)> erhEntries)
	{
		try
		{
			// 生产序上本写点先于 IR 落盘（Process.Initialize else 分支 vs LoadErbDir 收尾）——
			// 首建轮 IRCache/<gameKey>/ 目录尚不存在，必须自建（era_ir.dat 写器不管本文件的目录）；
			// ADR-0048：目录自建已由 SafCompat.OpenWrite 内部承担（父目录预建）。
			var tmp = path + ".tmp";
			using (var w = new BinaryWriter(SafCompat.OpenWrite(tmp)))
			{
				w.Write(Magic[0]); w.Write(Magic[1]); w.Write(Magic[2]); w.Write(Magic[3]);
				w.Write(FormatVersion);
				WriteSection(w, csvEntries);
				WriteSection(w, erhEntries);
			}
			SafCompat.TryGetFileStats(tmp, out long writtenBytes, out _);
			if (writtenBytes > MaxFileBytes)
			{
				SafCompat.Delete(tmp);
				EmueraLog.Info("SrcContentCache", $"write skipped: size exceeds bound {MaxFileBytes}");
				return false;
			}
			if (!SafCompat.TryAtomicReplaceLocal(tmp, path))
			{
				try { SafCompat.Delete(tmp); } catch { /* 残留 tmp 无害（下次写覆盖） */ }
				return false;
			}
			return true;
		}
		catch
		{
			return false;
		}
	}

	private static void WriteSection(BinaryWriter w, List<(string Rel, string[] Lines)> entries)
	{
		w.Write(entries.Count);
		foreach (var (rel, lines) in entries)
		{
			w.Write(rel);
			w.Write(lines.Length);
			foreach (var line in lines)
				w.Write(line);
		}
	}

	// ── 读取 + 重水化（命中轮，零 SAF 查询）─────────────────────────────────────────────

	/// <summary>
	/// 命中分支重水化：读 era_src.dat → 相对名经 CombinePath 重建全路径 → 填轮内快照。**零 SAF 查询**
	///（路线 2：不做任何新鲜度校验，用户是权威）。先自清旧快照（任何失败路径不留跨轮陈旧行）；
	/// 损坏/半截文件删除（防撕裂读半截，与新鲜度无关）。缓存不存在 = 静默（首次 / 重扫后待建库轮）。
	/// </summary>
	internal static void TryRehydrate(LoaderEnv env)
	{
		s_lines = null; // 先清：失败 / 禁用路径不留上一轮快照
		try
		{
			if (!Config.Config.SrcContentCache || !SafPath.IsContentUri(env.ErbDir))
				return;
			var path = GetSrcCachePath(Program.ExeDir);
			if (!SafCompat.FileExists(path))
				return;
			var sw = System.Diagnostics.Stopwatch.StartNew();
			if (!TryReadAndFill(env, path, out var lines, out var reason))
			{
				try { SafCompat.Delete(path); } catch { /* 删除失败无害（下轮重写覆盖） */ }
				EmueraLog.Info("SrcContentCache", $"miss: {reason} → invalidated (build round rebuilds)");
				return;
			}
			s_lines = lines;
			// 键漂移排障样本（真机 R11 取证，保留）：快照键样例 + 消费 miss 请求串（TryGetLines）对照。
			int shown = 0;
			foreach (var key in lines.Keys)
			{
				EmueraLog.Info("SrcContentCache", $"snapshot key sample: {key}");
				if (++shown >= 2) break;
			}
			EmueraLog.Info("SrcContentCache", $"hit: files={lines.Count} ms={sw.ElapsedMilliseconds} (lazy SAF reads skipped)");
		}
		catch (Exception ex)
		{
			s_lines = null;
			EmueraLog.Warn("SrcContentCache", $"rehydrate skipped: {ex.GetType().Name}: {ex.Message}");
		}
	}

	private static bool TryReadAndFill(LoaderEnv env, string path,
		out Dictionary<string, string[]> lines, out string reason)
	{
		lines = [];
		reason = "";
		var csvEntries = new List<(string Rel, string[] Lines)>();
		var erhEntries = new List<(string Rel, string[] Lines)>();
		int totalEntries = 0;
		try
		{
			// ADR-0048：读经 SafCompat（本地/content URI 单点）；打不开 = miss（对齐原 catch → false）。
			if (SafCompat.OpenRead(path) is not { } stream)
			{
				reason = "unreadable";
				return false;
			}
			using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: false);
			var mag = new char[4];
			mag[0] = r.ReadChar(); mag[1] = r.ReadChar(); mag[2] = r.ReadChar(); mag[3] = r.ReadChar();
			if (mag[0] != Magic[0] || mag[1] != Magic[1] || mag[2] != Magic[2] || mag[3] != Magic[3])
			{
				reason = "bad magic";
				return false;
			}
			if (r.ReadInt32() != FormatVersion)
			{
				reason = "format version";
				return false;
			}
			if (!ReadSection(r, csvEntries, ref totalEntries, ref reason)
				|| !ReadSection(r, erhEntries, ref totalEntries, ref reason))
				return false; // reason 已填
			if (r.BaseStream.Position != r.BaseStream.Length)
			{
				reason = "trailing bytes (torn write?)";
				return false;
			}
		}
		catch (Exception ex)
		{
			reason = $"read-ex {ex.GetType().Name}: {ex.Message}";
			return false;
		}
		if (totalEntries > MaxEntries)
		{
			reason = "entry count over bound";
			return false;
		}

		var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
		if (!FillSection(env, env.CsvDir, csvEntries, result, ref reason)
			|| !FillSection(env, env.ErbDir, erhEntries, result, ref reason))
			return false;
		lines = result;
		return true;
	}

	/// <summary>读一个根树的条目区。损坏（越界计数 / 空相对名 / 行数越界）→ false + reason。
	/// totalEntries 累计跨区条目数（硬上界防损坏文件把内存撑爆）。</summary>
	private static bool ReadSection(BinaryReader r, List<(string Rel, string[] Lines)> entries, ref int totalEntries, ref string reason)
	{
		int entryCount = r.ReadInt32();
		if (entryCount is < 0 or > MaxEntries || totalEntries + entryCount > MaxEntries)
		{
			reason = "entry count corrupt";
			return false;
		}
		totalEntries += entryCount;
		for (int i = 0; i < entryCount; i++)
		{
			var rel = r.ReadString();
			int lineCount = r.ReadInt32();
			if (rel.Length == 0 || lineCount < 0 || lineCount > MaxLineCount)
			{
				reason = "entry corrupt";
				return false;
			}
			var lines = new string[lineCount];
			for (int l = 0; l < lineCount; l++)
				lines[l] = r.ReadString();
			entries.Add((rel, lines));
		}
		return true;
	}

	/// <summary>相对名经 CombinePath(root, rel) 重建全路径填入快照（与消费方请求串同构——
	/// 消费方全走 accessor.CombinePath；sidecar 回放同构已在真机验证）。</summary>
	private static bool FillSection(LoaderEnv env, string root, List<(string Rel, string[] Lines)> entries,
		Dictionary<string, string[]> result, ref string reason)
	{
		var dirAccessor = env.DirAccessor;
		foreach (var (rel, lines) in entries)
		{
			if (dirAccessor == null || string.IsNullOrEmpty(root))
			{
				reason = "root missing";
				return false;
			}
			result[dirAccessor.CombinePath(root, rel.Replace('\\', '/'))] = lines;
		}
		return true;
	}
}
