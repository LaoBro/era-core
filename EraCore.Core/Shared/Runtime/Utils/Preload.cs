using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Script.Loader;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Utils;

static partial class Preload
{
    static readonly ConcurrentDictionary<string, string[]> files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// preload-cache-lifecycle 01（ADR-0023）：相对文件名 → 全路径的路径索引。
    /// 源文本缓存清空后常驻（~2.7K 条目），供运行期报错回读按需定位源文件。
    /// 键 = 相对根目录的逻辑相对路径（<see cref="SafPath.GetRelativePathFromRoot"/>，与 ScriptPosition.Filename 同构）；
    /// 同根内相对名唯一（firstDir 文件含子目录前缀，优先序语义照旧）。
    /// </summary>
    static readonly ConcurrentDictionary<string, string> pathIndex = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// S0-M3（diagnostic-knobs issues/11，2026-09-05）：轮内全树枚举 memo——树根路径 → 目标扩展名过滤后的文件清单。
    /// 命中轮 ERB 全树会被枚举 4 次（IR 预判定 / IndexTree / ErhLoader / LoadErbDir.BuildPendingList），
    /// 去重为 1 次冷枚举 + 3 次命中。失效边界 = <see cref="Clear"/>（ConsoleStateManager.Initialize 轮首调用，
    /// 恰在 IR 预判定之前）；<see cref="ClearCachedLines"/> 不清（轮内保留正是目的）。树根切换时 content URI
    /// 路径串必变，不可能命中旧键；目录不存在（null）不缓存。写-读竞态最坏重复枚举一次，语义无损。
    /// </summary>
    static readonly ConcurrentDictionary<string, List<string>> s_treeFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ADR-0019：返回所有已缓存文件路径键，保证与 ErbLoader/ErhLoader 查找键一致。</summary>
    public static IEnumerable<string> GetAllCachedKeys() => files.Keys;

    public static string[] GetFileLines(string path) => files[path];

    public static string[]? TryGetFileLines(string path)
    {
        files.TryGetValue(path, out var lines);
        return lines;
    }

    /// <summary>preload-cache-lifecycle 01：按相对文件名查全路径（缓存清空后的报错回读定位通道）。</summary>
    public static bool TryGetFullPath(string relativeName, out string fullPath)
    {
        pathIndex.TryGetValue(relativeName, out fullPath!);
        return fullPath != null;
    }

    /// <summary>ADR-0019：通过 DirAccessor 加载路径下所有 ERB/CSV/ERH/ERD/ALS 文件到缓存。</summary>
    public static async Task Load(string path, IGameDirAccessor dirAccessor)
    {
        // S0-M3：内容装载前失效该根 memo——Load（PreloadAsync / LoadTreesAsync 内容路径）语义为
        // 「重读磁盘快照」：写文件→Load 的测试/重载流程必须看到新增文件。失效后 EnumerateTargetFiles
        // 重新冷枚举并落新 memo；命中轮消费者（IsIrHitRound / IndexTree / ErhLoader / LoadErbDir）
        // 不经过本方法，memo 去重不受影响。
        s_treeFiles.TryRemove(path, out _);
        var startTime = DateTime.Now;
        EmueraLog.Info("preload", $"Load start: {path}");

        var targetFiles = EnumerateTargetFiles(path, dirAccessor);
        if (targetFiles != null)
        {
            // erb-bounded-lifecycle（handoff §5 最小版）：SAF content URI 路径把每树读取限为 1 并发。
            // 实测（MuMu NativeAOT 2026-08-28）：4 线程并发 SAF OpenRead 的 JNI 风暴中，monodroid
            // bridge 在 take_global_ref 处 abort（SIGABRT）——并发 JNI 是实验性 NativeAOT 运行时
            // bridge GC bug 的触发放大器。双树 WhenAll 下总计 ≤2 并发，桌面文件系统路径不受限。
            // ADR-0026 诊断旋钮二：壳层内存覆盖可拨任意并发度（钳制 1–64，真机崩溃阈值二分取证
            // 不重编），auto 保持每树 1 与止血值逐位一致；preload_policy 日志行自证请求值/生效值。
            int? requestedDegree = Config.Config.OverrideSafReadDegree;
            var readDegree = ResolveReadDegree(requestedDegree, SafPath.IsContentUri(path));
            var parallelOptions = new ParallelOptions { MaxDegreeOfParallelism = readDegree };

            await Task.Run(() =>
            {
                Parallel.ForEach(targetFiles, parallelOptions, filePath =>
                {
                    var value = ReadAllLinesViaAccessor(filePath, dirAccessor);
                    files[filePath] = value;
                    // preload-cache-lifecycle 01：顺手构建路径索引（相对名 → 全路径）。
                    // 同根内相对名唯一无竞争；重载时同键刷新（新枚举值覆盖旧值，无幽灵条目可被读出——
                    // 回读永远从磁盘取文，陈旧条目最坏退化为读失败 → 占位）。
                    pathIndex[SafPath.GetRelativePathFromRoot(path, filePath)] = filePath;
                });
            });

            EmueraLog.Info("preload",
                $"preload_policy requested={SafReadDegreeCodec.ToConfigString(requestedDegree)} " +
                $"effective={(readDegree < 0 ? "unlimited" : readDegree.ToString())} " +
                $"files={targetFiles.Count} ms={(DateTime.Now - startTime).TotalMilliseconds:F0}");
        }
        else
        {
            EmueraLog.Warn("preload", $"DirExists FAILED: {path} → caching single entry");
            var value = ReadAllLinesViaAccessor(path, dirAccessor);
            files[path] = value;
            pathIndex[SafPath.GetRelativePathFromRoot(path, path)] = path;
        }

        EmueraLog.Debug("preload", $"Load: {path} : End in {(DateTime.Now - startTime).TotalMilliseconds}ms");
        EmueraLog.Info("preload", $"{path} → {files.Count} files loaded");
    }

