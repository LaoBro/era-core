using MinorShift.Emuera.Files;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;
using Xunit;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// 配置读/写侧政策：**产品代码里没有任何自动 <c>SaveConfig</c> 调用方**——加载期三源合并、语言探测
/// （<c>Lang.SetLanguage</c>）、<c>/load-game</c>、设置页都不得触碰玩家游戏目录里的 emuera.config，
/// 玩家未手动设置时该文件必须逐字节不变。
///
/// <para>
/// 背景：EraCore 曾因语言兜底分支（<c>SetLanguageSetting</c> 内嵌 SaveConfig）把整份 config 以
/// <b>日文键名</b>回写，并丢掉其它本地化分支的专有键；原版简体中文 Emuera 随后读该文件时认不出键名、
/// 全部回落默认（东亚语言 = JAPANESE）→ 无 BOM 的 UTF-8 资源按 CP932 解码 → 满屏「无法解释标识符」。
/// </para>
///
/// 配套钉子：
/// <list type="bullet">
/// <item>中文键名经 <see cref="ConfigKeyAliases"/> 别名表被识别（值真正生效，不只是"不报未知"）；</item>
/// <item>未识别键登记进 <see cref="ConfigData.UnknownConfigKeys"/>（发聚合告警而非静默丢弃）；</item>
/// <item>文件编码不是 UTF-8 时另发一条专门告警（与"作者写了别的键名"区分）；</item>
/// <item>主 config 的未识别原始行在**显式** SaveConfig 时原样回写（不抹掉其它分支的专有键）；</item>
/// <item>ONCE 的变更判据改寄生到 IR 指纹：<see cref="ConfigData.ApplyReduceArgumentOnLoad"/> 真值表。</item>
/// </list>
/// </summary>
[Collection("LoaderTests")]
public class ConfigWritePolicyTests
{
	/// <summary>
	/// 简体中文分支写出的键名（前 3 行 + useLanguage 现已由别名表识别），末行是日文键名、含 ONCE
	/// ——旧实现下 ONCE 会因「最終更新コード 失配」触发整份重写，是本次回归的关键触发器。
	/// </summary>
	private const string CnStyleConfig =
		"忽略大小写:YES\n" +
		"启用鼠标:YES\n" +
		"在sav文件夹中创建存档:YES\n" +
		"记录日志的行数:10000\n" +
		"窗口宽度:1330\n" +
		"内部使用东亚语言:CHINESE_HANS\n" +
		"ロード時に引数を解析する:ONCE\n";

	/// <summary>
	/// 含**本引擎确实没有**的键（中文分支独有的 UTF-8 开关 + 本分支已移除的两项）——
	/// 用于钉"登记 + 聚合告警 + 显式保存原样回写"，这些键即使填了别名表也仍应无法识别。
	/// </summary>
	private const string ConfigWithUnsupportedKeys =
		"忽略大小写:YES\n" +
		"全部以UTF-8编码读入文件:YES\n" +
		"全部以UTF-8编码输出文件:YES\n" +
		"以UTF-8格式保存存档:NO\n" +
		"死循环超时警告(毫秒):5000\n";

	[Fact]
	public void LoadConfig_never_writes_the_game_config()
	{
		using var game = new TempGameDir();
		string configPath = game.Write("emuera.config", CnStyleConfig);
		byte[] before = File.ReadAllBytes(configPath);
		DateTime lastWriteBefore = File.GetLastWriteTimeUtc(configPath);

		Assert.True(game.Load());

		Assert.Equal(before, File.ReadAllBytes(configPath));                 // 逐字节不变
		Assert.Equal(lastWriteBefore, File.GetLastWriteTimeUtc(configPath)); // 连时间戳都没动
	}

	[Fact]
	public void LoadConfig_does_not_create_a_missing_config()
	{
		using var game = new TempGameDir();

		Assert.True(game.Load());

		// 文件缺失时全部走内存默认值；不替玩家创建 config（原版 Emuera 会创建它自己的那份）
		Assert.False(File.Exists(Path.Combine(game.Root, "emuera.config")));
	}

	/// <summary>
	/// 别名不是"不报未知"而已——中文键名的值必须真正落到对应配置项上。
	/// 这几项正是当初读不到值导致行为偏差的典型（存档目录 / 东亚语言 / 日志行数 / 窗口宽度）。
	/// </summary>
	[Fact]
	public void Chinese_keys_actually_apply_through_aliases()
	{
		using var game = new TempGameDir();
		game.Write("emuera.config", CnStyleConfig);

		Assert.True(game.Load());

		var config = ConfigData.Current!;
		Assert.True(config.GetConfigValue<bool>(ConfigCode.UseSaveFolder), "「在sav文件夹中创建存档」应生效");
		Assert.Equal(UseLanguage.CHINESE_HANS, config.GetConfigValue<UseLanguage>(ConfigCode.useLanguage));
		Assert.Equal(10000, config.GetConfigValue<int>(ConfigCode.MaxLog));
		Assert.Equal(1330, config.GetConfigValue<int>(ConfigCode.WindowX));
		// 别名表命中后就不该再出现在"未知键"名单里
		Assert.DoesNotContain(config.UnknownConfigKeys, k => k.Key == "忽略大小写");
	}

