using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Provider;
using Android.Util;
using Avalonia.Platform.Storage;

namespace EraCore.Native.Android;

/// <summary>游戏库中的一个游戏：私有目录（复制/解压）或登记的原目录（零拷贝）。</summary>
internal sealed record GameLibraryEntry(string Name, string RootPath, DateTimeOffset? LastPlayed)
{
    public GameSourceKind Source { get; init; } = GameSourceKind.Internal;

    /// <summary>目录是否存在且可访问；原目录在权限被收回或目录被移走后为 false。</summary>
    public bool IsAvailable { get; init; } = true;

    public string? UnavailableReason { get; init; }

    public string SourceText => Source == GameSourceKind.Path ? "原目录" : "已复制";

    /// <summary>library.json 的键：私有目录按名字（与历史数据兼容），原目录按绝对路径。</summary>
    public string PlayedKey => Source == GameSourceKind.Path ? "path:" + RootPath : "dir:" + Name;

    public string LastPlayedText => LastPlayed is { } played
        ? played.ToLocalTime().ToString("yyyy-MM-dd HH:mm")
        : "从未游玩";
}

/// <summary>导入结果：成功时为条目；失败时 <see cref="Error"/> 是可展示的短消息。</summary>
internal sealed record GameImportResult(GameLibraryEntry? Entry, string? Error)
{
    public static GameImportResult Success(GameLibraryEntry entry) => new(entry, null);
    public static GameImportResult Failure(string error) => new(null, error);
}

/// <summary>
/// 游戏库：扫描私有目录 <c>games/*</c>、导入目录/压缩包、记录最后游玩时间、解压内置示例。
///
/// <para>只负责文件系统与元数据；会话生命周期在 <see cref="NativeRuntime"/>，UI 在
/// <see cref="GameLibraryView"/>。导入后的游戏必须是真实目录（引擎需要可写存档目录）。</para>
/// </summary>
internal static class GameLibrary
{
    private const string LogTag = "EraCoreNative";
    private const string GamesDirName = "games";
    private const string MetadataFile = "library.json";
    private const string ConfigFile = "emuera.config";

    /// <summary>内置示例（asset 根名, 私有目录名）。</summary>
    private static readonly (string Asset, string Dir)[] BuiltInGames =
    {
        ("test_game", "test_game"),
        ("test_game_b", "test_game_b"),
    };

    public static string GamesDirectory(Context context)
    {
        var filesDir = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android FilesDir 不可用。");
        var dir = Path.Combine(filesDir, GamesDirName);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>
    /// 扫描游戏库：私有目录 <c>games/*</c> + settings.json 中登记的原目录。
    /// 原目录条目会就地检查可用性（权限被收回 / 目录被移走 / 不再是游戏），不可用时保留记录供 UI 提示与移除。
    /// </summary>
    public static IReadOnlyList<GameLibraryEntry> Scan(Context context)
    {
        var root = GamesDirectory(context);
        var played = LoadPlayed(context);
        var entries = new List<GameLibraryEntry>();
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);

        foreach (var dir in Directory.GetDirectories(root))
        {
            if (Path.GetFileName(dir).StartsWith('.'))
                continue; // 导入临时目录
            if (!IsGameDirectory(dir))
                continue;

            var name = Path.GetFileName(dir);
            seenPaths.Add(dir);
            entries.Add(new GameLibraryEntry(name, dir, played.TryGetValue("dir:" + name, out var at) ? at : null));
        }

        foreach (var source in NativeSettings.GameSources)
        {
            var path = source.RootPath;
            if (!seenPaths.Add(path))
                continue;

            var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                name = path;

            var available = true;
            string? reason = null;
            if (!ExternalStorageAccess.IsGranted())
            {
                available = false;
                reason = "缺少「所有文件访问」权限，请重新授权";
            }
            else if (!Directory.Exists(path))
            {
                available = false;
                reason = "目录不存在或已被移动";
            }
            else if (!IsGameDirectory(path))
            {
                available = false;
                reason = "目录已不是 emuera 游戏（缺少 emuera.config 或 erb/）";
            }

            entries.Add(new GameLibraryEntry(
                name,
                path,
                played.TryGetValue("path:" + path, out var at) ? at : null)
            {
                Source = GameSourceKind.Path,
                IsAvailable = available,
                UnavailableReason = reason,
            });
        }

        entries.Sort((a, b) =>
        {
            var aTime = a.LastPlayed ?? DateTimeOffset.MinValue;
            var bTime = b.LastPlayed ?? DateTimeOffset.MinValue;
            var byTime = bTime.CompareTo(aTime);
            return byTime != 0 ? byTime : string.CompareOrdinal(a.Name, b.Name);
        });
        return entries;
    }

