using Android.Content;
using Android.Content.Res;
using Android.Util;

namespace EraCore.Native.Android;

/// <summary>
/// 把 APK 内置的 <c>Assets/test_game</c> 解压到应用私有目录，供 <see cref="MinorShift.Emuera.Files.FileSystemGameDirAccessor"/>
/// 以真实路径访问（引擎需要可写目录放存档 / 日志）。带标记文件避免每次启动覆盖玩家存档。
/// </summary>
internal static class AndroidGameAssets
{
    private const string LogTag = "EraCoreNative";
    private const string AssetRoot = "test_game";
    private const string GameDirName = "test_game";
    private const string MarkerName = ".extracted-v1";

    public static string EnsureGame(Context context)
    {
        var filesDir = context.FilesDir?.AbsolutePath
            ?? throw new InvalidOperationException("Android FilesDir 不可用。");
        var root = Path.Combine(filesDir, "games", GameDirName);
        var marker = Path.Combine(root, MarkerName);

        if (File.Exists(marker))
        {
            Log.Info(LogTag, "game assets already extracted: " + root);
            return root;
        }

        if (Directory.Exists(root))
            Directory.Delete(root, recursive: true);
        Directory.CreateDirectory(root);

        var started = DateTime.UtcNow;
        CopyDirectory(context.Assets!, AssetRoot, root);
        File.WriteAllText(marker, DateTime.UtcNow.ToString("O"));

        Log.Info(LogTag, $"extracted assets to {root} in {(DateTime.UtcNow - started).TotalMilliseconds:F0} ms");
        return root;
    }

    private static void CopyDirectory(AssetManager assets, string assetPath, string destPath)
    {
        var children = assets.List(assetPath);
        if (children is { Length: > 0 })
        {
            Directory.CreateDirectory(destPath);
            foreach (var child in children)
                CopyDirectory(assets, assetPath + "/" + child, Path.Combine(destPath, child));
            return;
        }

        // List 为空既可能是空目录也可能是文件：能打开就是文件，否则按空目录处理。
        try
        {
            using var input = assets.Open(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            using var output = File.Create(destPath);
            input.CopyTo(output);
        }
        catch (Java.IO.FileNotFoundException)
        {
            Directory.CreateDirectory(destPath);
        }
    }
}