    public static async Task Load(IEnumerable<string> paths, IGameDirAccessor dirAccessor)
    {
        foreach (var path in paths)
            await Load(path, dirAccessor);
    }

    /// <summary>
    /// issue 09：命中轮（跳过内容 Preload）的路径索引构建——只枚举文件清单，不读源内容。
    /// 运行期报错回读（<see cref="Process.getRawTextFormFilewithLine"/> → <see cref="TryGetFullPath"/>）
    /// 依赖路径索引定位源文件；命中轮无内容缓存，索引必须在枚举期顺手建好，否则子目录文件
    /// 报错回显退化为占位。枚举/扩展名过滤与 <see cref="Load(string, IGameDirAccessor)"/> 语义一致。
    /// </summary>
    public static void IndexTree(string path, IGameDirAccessor dirAccessor)
    {
        // S0-M3：计时行把「预检段 / 间隙①」拆开——命中轮此段 = memo 命中时仅剩 pathIndex 回填。
        var sw = Stopwatch.StartNew();
        var targetFiles = EnumerateTargetFiles(path, dirAccessor);
        if (targetFiles == null)
            return;
        foreach (var filePath in targetFiles)
            pathIndex[SafPath.GetRelativePathFromRoot(path, filePath)] = filePath;
        sw.Stop();
        EmueraLog.Info("preload",
            $"IndexTree root={SafPath.GetLogicalFileName(path)} files={targetFiles.Count} ms={sw.ElapsedMilliseconds}");
    }

    /// <summary>枚举 path 下目标扩展名（.erb/.csv/.erh/.erd/.als）的文件清单，不读内容。目录不存在返回 null。
    /// S0-M3：轮内 memo 优先——命中直接返回（零 IPC 零 URI 重建），miss 才走 DirectoryExists + 全树 GetFiles 并落 memo。
    /// 冷枚举打 Info（含耗时），命中不打（由 IndexTree / BuildPendingList 各自的计时行体现收缩）。</summary>
    /// <summary>
    /// issues/13 M5：命中轮 memo 播种——sidecar 全树清单（相对名）经 CombinePath 重建全路径回填
    /// <see cref="s_treeFiles"/>，让 IndexTree / ErhLoader（.erh 枚举同走 memo）/ 任何
    /// EnumerateTargetFiles 消费者免冷枚举（真机 15.6s/次）。已有 memo（真实枚举先到）不覆盖；
    /// 空清单不回填（与枚举侧「空清单不缓存」语义一致）。陈旧清单的后果 = 幽灵条目读失败告警，
    /// 路线 2 语义下由用户重扫收敛（fp 只吃 .erb 清单，.erh 幽灵不触发自动失效）。
    /// </summary>
    public static void SeedTreeMemo(string rootDir, IReadOnlyList<string> relNames, IGameDirAccessor dirAccessor)
    {
        if (relNames.Count == 0 || s_treeFiles.ContainsKey(rootDir))
            return;
        // issues/14 A1：批量拼接——SAF 逐条 CombinePath 曾占播种 ~1.4s（2532 次 Parse+ResolveDocId
        // 重复），批量后基座解析一次。顺序与逐条一致（CombinePaths 契约）。
        var normalized = new string[relNames.Count];
        for (int i = 0; i < relNames.Count; i++)
            normalized[i] = SafPath.RelToPhysical(relNames[i]);
        var full = dirAccessor.CombinePaths(rootDir, normalized);
        s_treeFiles[rootDir] = new List<string>(full);
    }