    public static void RecordPlayed(Context context, GameLibraryEntry entry)
    {
        var played = LoadPlayed(context);
        played[entry.PlayedKey] = DateTimeOffset.UtcNow;
        SavePlayed(context, played);
    }

    /// <summary>
    /// 登记原目录（零拷贝）：只校验并把路径写入 settings.json，不复制、不写 marker、不触碰游戏文件。
    /// 校验顺序与提示一致：权限 → 目录存在 → 是 emuera 游戏 → 至少有一个 .erb。
    /// </summary>
    public static GameImportResult RegisterPathSource(Context context, string realPath)
    {
        var watch = Stopwatch.StartNew();
        string normalized;
        try
        {
            normalized = Path.GetFullPath(realPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return GameImportResult.Failure("目录路径无效：" + ex.Message);
        }

        if (!ExternalStorageAccess.IsGranted())
            return GameImportResult.Failure("缺少「所有文件访问」权限，无法直接读取原目录。");

        // Directory.Exists 对中文/空格路径与真实文件系统一致；Android 外部存储通常不允许软链接，
        // 这里按目标路径直接探测，不额外解析链接。
        if (!Directory.Exists(normalized))
            return GameImportResult.Failure("目录不存在或无权访问：" + normalized);

        if (!IsGameDirectory(normalized))
            return GameImportResult.Failure("所选目录不是 emuera 游戏（缺少 emuera.config 或 erb/）。");

        if (!ContainsErbFile(normalized))
        {
            return GameImportResult.Failure(
                "未找到 .erb 文件：请选择 emuera 游戏根目录（包含 erb/）。");
        }

        var added = NativeSettings.AddPathSource(normalized);
        watch.Stop();
        Log.Info(LogTag, string.Format(
            "path source registered: path={0} added={1} ms={2}",
            normalized, added, watch.ElapsedMilliseconds));

        var name = Path.GetFileName(normalized);
        if (string.IsNullOrEmpty(name))
            name = normalized;
        return GameImportResult.Success(new GameLibraryEntry(name, normalized, null)
        {
            Source = GameSourceKind.Path,
        });
    }

    /// <summary>只移除登记记录，<b>绝不删除用户原目录</b>。</summary>
    public static bool RemovePathSource(string rootPath) => NativeSettings.RemovePathSource(rootPath);

    private static bool ContainsErbFile(string root)
    {
        try
        {
            // 不用 searchPattern "*.erb"：Android 真实文件系统区分大小写，会漏掉 .ERB。
            // 统一枚举后按扩展名做 OrdinalIgnoreCase 匹配（与 IGameDirAccessor 的通配语义一致）。
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                if (Path.GetExtension(file).Equals(".erb", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "check erb failed: " + ex.Message);
            return false;
        }
    }

    /// <summary>确保内置示例已解压（M3 的离线可玩入口；SAF/选择器不可用时仍可进游戏）。</summary>
    public static async Task EnsureBuiltInGamesAsync(Context context)
    {
        foreach (var (asset, dir) in BuiltInGames)
        {
            var root = Path.Combine(GamesDirectory(context), dir);
            if (File.Exists(Path.Combine(root, ".extracted-v1")))
                continue;
            await Task.Run(() => AndroidGameAssets.EnsureGame(context, asset, dir)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 原生 SAF 目录导入：迭代枚举 tree URI，只复制游戏所需文件到私有目录。
    ///
    /// <para>不走 Avalonia 的 <c>IStorageFolder</c>：其 Android 实现依赖 ActivityResult 回传，
    /// 部分设备在「允许访问」确认框后会丢失回调，任务永不完成；且逐项查询元数据在真实游戏目录
    /// （上千文件）上非常慢。这里用 DocumentsContract + ContentResolver 直接流式复制：
    /// 每个目录一次 Query 拿齐元数据，复制按文件数并发（4–8），瓶颈是每文件 SAF IPC 固定开销。</para>
    /// </summary>
    /// <param name="progress">后台线程回调：每 ~200ms 一次的复制进度（文件数 / 字节 / 速率 / ETA）。</param>
    /// <param name="cancellationToken">取消导入：中断并行 worker 并清理 staging。</param>
    /// <param name="includeOptionalFiles">是否复制可选扩展名（.txt/.json/.m4a/.flac）；默认只复制必需项。</param>
    public static async Task<GameImportResult> ImportSafTreeAsync(
        Context context,
        global::Android.Net.Uri treeUri,
        Action<SafImportProgress>? progress,
        CancellationToken cancellationToken = default,
        bool includeOptionalFiles = false)
    {
        var staging = CreateStagingDirectory(context);
        var totalWatch = Stopwatch.StartNew();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var scanWatch = Stopwatch.StartNew();
            var displayName = ReadTreeDisplayName(context, treeUri);
            Log.Info(LogTag, $"saf import begin: uri={treeUri} name={displayName} staging={staging} optional={includeOptionalFiles}");

            var scan = await Task.Run(
                () => EnumerateSafTree(context, treeUri, includeOptionalFiles),
                CancellationToken.None).ConfigureAwait(false);
            scanWatch.Stop();
            Log.Info(LogTag, string.Format(
                "saf timing: scan ms={0} queries={1} dirs={2} files={3} bytes={4} skippedOptional={5}",
                scanWatch.ElapsedMilliseconds, scan.QueryCount, scan.DirectoryCount,
                scan.Files.Count, scan.TotalBytes, scan.SkippedOptional));

            var files = scan.Files;
            if (!files.Any(f => f.Name.EndsWith(".erb", StringComparison.OrdinalIgnoreCase)))
            {
                DeleteDirectory(staging);
                Log.Warn(LogTag, "saf import rejected: no .erb file");
                return GameImportResult.Failure("未找到 .erb 文件：请选择 emuera 游戏根目录（包含 erb/）。");
            }

            var copyWatch = Stopwatch.StartNew();
            var copy = await Task.Run(
                () => CopySafFiles(context, treeUri, files, staging, progress, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
            copyWatch.Stop();
            Log.Info(LogTag, string.Format(
                "saf timing: copy ms={0} files={1} bytes={2} avgFileMs={3:F2} mbps={4:F2}",
                copyWatch.ElapsedMilliseconds, copy.Count, copy.Bytes,
                copy.Count == 0 ? 0 : copy.TotalMs / copy.Count,
                copyWatch.ElapsedMilliseconds == 0 ? 0 : copy.Bytes / 1024.0 / 1024.0 * 1000 / copyWatch.ElapsedMilliseconds));
            Log.Info(LogTag, string.Format(
                "saf timing: copy-small ms={0} files={1} bytes={2} avgFileMs={3:F2} mbps={4:F2}",
                copy.SmallMs, copy.SmallCount, copy.SmallBytes,
                copy.SmallCount == 0 ? 0 : copy.SmallMs / copy.SmallCount,
                copy.SmallMs == 0 ? 0 : copy.SmallBytes / 1024.0 / 1024.0 * 1000 / copy.SmallMs));
            Log.Info(LogTag, string.Format(
                "saf timing: copy-large ms={0} files={1} bytes={2} avgFileMs={3:F2} mbps={4:F2}",
                copy.LargeMs, copy.LargeCount, copy.LargeBytes,
                copy.LargeCount == 0 ? 0 : copy.LargeMs / copy.LargeCount,
                copy.LargeMs == 0 ? 0 : copy.LargeBytes / 1024.0 / 1024.0 * 1000 / copy.LargeMs));
            Log.Info(LogTag, "saf import: copy finished, finalizing");

            cancellationToken.ThrowIfCancellationRequested();
            var finalizeWatch = Stopwatch.StartNew();
            var result = FinalizeImport(context, staging, SanitizeName(displayName), markImported: true);
            finalizeWatch.Stop();
            if (result.Entry is null)
                DeleteDirectory(staging);
            Log.Info(LogTag, string.Format(
                "saf timing: finalize ms={0} total ms={1}",
                finalizeWatch.ElapsedMilliseconds, totalWatch.ElapsedMilliseconds));
            return result;
        }
        catch (OperationCanceledException)
        {
            Log.Warn(LogTag, "saf import cancelled");
            DeleteDirectory(staging);
            return GameImportResult.Failure("已取消导入。");
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "saf import failed: " + ex);
            DeleteDirectory(staging);
            return GameImportResult.Failure("导入失败：" + ex.Message);
        }
    }

    /// <summary>通过 Avalonia 存储选择器导入 zip（解压后可选剥离单一顶层目录）。</summary>
    public static async Task<GameImportResult> ImportZipAsync(Context context, IStorageFile file)
    {
        var staging = CreateStagingDirectory(context);
        var zipPath = staging + ".zip";
        var extractRoot = Path.Combine(staging, "extract");
        try
        {
            await using (var input = await file.OpenReadAsync().ConfigureAwait(false))
            await using (var output = File.Create(zipPath))
                await input.CopyToAsync(output).ConfigureAwait(false);

            Directory.CreateDirectory(extractRoot);
            ZipFile.ExtractToDirectory(zipPath, extractRoot);

            var candidate = FindGameRoot(extractRoot) ?? extractRoot;
            var fallbackName = Path.GetFileNameWithoutExtension(file.Name);
            if (!IsGameDirectory(candidate))
            {
                DeleteDirectory(staging);
                return GameImportResult.Failure("压缩包内没有找到 emuera 游戏（缺少 emuera.config 或 erb/）。");
            }

            return FinalizeImport(context, candidate, SanitizeName(fallbackName), markImported: true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
        {
            Log.Warn(LogTag, "zip import failed: " + ex.Message);
            DeleteDirectory(staging);
            return GameImportResult.Failure("压缩包导入失败：" + ex.Message);
        }
        finally
        {
            if (File.Exists(zipPath))
                File.Delete(zipPath);
            if (Directory.Exists(staging))
                DeleteDirectory(staging);
        }
    }

    private static GameImportResult FinalizeImport(
        Context context,
        string candidateRoot,
        string preferredName,
        bool markImported = false)
    {
        if (!IsGameDirectory(candidateRoot))
            return GameImportResult.Failure("所选目录不是 emuera 游戏（缺少 emuera.config 或 erb/）。");

        var root = GamesDirectory(context);
        var name = preferredName.Length == 0 ? "game" : preferredName;
        var finalPath = Path.Combine(root, name);
        var suffix = 2;
        while (Directory.Exists(finalPath))
            finalPath = Path.Combine(root, $"{name}-{suffix++}");
        if (Path.GetFileName(finalPath) != name)
            Log.Info(LogTag, $"import name collision: {name} -> {Path.GetFileName(finalPath)}");

        // candidateRoot 可能嵌套在 staging 里；Directory.Move 会把整棵树搬到最终位置。
        Directory.Move(candidateRoot, finalPath);
        if (markImported)
            WriteImportMarker(finalPath);

        var entry = new GameLibraryEntry(Path.GetFileName(finalPath), finalPath, null);
        Log.Info(LogTag, $"imported game: {entry.Name} -> {finalPath}");
        return GameImportResult.Success(entry);
    }

    private static void WriteImportMarker(string root)
    {
        try
        {
            File.WriteAllText(Path.Combine(root, ".extracted-v1"), "imported\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "write import marker failed: " + ex.Message);
        }
    }

    /// <summary>SAF 中的一个待复制文件（<see cref="Size"/> 来自 enumerate 的 size 列，缺列/不可用时为 -1 或 0）。</summary>
    private sealed record SafFile(string DocumentId, string RelativePath, string Name, long Size);

    /// <summary>枚举结果与阶段统计（查询次数 / 目录数用于区分 IPC 与遍历开销）。</summary>
    private sealed record SafScanResult(
        List<SafFile> Files, int QueryCount, int DirectoryCount,
        long TotalBytes, int SkippedOptional);

    /// <summary>复制阶段统计（TotalMs 为逐文件复制体耗时之和，不含进度/日志开销）。</summary>
    private sealed record SafCopyResult(
        int Count, long Bytes, long TotalMs,
        int SmallCount, long SmallBytes, long SmallMs,
        int LargeCount, long LargeBytes, long LargeMs);

    /// <summary>
    /// 后台线程上报的复制进度（每 ~200ms 一次）。
    /// <see cref="TotalBytes"/> 为 0 表示 provider 未提供 size 列，此时 ETA 按文件速率估算。
    /// </summary>
    internal sealed record SafImportProgress(
        int CopiedFiles,
        int TotalFiles,
        long CopiedBytes,
        long TotalBytes,
        double BytesPerSecond,
        TimeSpan? Remaining,
        string CurrentName);

    /// <summary>小文件 / 大文件分界（用于报告每文件固定开销 vs 吞吐）。</summary>
    private const long LargeFileThresholdBytes = 100 * 1024;

    /// <summary>复制缓冲：256KB（真实游戏文件多为 KB 级脚本与 MB 级资源，避免逐字节与小缓冲抖动）。</summary>
    private const int CopyBufferBytes = 256 * 1024;

    private static readonly HashSet<string> SkipFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "emuera.log", "time.log", "profile",
    };

    private static readonly HashSet<string> SkipDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "save", "_ircache", ".git", ".svn", "out", "build",
        "node_modules", "__pycache__",
    };

    /// <summary>
    /// 必需资源：引擎/游戏运行缺一不可的扩展名。
    /// 脚本（erb/erh/csv/config）、图像、常用音频、字体。
    /// </summary>
    private static readonly HashSet<string> RequiredExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".erb", ".erh", ".csv", ".config",
        ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp",
        ".wav", ".ogg", ".mp3",
        ".ttf", ".otf",
    };

    /// <summary>
    /// 可选资源：默认不复制（旧白名单里过宽的项）。
    /// .txt/.json 不是引擎原生读取格式；.m4a/.flac 极少作为 BGM。
    /// 需要时由调用方显式开启 <c>includeOptionalFiles</c>。
    /// </summary>
    private static readonly HashSet<string> OptionalExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".m4a", ".flac",
    };

    private static SafScanResult EnumerateSafTree(
        Context context,
        global::Android.Net.Uri treeUri,
        bool includeOptionalFiles)
    {
        var resolver = context.ContentResolver
            ?? throw new InvalidOperationException("ContentResolver 不可用。");
        var files = new List<SafFile>();
        var queryCount = 0;
        var directoryCount = 0;
        var skippedOptional = 0;
        var rootDocumentId = DocumentsContract.GetTreeDocumentId(treeUri)
            ?? throw new InvalidOperationException("tree URI 缺少 document id。");
        var rootChildren = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, rootDocumentId)
            ?? throw new InvalidOperationException("无法构建子目录 URI。");

        // 迭代式深度优先：每个目录只 Query 一次，拿齐 document_id/display_name/mime_type/size，
        // 不再对每个子项单独查 _display_name；目录入栈而非递归，避免深层树的栈增长。
        var stack = new Stack<(global::Android.Net.Uri ChildrenUri, string Prefix, int Depth)>();
        stack.Push((rootChildren, string.Empty, 0));

        while (stack.Count > 0)
        {
            var (childrenUri, prefix, depth) = stack.Pop();
            if (depth > 24)
            {
                Log.Warn(LogTag, $"saf scan: depth limit reached at {prefix}");
                continue;
            }

            queryCount++;
            directoryCount++;
            var projection = new[] { "document_id", "_display_name", "mime_type", "size" };
            using var cursor = resolver.Query(childrenUri, projection, null, null, null);
            if (cursor is null)
            {
                Log.Warn(LogTag, "saf scan: query returned null for " + childrenUri);
                continue;
            }

            var idIndex = cursor.GetColumnIndex("document_id");
            var nameIndex = cursor.GetColumnIndex("_display_name");
            var mimeIndex = cursor.GetColumnIndex("mime_type");
            var sizeIndex = cursor.GetColumnIndex("size");
            if (idIndex < 0 || nameIndex < 0 || mimeIndex < 0)
            {
                Log.Warn(LogTag, "saf scan: required column missing");
                continue;
            }

            while (cursor.MoveToNext())
            {
                var documentId = cursor.GetString(idIndex);
                var displayName = cursor.GetString(nameIndex);
                var mimeType = cursor.GetString(mimeIndex);
                if (string.IsNullOrWhiteSpace(documentId) || string.IsNullOrWhiteSpace(displayName))
                    continue;

                if (mimeType == "vnd.android.document/directory")
                {
                    if (SkipDirectoryNames.Contains(displayName) || displayName.StartsWith('.'))
                        continue;
                    var childUri = DocumentsContract.BuildChildDocumentsUriUsingTree(treeUri, documentId);
                    if (childUri is null)
                    {
                        Log.Warn(LogTag, $"saf scan: cannot build child uri for {prefix}{displayName}");
                        continue;
                    }
                    stack.Push((childUri, prefix + SanitizeName(displayName) + "/", depth + 1));
                    continue;
                }

                if (SkipFileNames.Contains(displayName) || displayName.StartsWith('.'))
                    continue;

                var extension = Path.GetExtension(displayName);
                if (OptionalExtensions.Contains(extension))
                {
                    if (!includeOptionalFiles)
                    {
                        skippedOptional++;
                        continue;
                    }
                }
                else if (!RequiredExtensions.Contains(extension))
                {
                    continue;
                }

                // 注意：部分 provider 的 size 列为 0/null——仅作 ETA 参考，进度以实际写出字节为准。
                var size = sizeIndex >= 0 ? cursor.GetLong(sizeIndex) : -1;
                files.Add(new SafFile(documentId, prefix + SanitizeName(displayName), displayName, size));
            }
        }

        var totalBytes = files.Sum(f => f.Size > 0 ? f.Size : 0);
        return new SafScanResult(files, queryCount, directoryCount, totalBytes, skippedOptional);
    }

    private static async Task<SafCopyResult> CopySafFiles(
        Context context,
        global::Android.Net.Uri treeUri,
        IReadOnlyList<SafFile> files,
        string staging,
        Action<SafImportProgress>? progress,
        CancellationToken cancellationToken)
    {
        var resolver = context.ContentResolver
            ?? throw new InvalidOperationException("ContentResolver 不可用。");
        var total = files.Count;
        if (total == 0)
            return new SafCopyResult(0, 0, 0, 0, 0, 0, 0, 0, 0);

        var totalBytes = files.Sum(f => f.Size > 0 ? f.Size : 0);

        // 目录一次性预建：并行 worker 只写文件，避免同一目录被多线程同时 CreateDirectory。
        var directories = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < total; i++)
        {
            var destination = Path.Combine(staging, files[i].RelativePath.Replace('/', Path.DirectorySeparatorChar));
            directories.Add(Path.GetDirectoryName(destination)!);
        }
        foreach (var directory in directories)
            Directory.CreateDirectory(directory);

        var copiedFiles = 0;
        var copiedBytes = 0L;
        var totalMs = 0L;
        var smallCount = 0;
        var smallBytes = 0L;
        var smallMs = 0L;
        var largeCount = 0;
        var largeBytes = 0L;
        var largeMs = 0L;
        var currentName = files[0].RelativePath;
        var nextIndex = -1;
        Exception? firstFailure = null;
        var failureGate = new object();

        // 4–8 并发：瓶颈是每文件 SAF IPC 固定开销（线程阻塞等待），并发重叠等待。
        // IO-bound → 取 2×CPU，落在 [4,8]：模拟器 4 核用 8，真机 8 核封顶 8，低端 2 核用 4。
        var concurrency = Math.Clamp(Environment.ProcessorCount * 2, 4, 8);
        var watch = Stopwatch.StartNew();
        using var workCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        void CopyWorker()
        {
            // worker 本地复用缓冲：避免每文件分配 256KB（1000+ 文件时明显）。
            var buffer = new byte[CopyBufferBytes];
            while (true)
            {
                workCts.Token.ThrowIfCancellationRequested();
                var index = Interlocked.Increment(ref nextIndex);
                if (index >= total)
                    return;

                var file = files[index];
                var documentUri = DocumentsContract.BuildDocumentUriUsingTree(treeUri, file.DocumentId)
                    ?? throw new IOException("无法构建文件 URI：" + file.RelativePath);
                var destination = Path.Combine(staging, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                var fileWatch = Stopwatch.StartNew();
                long written;
                using (var input = resolver.OpenInputStream(documentUri)
                    ?? throw new IOException("无法读取 " + file.RelativePath))
                using (var output = File.Create(destination))
                    written = CopyStream(input, output, buffer, workCts.Token);
                fileWatch.Stop();
                var fileMs = fileWatch.ElapsedMilliseconds;

                Interlocked.Add(ref copiedBytes, written);
                Interlocked.Add(ref totalMs, fileMs);
                if (written < LargeFileThresholdBytes)
                {
                    Interlocked.Increment(ref smallCount);
                    Interlocked.Add(ref smallBytes, written);
                    Interlocked.Add(ref smallMs, fileMs);
                }
                else
                {
                    Interlocked.Increment(ref largeCount);
                    Interlocked.Add(ref largeBytes, written);
                    Interlocked.Add(ref largeMs, fileMs);
                }
                Volatile.Write(ref currentName, file.RelativePath);
                Interlocked.Increment(ref copiedFiles);
            }
        }

        void ReportProgress()
        {
            var elapsedMs = watch.ElapsedMilliseconds;
            var filesDone = Volatile.Read(ref copiedFiles);
            var bytesDone = Interlocked.Read(ref copiedBytes);
            var bytesPerSecond = elapsedMs > 0 ? bytesDone * 1000.0 / elapsedMs : 0;
            TimeSpan? remaining = null;
            if (totalBytes > 0 && bytesPerSecond > 0)
            {
                remaining = TimeSpan.FromSeconds(Math.Max(0, totalBytes - bytesDone) / bytesPerSecond);
            }
            else if (filesDone > 0 && elapsedMs > 0)
            {
                var filesPerSecond = filesDone * 1000.0 / elapsedMs;
                remaining = TimeSpan.FromSeconds(Math.Max(0, total - filesDone) / filesPerSecond);
            }
            progress?.Invoke(new SafImportProgress(
                filesDone, total, bytesDone, totalBytes, bytesPerSecond, remaining,
                Volatile.Read(ref currentName) ?? string.Empty));
        }

        var reporter = progress is null
            ? Task.CompletedTask
            : Task.Run(async () =>
            {
                try
                {
                    while (!workCts.IsCancellationRequested)
                    {
                        await Task.Delay(200, workCts.Token).ConfigureAwait(false);
                        ReportProgress();
                    }
                }
                catch (OperationCanceledException)
                {
                    // 复制完成 / 失败 / 取消：停止节流上报
                }
                catch (Exception ex)
                {
                    Log.Warn(LogTag, "saf progress reporter failed: " + ex.Message);
                }
            });

        var workers = new Task[concurrency];
        for (var w = 0; w < concurrency; w++)
        {
            workers[w] = Task.Run(() =>
            {
                try
                {
                    CopyWorker();
                }
                catch (OperationCanceledException) when (workCts.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lock (failureGate)
                    {
                        firstFailure ??= ex;
                    }
                    workCts.Cancel(); // 一处失败 → 其余 worker 尽快停下
                    throw;
                }
            });
        }

        try
        {
            await Task.WhenAll(workers).ConfigureAwait(false);
        }
        catch
        {
            // 取消优先；否则抛首个真实失败（避免被其它 worker 的取消异常掩盖）。
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(cancellationToken);
            if (firstFailure is not null)
                throw firstFailure;
            throw;
        }
        finally
        {
            workCts.Cancel();
            try
            {
                await reporter.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // reporter 正常停止
            }
        }

        watch.Stop();
        ReportProgress(); // 收尾一次 100%，避免 <200ms 的导入没有进度
        Log.Info(LogTag, $"saf copy concurrency={concurrency} bufferKB={CopyBufferBytes / 1024}");

        return new SafCopyResult(total, Interlocked.Read(ref copiedBytes), Interlocked.Read(ref totalMs),
            Volatile.Read(ref smallCount), Interlocked.Read(ref smallBytes), Interlocked.Read(ref smallMs),
            Volatile.Read(ref largeCount), Interlocked.Read(ref largeBytes), Interlocked.Read(ref largeMs));
    }

    /// <summary>带缓冲的分块复制；每块前检查取消，避免取消大文件要等整个文件读完。</summary>
    private static long CopyStream(Stream input, Stream output, byte[] buffer, CancellationToken cancellationToken)
    {
        var written = 0L;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = input.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                return written;
            output.Write(buffer, 0, read);
            written += read;
        }
    }

    private static string ReadTreeDisplayName(Context context, global::Android.Net.Uri treeUri)
    {
        try
        {
            var treeDocumentId = DocumentsContract.GetTreeDocumentId(treeUri);
            if (treeDocumentId is not null)
            {
                var rootDocument = DocumentsContract.BuildDocumentUriUsingTree(treeUri, treeDocumentId);
                if (rootDocument is not null)
                {
                    using var cursor = context.ContentResolver?.Query(
                        rootDocument, new[] { "_display_name" }, null, null, null);
                    if (cursor is not null && cursor.MoveToFirst())
                    {
                        var index = cursor.GetColumnIndex("_display_name");
                        if (index >= 0)
                        {
                            var value = cursor.GetString(index);
                            if (!string.IsNullOrWhiteSpace(value))
                                return value;
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "saf root display name failed: " + ex.Message);
        }

        var documentId = DocumentsContract.GetTreeDocumentId(treeUri);
        var tail = documentId?.Split('/', ':').LastOrDefault(part => part.Length > 0);
        return tail ?? "game";
    }

    private static string? FindGameRoot(string extractedRoot)
    {
        if (IsGameDirectory(extractedRoot))
            return extractedRoot;

        string? found = null;
        foreach (var dir in Directory.GetDirectories(extractedRoot))
        {
            if (!IsGameDirectory(dir))
                continue;
            if (found is not null)
                return null; // 多个候选：让调用方按整包处理
            found = dir;
        }
        return found;
    }

    public static bool IsGameDirectory(string dir)
        => File.Exists(Path.Combine(dir, ConfigFile)) || Directory.Exists(Path.Combine(dir, "erb"));

    private static string CreateStagingDirectory(Context context)
    {
        var path = Path.Combine(GamesDirectory(context), ".import-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string SanitizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "game";
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray();
        var sanitized = new string(chars).Trim();
        return sanitized.Length == 0 ? "game" : sanitized;
    }

    private static Dictionary<string, DateTimeOffset> LoadPlayed(Context context)
    {
        var path = MetadataPath(context);
        if (!File.Exists(path))
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        try
        {
            var raw = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            var result = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            if (raw is null)
                return result;
            foreach (var (name, value) in raw)
            {
                if (DateTimeOffset.TryParse(value, out var at))
                    result[name] = at;
            }
            return result;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "library metadata read failed: " + ex.Message);
            return new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        }
    }

    private static void SavePlayed(Context context, Dictionary<string, DateTimeOffset> played)
    {
        try
        {
            var raw = played.ToDictionary(kv => kv.Key, kv => kv.Value.ToString("O"));
            File.WriteAllText(MetadataPath(context), JsonSerializer.Serialize(raw));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "library metadata write failed: " + ex.Message);
        }
    }

    private static string MetadataPath(Context context)
    {
        var filesDir = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android FilesDir 不可用。");
        return Path.Combine(filesDir, MetadataFile);
    }

    private static void DeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn(LogTag, "cleanup failed: " + ex.Message);
        }
    }
}
