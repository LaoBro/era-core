using System;
using System.IO;
using System.Linq;
using System.Reflection;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;
using Xunit;

namespace MinorShift.Emuera.Tests;

[Collection("LoaderTests")]
public class SafStage2StorageTests
{
	private const string Root =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FTK";
	private const string OtherRoot =
		"content://com.android.externalstorage.documents/tree/primary%3Aemuera/document/primary%3Aemuera%2FOther";

    [Fact]
	public void Config_and_macro_saves_write_to_the_SAF_game_root()
    {
        var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
        using var gamePaths = new GamePathsScope(Root, accessor);
        using var globals = GlobalStatic.OpenScope(new ConfigData());
        using var macro = new KeyMacroScope();

		Assert.True(ConfigData.Current!.SaveConfig());
		Assert.True(accessor.FileExists(SafCompat.CombinePath(Root, "emuera.config")));
		using var console = new EmueraConsole(new HeadlessConsole(), new PosixTerminalSetup());
		Assert.True(console.OutputLog("logs/output.log", hideInfo: true));
		Assert.True(accessor.FileExists(SafCompat.TryResolvePathUnderGameRoot("logs/output.log", false, out var outputLogPath)
			? outputLogPath
			: throw new InvalidOperationException("Expected log path to resolve.")));

		KeyMacro.SetMacro(0, 0, "PRINTL saved through SAF");
        Assert.True(KeyMacro.SaveMacro());
		Assert.True(accessor.FileExists(SafCompat.CombinePath(Root, "macro.txt")));
	}

	/// <summary>
	/// 加载一个没有 emuera.config 的 SAF 游戏：**不得创建**该文件——写盘政策是"玩家未手动设置时
	/// 绝不修改游戏目录"，缺失时全部走内存默认值（原版 Emuera 会创建它自己的那份）。
	/// 同时断言加载不会跨游戏根写入：另一个根下既有的 config 不受影响。
	/// </summary>
	[Fact]
	public void Loading_a_new_SAF_game_does_not_create_a_config()
	{
		var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(Root, accessor);
		var config = new ConfigData();
		using var globals = GlobalStatic.OpenScope(config);
		// 显式保存（唯一允许的写盘入口）在 Root 下建出一份，作为"别动我"的对照物
		Assert.True(config.SaveConfig());
		Assert.True(accessor.FileExists(SafCompat.CombinePath(Root, "emuera.config")));

		_ = GamePaths.Resolve(OtherRoot, accessor);
		Assert.True(config.LoadConfig(OtherRoot));

		Assert.False(accessor.FileExists(SafCompat.CombinePath(OtherRoot, "emuera.config")));
		Assert.True(accessor.FileExists(SafCompat.CombinePath(Root, "emuera.config")));
	}

    [Fact]
    public void Log_path_resolves_nested_relative_names_under_the_SAF_game_root()
    {
        var accessor = new SafCompatContentUriTests.InMemoryContentDirAccessor(Root);
		using var gamePaths = new GamePathsScope(Root, accessor);

        Assert.True(SafCompat.TryResolvePathUnderGameRoot("logs/init.log", createParentDirectories: true, out var path));
        Assert.Equal("init.log", SafPath.GetLogicalFileName(path));
        Assert.True(accessor.DirectoryExists(SafCompat.ResolveSubPath(Root, "logs")));
        Assert.False(SafCompat.TryResolvePathUnderGameRoot("../init.log", createParentDirectories: true, out _));
    }

    [Fact]
    public void Json_settings_are_persisted_in_app_private_storage()
    {
        using var appData = new TempDir();
        using var scope = new AppDataScope(appData.Root);
        var previous = JSONConfig.Data;
        try
        {
            JSONConfig.Data = new JSONConfigData();
            JSONConfig.Save();

            Assert.True(File.Exists(Path.Combine(appData.Root, "setting.json")));
            JSONConfig.Load();
            Assert.NotNull(JSONConfig.Data);
        }
        finally
        {
            JSONConfig.Data = previous;
        }
    }

    private sealed class GamePathsScope : IDisposable
    {
        private readonly GamePaths previous = GamePaths.Current;

		public GamePathsScope(string root, IGameDirAccessor accessor)
		{
			_ = GamePaths.Resolve(root, accessor);
        }

        public void Dispose() => GamePaths.SetCurrent(previous);
    }

    private sealed class AppDataScope : IDisposable
    {
        private readonly string previous = AppDataPaths.Directory;

        public AppDataScope(string path) => AppDataPaths.Configure(path);

        public void Dispose() => AppDataPaths.Configure(previous);
    }

    private sealed class KeyMacroScope : IDisposable
    {
        private static readonly FieldInfo Macro = GetField("macro");
        private static readonly FieldInfo MacroName = GetField("macroName");
        private static readonly FieldInfo GroupName = GetField("groupName");
        private static readonly FieldInfo IsChanged = GetField("isMacroChanged");

        private readonly string[] macro = ((string[])Macro.GetValue(null)!).ToArray();
        private readonly string[] macroName = ((string[])MacroName.GetValue(null)!).ToArray();
        private readonly string[] groupName = ((string[])GroupName.GetValue(null)!).ToArray();
        private readonly bool isChanged = (bool)IsChanged.GetValue(null)!;

        public void Dispose()
        {
            Macro.SetValue(null, macro);
            MacroName.SetValue(null, macroName);
            GroupName.SetValue(null, groupName);
            IsChanged.SetValue(null, isChanged);
        }

        private static FieldInfo GetField(string name) => typeof(KeyMacro).GetField(
            name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingFieldException(typeof(KeyMacro).FullName, name);
    }

    private sealed class TempDir : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "emuera-stage2-" + Guid.NewGuid().ToString("N"));

        public TempDir() => Directory.CreateDirectory(Root);

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Root))
                    Directory.Delete(Root, recursive: true);
            }
            catch { }
        }
    }
}
