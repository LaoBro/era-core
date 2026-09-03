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
        var startTime = DateTime.Now;
        EmueraLog.Info("preload", $"Load start: {path}");

        if (dirAccessor.DirectoryExists(path))
        {
            var allFiles = dirAccessor.GetFiles(path, "*", SearchOption.AllDirectories);
            var targetFiles = allFiles.Where(f =>
            {
                // SAF content URI 不能对整段 path 用 Path.GetExtension（应取逻辑短名）
                var ext = Path.GetExtension(SafPath.GetLogicalFileName(f));
                return ext.Equals(".csv", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".erb", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".erh", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".erd", StringComparison.OrdinalIgnoreCase) ||
                       ext.Equals(".als", StringComparison.OrdinalIgnoreCase);
            }).ToList();

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

    public static void Clear()
    {
        files.Clear();
        pathIndex.Clear();
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
    /// 02：缓存键只枚举一次——快照后复用（含日志取数）。</summary>
    public static List<KeyValuePair<string, string>>? GetFilesFromCache(string dirPath, string extension, string logTag)
    {
        if (!SafPath.IsContentUri(dirPath)) return null;
        var cachedKeys = files.Keys.ToList();
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