    /// <summary>issues/13 M5：建库轮收尾取全树清单（写 sidecar ② 区用）。memo 未填充（空树等）返回 false。</summary>
    internal static bool TryGetTreeFiles(string rootDir, out IReadOnlyList<string> fullPaths)
    {
        if (s_treeFiles.TryGetValue(rootDir, out var cached))
        {
            fullPaths = cached;
            return true;
        }
        fullPaths = Array.Empty<string>();
        return false;
    }

    private static List<string>? EnumerateTargetFiles(string path, IGameDirAccessor dirAccessor)
    {
        if (s_treeFiles.TryGetValue(path, out var cached))
            return cached;
        if (!dirAccessor.DirectoryExists(path))
            return null;
        var sw = Stopwatch.StartNew();
        var allFiles = dirAccessor.GetFiles(path, "*", SearchOption.AllDirectories);
        var filtered = allFiles.Where(f => IsTargetExtension(Path.GetExtension(SafPath.GetLogicalFileName(f)))).ToList();
        sw.Stop();
        // 只缓存非空清单：空目录（引擎初始化轮在文件写入前先行枚举的典型场景）不缓存，否则
        // 同一轮稍后写入的文件会被陈旧空缓存吞掉（LoaderTestHarness Initialize→写语料→reload 即此模式，
        // 2026-09-05 回归：SelectCase/UncalledSweep 语料 0 装载）。目录不存在（null）同理不缓存。
        if (filtered.Count > 0)
        {
            s_treeFiles[path] = filtered;
            // S0-M6（空档取证，2026-09-09）：seedKeys>0 且仍 miss = 播种 key 与查询 key 错位
            //（SeedTreeMemo 播种在 env.ErbDir，CSV/ERH 子树查询走各自子目录 key）——
            // 证实「sidecar 全树清单为何没救 CSV 冷枚举（TreeEnum 473ms）」的根因归属。
            EmueraLog.Info("preload",
                $"TreeEnum root={SafPath.GetLogicalFileName(path)} files={filtered.Count} ms={sw.ElapsedMilliseconds} memo=miss seedKeys={s_treeFiles.Count}");
        }
        return filtered;
    }

    /// <summary>
    /// issue 09 真机修复（launch7 list-diff 定位）：命中轮（缓存空）的文件清单按 Preload 语义枚举——
    /// 与缓存键同源（<c>GetFiles(path, "*", AllDirectories)</c> 全树 + C# 侧扩展名过滤），
    /// 避免 <see cref="Config.Config.GetFiles"/> 的 SAF 通配/displayName 差异（真机实测丢文件、
    /// 混合分隔符路径）导致清单漂移、IR 指纹永不命中。返回 (相对路径, 全路径)。
    /// </summary>
    public static List<KeyValuePair<string, string>> EnumerateFilesFromTree(
        string rootDir, IGameDirAccessor dirAccessor, string extension)
    {
        var result = new List<KeyValuePair<string, string>>();
        var targetFiles = EnumerateTargetFiles(rootDir, dirAccessor);
        if (targetFiles == null)
            return result;
        foreach (var filePath in targetFiles)
            if (filePath.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                result.Add(new KeyValuePair<string, string>(
                    SafPath.GetRelativePathFromRoot(rootDir, filePath), filePath));
        return result;
    }

    /// <summary>SAF content URI 不能对整段 path 用 Path.GetExtension（应取逻辑短名），见 <see cref="SafPath.GetLogicalFileName"/>。</summary>
    private static bool IsTargetExtension(string ext) =>
        ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".erd", StringComparison.OrdinalIgnoreCase) ||
        ext.Equals(".als", StringComparison.OrdinalIgnoreCase);

    public static void Clear()
    {
        files.Clear();
        pathIndex.Clear();
        s_treeFiles.Clear(); // S0-M3：轮边界——下轮全树枚举重新冷走，杜绝跨轮陈旧清单
        // issues/10 10-B：CSV/ERH 内容快照同属轮界生命周期（重水化每轮先自清，此处兜底防
        // 「命中轮 → 快照留存 → 同进程改文件 → 非命中轮」序列把旧行喂给 OpenOnCache）。
        SrcContentCache.ClearRound();
    }