	[Fact]
	public void Unrecognized_keys_are_collected_not_dropped()
	{
		using var game = new TempGameDir();
		game.Write("emuera.config", ConfigWithUnsupportedKeys);

		Assert.True(game.Load());

		var keys = ConfigData.Current!.UnknownConfigKeys;
		// 本引擎确实没有的键仍要登记（别名表只覆盖"本引擎有的项"）
		Assert.Contains(keys, k => k.Key == "全部以UTF-8编码读入文件");
		Assert.Contains(keys, k => k.Key == "死循环超时警告(毫秒)");
		// 已由别名识别的键不得进名单（否则告警会误报）
		Assert.DoesNotContain(keys, k => k.Key == "忽略大小写");
		// 行号与来源文件都要能定位
		Assert.All(keys, k => Assert.Equal("emuera.config", k.File));
		Assert.Contains(keys, k => k.Key == "全部以UTF-8编码读入文件" && k.Line == 2);
	}

	[Fact]
	public void Explicit_save_round_trips_unrecognized_lines()
	{
		using var game = new TempGameDir();
		game.Write("emuera.config", ConfigWithUnsupportedKeys);

		Assert.True(game.Load());
		Assert.True(ConfigData.Current!.SaveConfig());

		string saved = File.ReadAllText(Path.Combine(game.Root, "emuera.config"), Encoding.UTF8);
		// 本引擎不认识的专有键必须原样保留——显式保存也不得抹掉别人的行
		Assert.Contains("全部以UTF-8编码读入文件:YES", saved);
		Assert.Contains("全部以UTF-8编码输出文件:YES", saved);
		Assert.Contains("死循环超时警告(毫秒):5000", saved);
		// 已识别的项按本引擎的键名（Text）规范化写出——别名是读侧映射，不是写侧改名
		Assert.Contains("大文字小文字の違いを無視する:YES", saved);
	}

	/// <summary>
	/// 编码不是 UTF-8（如 GBK 写出的中文键名配置）时必须**专门告警**：键名解不出来是编码问题，
	/// 不是"作者写了别的键名"。此路径同时验证解码抛异常不会打崩启动。
	/// </summary>
	[Fact]
	public void Non_utf8_config_reports_an_encoding_warning()
	{
		Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
		using var game = new TempGameDir();
		File.WriteAllBytes(
			Path.Combine(game.Root, "emuera.config"),
			Encoding.GetEncoding(936).GetBytes(CnStyleConfig)); // GBK

		var warnings = new List<string>();
		EmueraLog.SetPlatformSink((level, line) =>
		{
			if (level >= EmueraLog.Level.Warn)
				warnings.Add(line);
		});
		try
		{
			_ = game.Load(); // 不崩即达标（解码失败由 loadConfig 兜成告警）
		}
		finally
		{
			EmueraLog.SetPlatformSink(null);
		}

		Assert.Contains(warnings, l => l.Contains("编码不是 UTF-8"));
	}

	/// <summary>
	/// ONCE 的变更判据改寄生到 IR 指纹后的真值表（<c>ReduceArgumentOnLoadFlag</c> 是 internal，
	/// 故 InlineData 传 int 再转换）。
	/// </summary>
	[Theory]
	[InlineData((int)ReduceArgumentOnLoadFlag.YES, true, true)]
	[InlineData((int)ReduceArgumentOnLoadFlag.YES, false, true)]
	[InlineData((int)ReduceArgumentOnLoadFlag.NO, true, false)]
	[InlineData((int)ReduceArgumentOnLoadFlag.NO, false, false)]
	[InlineData((int)ReduceArgumentOnLoadFlag.ONCE, true, false)]  // IR 命中 = 源未变 → 跳过全量参数解析
	[InlineData((int)ReduceArgumentOnLoadFlag.ONCE, false, true)]  // 未命中（首次/源变更/IR 未启用）→ 强制全量
	public void ApplyReduceArgumentOnLoad_truth_table(int flag, bool irHit, bool expected)
	{
		var config = new ConfigData();

		config.ApplyReduceArgumentOnLoad((ReduceArgumentOnLoadFlag)flag, irHit);

		Assert.Equal(expected, config.NeedReduceArgumentOnLoad);
	}

	/// <summary>临时游戏目录：<c>Load()</c> 复刻生产加载时序（Resolve → new ConfigData → OpenScope → LoadConfig）。</summary>
	private sealed class TempGameDir : IDisposable
	{
		public string Root { get; }
		private IDisposable? _scope;

		public TempGameDir()
		{
			Root = Path.Combine(Path.GetTempPath(), "emuera-configwrite-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(Root);
			// 旧实现的 getUpdateKey 会枚举 erb/csv（缺失目录可能抛错），补齐以让回归对照成立
			Directory.CreateDirectory(Path.Combine(Root, "erb"));
			Directory.CreateDirectory(Path.Combine(Root, "csv"));
		}

		/// <summary>写一个相对名文件（父目录自动创建），返回绝对路径。</summary>
		public string Write(string relativeName, string content)
		{
			string path = Path.Combine(Root, relativeName.Replace('/', Path.DirectorySeparatorChar));
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, content, new UTF8Encoding(false));
			return path;
		}

		public bool Load()
		{
			var paths = GamePaths.Resolve(Root, new FileSystemGameDirAccessor());
			var config = new ConfigData(paths);
			_scope = GlobalStatic.OpenScope(config);
			return config.LoadConfig();
		}

		public void Dispose()
		{
			_scope?.Dispose();
			try
			{
				if (Directory.Exists(Root))
					Directory.Delete(Root, recursive: true);
			}
			catch { /* best-effort */ }
		}
	}
}