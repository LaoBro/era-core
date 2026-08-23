using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using MinorShift.Emuera.GameView;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;

namespace MinorShift.Emuera.Runtime.Utils;

static partial class Preload
{
    static readonly ConcurrentDictionary<string, string[]> files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ADR-0019：返回所有已缓存文件路径键，保证与 ErbLoader/ErhLoader 查找键一致。</summary>
    public static IEnumerable<string> GetAllCachedKeys() => files.Keys;

    public static string[] GetFileLines(string path) => files[path];

    public static string[]? TryGetFileLines(string path)
    {
        files.TryGetValue(path, out var lines);
        return lines;
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

            await Task.Run(() =>
            {
                Parallel.ForEach(targetFiles, filePath =>
                {
                    var value = ReadAllLinesViaAccessor(filePath, dirAccessor);
                    files[filePath] = value;
                });
            });
        }
        else
        {
            EmueraLog.Warn("preload", $"DirExists FAILED: {path} → caching single entry");
            var value = ReadAllLinesViaAccessor(path, dirAccessor);
            files[path] = value;
        }

        EmueraLog.Debug("preload", $"Load: {path} : End in {(DateTime.Now - startTime).TotalMilliseconds}ms");
        EmueraLog.Info("preload", $"{path} → {files.Count} files loaded");
    }

    public static async Task Load(IEnumerable<string> paths, IGameDirAccessor dirAccessor)
    {
        foreach (var path in paths)
            await Load(path, dirAccessor);
    }

    public static void Clear() => files.Clear();

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