    /// <summary>
    /// preload-cache-lifecycle 01（ADR-0023）：加载管线收尾清空源文本缓存（~180 MiB @ eraTW 规模）。
    /// 路径索引保留——运行期报错回读（getRawTextFormFilewithLine）经它定位源文件后按需回读。
    /// 全量重置（含索引）仍走 <see cref="Clear"/>（轮首 / 会话拆除）。
    /// </summary>
    public static void ClearCachedLines() => files.Clear();

    /// <summary>ADR-0019 / erb-loader-parallel 03：从 Preload 缓存获取指定扩展名的文件列表
    /// （ErbLoader.GetErbFilesFromCache 与 ErhLoader.GetErhFilesFromCache 孪生收敛，SAF 路径专用，
    /// 非缓存键路径返回 null 由调用方走文件系统枚举）。
    /// Key = 相对 dir 的逻辑相对路径（对齐 Config.getFiles）；Value = 缓存全路径键（OpenOnCache 精确命中）。
    /// 用 Unescape 后的 documentId 前缀过滤（避免 raw URI 上 %2F 前缀匹配失效）。
    /// 02：缓存键只枚举一次——快照后复用（含日志取数）。
    /// issue 09：缓存整体为空（从未加载——命中轮跳过内容 Preload）时返回 null，由调用方走
    /// Config.GetFiles 枚举兜底；缓存加载过但无匹配扩展名时仍返回空列表（表示「确实没有」）。</summary>
    public static List<KeyValuePair<string, string>>? GetFilesFromCache(string dirPath, string extension, string logTag)
    {
        if (!SafPath.IsContentUri(dirPath)) return null;
        var cachedKeys = files.Keys.ToList();
        if (cachedKeys.Count == 0)
            return null;
        var result = cachedKeys
            .Where(k => k.EndsWith(extension, StringComparison.OrdinalIgnoreCase)
                && SafPath.IsUnderRoot(dirPath, k))
            .Select(k => new KeyValuePair<string, string>(
                SafPath.GetRelativePathFromRoot(dirPath, k), k))
            .ToList();
        if (result.Count == 0)
            EmueraLog.Info(logTag, $"GetFilesFromCache: 0 {extension} under root in {cachedKeys.Count} cached keys");
        else
            EmueraLog.Info(logTag, $"GetFilesFromCache: {result.Count} files, firstKey={result[0].Key}");
        return result;
    }

    /// <summary>erb-loader-parallel 02/03：ERB/CSV 两棵树并行预载的单一入口
    /// （原 Process.ReloadErb / ConsoleStateManager.Initialize / LoaderTestHarness.PreloadAsync
    /// 三处 Task.WhenAll 配对收敛——Load 内部本就 Parallel.ForEach 读文件，串行 await 白付一棵树的墙钟）。</summary>
    public static Task LoadTreesAsync(string erbDir, string csvDir, IGameDirAccessor dirAccessor) =>
        Task.WhenAll(
            Load(erbDir, dirAccessor),
            Load(csvDir, dirAccessor));

    /// <summary>
    /// ADR-0026 诊断旋钮二唯一决策点：请求并发度（null=auto）× 是否 content-URI → 每树
    /// ParallelOptions.MaxDegreeOfParallelism。auto 与 7c10328 止血值逐位一致（content-URI 每树 1）；
    /// 显式值（真机崩溃阈值二分取证）钳制 1–64 后原样生效；桌面文件系统路径恒 -1（不受限）。
    /// 数值域语义由 SafReadDegreeTests 覆盖。
    /// </summary>
    internal static int ResolveReadDegree(int? requested, bool isContentUri)
        => !isContentUri ? -1
        : Math.Clamp(requested ?? 1, SafReadDegreeCodec.MinExplicitDegree, SafReadDegreeCodec.MaxExplicitDegree);

    private static string[] ReadAllLinesViaAccessor(string path, IGameDirAccessor dirAccessor)
    {
        try
        {
            var bytes = dirAccessor.ReadAllBytes(path);
            if (bytes == null)
            {
                EmueraLog.Warn("preload", $"EMPTY_BYTES: {path}");
                return [];
            }
            var lines = EncodingHandler.ReadAllLinesFromBytes(bytes);
            if (lines.Length == 0)
                EmueraLog.Warn("preload", $"EMPTY_LINES: {path}");
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ParserMediator.Warn(string.Format(trerror.FileUsingOtherProcess.Text, path), new ScriptPosition(path, 0), 0, "");
            EmueraLog.Error("preload", $"IOERR: {path} → {ex.Message}");
            return [];
        }
        catch (Exception)
        {
            ParserMediator.Warn(trerror.AbnormalEncode.Text, new ScriptPosition(path, 0), 0, "");
            EmueraLog.Error("preload", $"ENCERR: {path}");
            return [];
        }
    }
}
