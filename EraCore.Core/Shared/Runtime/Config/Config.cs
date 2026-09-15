using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.UI;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
#if !HEADLESS
using System.Windows.Forms;
#endif
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// 配置薄视图（候选 2 / ADR-0009）。
///
/// 原 <c>internal static class Config</c> 是 144 成员的静态上帝类（从 ConfigData 平铺拷贝的镜像）。
/// 现改为实例类 + ambient <see cref="Current"/>（AsyncLocal）+ 转发/计算属性，与候选 1 的
/// <see cref="GlobalStatic"/> 同构：调用方仍写 <see cref="FontName"/> 等静态属性，底层读 <see cref="Current"/>._data。
///
/// 设计要点：
/// - <see cref="Current"/> 由 <see cref="AsyncLocal{T}"/> 承载，仅在开 scope 的 async 上下文存活；
///   并行测试各自 scope 互不污染；生产单会话开销可忽略。
/// - 实例只持单字段 <c>_data</c>（ConfigData）；144 属性直接转发 _data，派生字段升为计算属性。
/// - SetConfig/SetReplace/SetDebugConfig/UpdateLangSetting 四个"拷贝方法"已坍缩——视图无副本可刷新。
/// - 纯全局常量（SCIgnoreCase/SCExpression/Encode/SaveEncode）原地保留 static。
/// </summary>
internal sealed class Config
{
	private static readonly AsyncLocal<Config?> _current = new();

	/// <summary>Ambient 会话作用域配置视图。scope 外为 null。</summary>
	public static Config? Current => _current.Value;

	/// <summary>在 scope 内绑定一份 ConfigData，返回可 Dispose 的绑定（由 GlobalStatic.OpenScope 统一管理）。</summary>
	internal static void SetCurrent(ConfigData data) => _current.Value = new Config(data);
	internal static void ClearCurrent() => _current.Value = null;

	private readonly ConfigData _data;

	private Config(ConfigData data) => _data = data;

	// ===== 纯全局常量（无会话语义，原地 static）=====

	/// <summary>文件名比较标志（eramaker 兼容，恒 OrdinalIgnoreCase）。</summary>
	public const StringComparison SCIgnoreCase = StringComparison.OrdinalIgnoreCase;

	/// <summary>式中字符串比较标志（恒 Ordinal）。</summary>
	public const StringComparison SCExpression = StringComparison.Ordinal;

	/// <summary>读/写 emuera.config 的编码（真正全局，不随会话变）。</summary>
	public static Encoding Encode = EncodingHandler.UTF8BOMEncoding;

	/// <summary>读/写 save 文件的编码（真正全局）。</summary>
	public static Encoding SaveEncode = EncodingHandler.UTF8BOMEncoding;

	// ===== 转发属性：直接读 _data（O(1)，经 ConfigData 索引）=====

	public static bool UseRenameFile => Current!._data.GetConfigValue<bool>(ConfigCode.UseRenameFile);
	public static bool UseReplaceFile => Current!._data.GetConfigValue<bool>(ConfigCode.UseReplaceFile);
	public static bool UseMouse => Current!._data.GetConfigValue<bool>(ConfigCode.UseMouse);
	public static bool UseMenu => Current!._data.GetConfigValue<bool>(ConfigCode.UseMenu);
	public static bool UseDebugCommand => Current!._data.GetConfigValue<bool>(ConfigCode.UseDebugCommand);
	public static bool AllowMultipleInstances => Current!._data.GetConfigValue<bool>(ConfigCode.AllowMultipleInstances);
	public static bool AutoSave => Current!._data.GetConfigValue<bool>(ConfigCode.AutoSave);
	public static bool UseKeyMacro => Current!._data.GetConfigValue<bool>(ConfigCode.UseKeyMacro);
	public static bool SizableWindow => Current!._data.GetConfigValue<bool>(ConfigCode.SizableWindow);
	public static TextDrawingMode TextDrawingMode => Current!._data.GetConfigValue<TextDrawingMode>(ConfigCode.TextDrawingMode);
	public static int WindowX => Current!._data.GetConfigValue<int>(ConfigCode.WindowX);
	public static int WindowY => Current!._data.GetConfigValue<int>(ConfigCode.WindowY);
	public static int WindowPosX => Current!._data.GetConfigValue<int>(ConfigCode.WindowPosX);
	public static int WindowPosY => Current!._data.GetConfigValue<int>(ConfigCode.WindowPosY);
	public static bool SetWindowPos => Current!._data.GetConfigValue<bool>(ConfigCode.SetWindowPos);
	public static int MaxLog => Current!._data.GetConfigValue<int>(ConfigCode.MaxLog);
	public static int PrintCPerLine => Current!._data.GetConfigValue<int>(ConfigCode.PrintCPerLine);
	public static int PrintCLength => Current!._data.GetConfigValue<int>(ConfigCode.PrintCLength);
	public static EmuColor ForeColor => Current!._data.GetConfigValue<EmuColor>(ConfigCode.ForeColor);
	public static EmuColor BackColor => Current!._data.GetConfigValue<EmuColor>(ConfigCode.BackColor);
	public static EmuColor FocusColor => Current!._data.GetConfigValue<EmuColor>(ConfigCode.FocusColor);
	public static EmuColor LogColor => Current!._data.GetConfigValue<EmuColor>(ConfigCode.LogColor);
	public static int FontSize => Current!._data.GetConfigValue<int>(ConfigCode.FontSize);
	public static string FontName => Current!._data.GetConfigValue<string>(ConfigCode.FontName);
	public static int LineHeight => Current!._data.GetConfigValue<int>(ConfigCode.LineHeight);
	public static int FPS => Current!._data.GetConfigValue<int>(ConfigCode.FPS);
	public static int ScrollHeight => Current!._data.GetConfigValue<int>(ConfigCode.ScrollHeight);
	public static int SaveDataNos => Current!._data.GetConfigValue<int>(ConfigCode.SaveDataNos);
	public static bool WarnBackCompatibility => Current!._data.GetConfigValue<bool>(ConfigCode.WarnBackCompatibility);
	public static bool WindowMaximixed => Current!._data.GetConfigValue<bool>(ConfigCode.WindowMaximixed);
	public static bool WarnNormalFunctionOverloading => Current!._data.GetConfigValue<bool>(ConfigCode.WarnNormalFunctionOverloading);
	public static bool SearchSubdirectory => Current!._data.GetConfigValue<bool>(ConfigCode.SearchSubdirectory);
	public static bool SortWithFilename => Current!._data.GetConfigValue<bool>(ConfigCode.SortWithFilename);
	public static bool AllowFunctionOverloading => Current!._data.GetConfigValue<bool>(ConfigCode.AllowFunctionOverloading);
	public static bool WarnFunctionOverloading => Current!._data.GetConfigValue<bool>(ConfigCode.WarnFunctionOverloading);
	public static int DisplayWarningLevel => Current!._data.GetConfigValue<int>(ConfigCode.DisplayWarningLevel);
	public static bool DisplayReport => Current!._data.GetConfigValue<bool>(ConfigCode.DisplayReport);
	/// <summary>erb-loader-parallel 03：ERB 并行加载开关（默认 true）。false = 串行 fallback（排查语义偏差/单线程调试）；AnalysisMode 一律强制串行。</summary>
	public static bool ParallelErbLoad => Current!._data.GetConfigValue<bool>(ConfigCode.ParallelErbLoad);
	/// <summary>erb-loader-parallel 08：ParseScript 未调用遍历并行开关（默认 true，与 ParallelErbLoad 正交）。false = 串行 fallback；AnalysisMode 一律强制串行。</summary>
	public static bool ParallelParseScript => Current!._data.GetConfigValue<bool>(ConfigCode.ParallelParseScript);
	/// <summary>ADR-0026 诊断旋钮：并行未调用遍历平台门策略内存覆盖（null=Auto 跟随平台门）。壳层经 Preferences 写入，无 ConfigCode 条目、游戏配置不可设置。</summary>
	public static SweepGatePolicy? OverrideSweepGatePolicy => Current?._data.OverrideSweepGatePolicy;
	/// <summary>ADR-0026 诊断旋钮：读取并发度内存覆盖（null=auto：content-URI 每树 1 并发、本地路径 -1 不受限；显式值钳制 1–64，2026-09-15 起对本地路径同样生效）。壳层经 Preferences 写入，无 ConfigCode 条目、游戏配置不可设置。</summary>
	public static int? OverrideSafReadDegree => Current?._data.OverrideSafReadDegree;
	/// <summary>ADR-0026 诊断旋钮（06-K1）：并行生成度上限内存覆盖（null=auto min(核数,8)；显式 4/8/16 取证档与核数解耦，窗口与清扫度同源同拨）。壳层经 Preferences 写入，无 ConfigCode 条目、游戏配置不可设置。</summary>
	public static int? OverrideMaxParallelErbFiles => Current?._data.OverrideMaxParallelErbFiles;
	/// <summary>ADR-0026 诊断旋钮（06-K2）：privateDimList 锁策略内存覆盖（null/Auto=恒加锁；NoLock=纯取证档，可致状态损坏）。壳层经 Preferences 写入，无 ConfigCode 条目、游戏配置不可设置。</summary>
	public static DimLockPolicy? OverrideDimLockPolicy => Current?._data.OverrideDimLockPolicy;
	public static ReduceArgumentOnLoadFlag ReduceArgumentOnLoad => Current!._data.GetConfigValue<ReduceArgumentOnLoadFlag>(ConfigCode.ReduceArgumentOnLoad);
	public static bool IgnoreUncalledFunction => Current!._data.GetConfigValue<bool>(ConfigCode.IgnoreUncalledFunction);
	/// <summary>lazy-function-load（B 线 B1）：函数级惰性物化开关，与 IgnoreUncalledFunction 正交；AnalysisMode 一律强制 eager。</summary>
	public static bool LazyFunctionLoad => Current!._data.GetConfigValue<bool>(ConfigCode.LazyFunctionLoad);
	/// <summary>b2-irdiskcache（B 线 B2）：函数粒度 IR 磁盘缓存开关（默认 true）。生效前提 = 与 LazyFunctionLoad 同为 true 且非 AnalysisMode；否则 IR 不建不读（回退硬门）。</summary>
	public static bool IrDiskCacheLoad => Current!._data.GetConfigValue<bool>(ConfigCode.IrDiskCacheLoad);
	/// <summary>issues/12 M4：命中轮用 IR 头存 firstDir 清单替代 SAF GetDirectories("*#*") 通配扫描（默认 true）；FALSE = 命中轮恒走扫描，建库轮写侧与指纹不受影响。</summary>
	public static bool IrFirstDirList => Current!._data.GetConfigValue<bool>(ConfigCode.IrFirstDirList);
	/// <summary>issues/13 M5：命中轮用清单 sidecar 回放替代全树枚举（默认 true）；仅 content-URI 游戏生效，FALSE = 恒走真实枚举，建库轮写侧不受影响。</summary>
	public static bool IrFileListCache => Current!._data.GetConfigValue<bool>(ConfigCode.IrFileListCache);
	/// <summary>issues/10 10-B：CSV/ERH 源内容缓存（era_src.dat，默认 true）；仅 content-URI 游戏生效，FALSE = 命中轮回到懒读现状。</summary>
	public static bool SrcContentCache => Current!._data.GetConfigValue<bool>(ConfigCode.SrcContentCache);
	public static DisplayWarningFlag FunctionNotFoundWarning => Current!._data.GetConfigValue<DisplayWarningFlag>(ConfigCode.FunctionNotFoundWarning);
	public static DisplayWarningFlag FunctionNotCalledWarning => Current!._data.GetConfigValue<DisplayWarningFlag>(ConfigCode.FunctionNotCalledWarning);
	public static bool ChangeMasterNameIfDebug => Current!._data.GetConfigValue<bool>(ConfigCode.ChangeMasterNameIfDebug);
	public static long LastKey => Current!._data.GetConfigValue<long>(ConfigCode.LastKey);
	public static bool ButtonWrap => Current!._data.GetConfigValue<bool>(ConfigCode.ButtonWrap);
	public static string TextEditor => Current!._data.GetConfigValue<string>(ConfigCode.TextEditor);
	public static TextEditorType EditorType => Current!._data.GetConfigValue<TextEditorType>(ConfigCode.EditorType);
	public static string EditorArg => Current!._data.GetConfigValue<string>(ConfigCode.EditorArgument);
	public static bool CompatiErrorLine => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiErrorLine);
	public static bool CompatiCALLNAME => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiCALLNAME);
	public static bool UseSaveFolder => Current!._data.GetConfigValue<bool>(ConfigCode.UseSaveFolder);
	public static bool CompatiRAND => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiRAND);
	public static bool CompatiLinefeedAs1739 => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiLinefeedAs1739);
	public static bool SystemAllowFullSpace => Current!._data.GetConfigValue<bool>(ConfigCode.SystemAllowFullSpace);
	public static bool SystemSaveInBinary => Current!._data.GetConfigValue<bool>(ConfigCode.SystemSaveInBinary);
	public static bool CompatiFuncArgAutoConvert => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiFuncArgAutoConvert);
	public static bool CompatiFuncArgOptional => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiFuncArgOptional);
	public static bool CompatiCallEvent => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiCallEvent);
	public static bool CompatiSPChara => Current!._data.GetConfigValue<bool>(ConfigCode.CompatiSPChara);
	public static bool SystemIgnoreTripleSymbol => Current!._data.GetConfigValue<bool>(ConfigCode.SystemIgnoreTripleSymbol);
	public static bool SystemNoTarget => Current!._data.GetConfigValue<bool>(ConfigCode.SystemNoTarget);
	public static bool SystemIgnoreStringSet => Current!._data.GetConfigValue<bool>(ConfigCode.SystemIgnoreStringSet);
	public static bool AllowLongInputByMouse => Current!._data.GetConfigValue<bool>(ConfigCode.AllowLongInputByMouse);
	public static bool TimesNotRigorousCalculation => Current!._data.GetConfigValue<bool>(ConfigCode.TimesNotRigorousCalculation);
	public static bool ForbidUpdateCheck => Current!._data.GetConfigValue<bool>(ConfigCode.ForbidUpdateCheck);
	public static bool UseERD => Current!._data.GetConfigValue<bool>(ConfigCode.UseERD);
	public static bool VarsizeDimConfig => Current!._data.GetConfigValue<bool>(ConfigCode.VarsizeDimConfig);
	public static bool CheckDuplicateIdentifier => Current!._data.GetConfigValue<bool>(ConfigCode.CheckDuplicateIdentifier);
	public static string ReplaceContinuationBR => Current!._data.GetConfigValue<string>(ConfigCode.ReplaceContinuationBR);
	public static List<string> ValidExtension => Current!._data.GetConfigValue<List<string>>(ConfigCode.ValidExtension);
	public static bool ZipSaveData => Current!._data.GetConfigValue<bool>(ConfigCode.ZipSaveData);
	public static bool EnglishConfigOutput => Current!._data.GetConfigValue<bool>(ConfigCode.EnglishConfigOutput);
	public static string EmueraLang => Current!._data.GetConfigValue<string>(ConfigCode.EmueraLang);
	public static string EmueraIcon => Current!._data.GetConfigValue<string>(ConfigCode.EmueraIcon);
	public static bool CBUseClipboard => Current!._data.GetConfigValue<bool>(ConfigCode.CBUseClipboard);
	public static bool CBIgnoreTags => Current!._data.GetConfigValue<bool>(ConfigCode.CBIgnoreTags);
	public static string CBReplaceTags => Current!._data.GetConfigValue<string>(ConfigCode.CBReplaceTags);
	public static bool CBNewLinesOnly => Current!._data.GetConfigValue<bool>(ConfigCode.CBNewLinesOnly);
	public static bool CBClearBuffer => Current!._data.GetConfigValue<bool>(ConfigCode.CBClearBuffer);
	public static bool CBTriggerLeftClick => Current!._data.GetConfigValue<bool>(ConfigCode.CBTriggerLeftClick);
	public static bool CBTriggerMiddleClick => Current!._data.GetConfigValue<bool>(ConfigCode.CBTriggerMiddleClick);
	public static bool CBTriggerDoubleLeftClick => Current!._data.GetConfigValue<bool>(ConfigCode.CBTriggerDoubleLeftClick);
	public static bool CBTriggerAnyKeyWait => Current!._data.GetConfigValue<bool>(ConfigCode.CBTriggerAnyKeyWait);
	public static bool CBTriggerInputWait => Current!._data.GetConfigValue<bool>(ConfigCode.CBTriggerInputWait);
	public static int CBMaxCB => Current!._data.GetConfigValue<int>(ConfigCode.CBMaxCB);
	public static int CBBufferSize => Current!._data.GetConfigValue<int>(ConfigCode.CBBufferSize);
	public static int CBScrollCount => Current!._data.GetConfigValue<int>(ConfigCode.CBScrollCount);
	public static int CBMinTimer => Current!._data.GetConfigValue<int>(ConfigCode.CBMinTimer);
	public static bool RikaiEnabled => Current!._data.GetConfigValue<bool>(ConfigCode.RikaiEnabled);
	public static string RikaiFilename => Current!._data.GetConfigValue<string>(ConfigCode.RikaiFilename);
	public static EmuColor RikaiColorBack => Current!._data.GetConfigValue<EmuColor>(ConfigCode.RikaiColorBack);
	public static EmuColor RikaiColorText => Current!._data.GetConfigValue<EmuColor>(ConfigCode.RikaiColorText);
	public static bool RikaiUseSeparateBoxes => Current!._data.GetConfigValue<bool>(ConfigCode.RikaiUseSeparateBoxes);
	public static bool Ctrl_Z_Enabled => Current!._data.GetConfigValue<bool>(ConfigCode.Ctrl_Z_Enabled);
	public static bool DebugShowWindow => Current!._data.GetConfigValue<bool>(ConfigCode.DebugShowWindow);
	public static bool DebugWindowTopMost => Current!._data.GetConfigValue<bool>(ConfigCode.DebugWindowTopMost);
	public static int DebugWindowWidth => Current!._data.GetConfigValue<int>(ConfigCode.DebugWindowWidth);
	public static int DebugWindowHeight => Current!._data.GetConfigValue<int>(ConfigCode.DebugWindowHeight);
	public static bool DebugSetWindowPos => Current!._data.GetConfigValue<bool>(ConfigCode.DebugSetWindowPos);
	public static int DebugWindowPosX => Current!._data.GetConfigValue<int>(ConfigCode.DebugWindowPosX);
	public static int DebugWindowPosY => Current!._data.GetConfigValue<int>(ConfigCode.DebugWindowPosY);
	public static string MoneyLabel => Current!._data.GetConfigValue<string>(ConfigCode.MoneyLabel);
	public static bool MoneyFirst => Current!._data.GetConfigValue<bool>(ConfigCode.MoneyFirst);
	public static string LoadLabel => Current!._data.GetConfigValue<string>(ConfigCode.LoadLabel);
	public static int MaxShopItem => Current!._data.GetConfigValue<int>(ConfigCode.MaxShopItem);
	public static string DrawLineString
	{
		get
		{
			string v = Current!._data.GetConfigValue<string>(ConfigCode.DrawLineString);
			return string.IsNullOrEmpty(v) ? "-" : v;
		}
	}
	public static char BarChar1 => Current!._data.GetConfigValue<char>(ConfigCode.BarChar1);
	public static char BarChar2 => Current!._data.GetConfigValue<char>(ConfigCode.BarChar2);
	public static string TitleMenuString0 => Current!._data.GetConfigValue<string>(ConfigCode.TitleMenuString0);
	public static string TitleMenuString1 => Current!._data.GetConfigValue<string>(ConfigCode.TitleMenuString1);
	public static int ComAbleDefault => Current!._data.GetConfigValue<int>(ConfigCode.ComAbleDefault);
	public static List<long> StainDefault => Current!._data.GetConfigValue<List<long>>(ConfigCode.StainDefault);
	public static string TimeupLabel => Current!._data.GetConfigValue<string>(ConfigCode.TimeupLabel);
	public static List<long> ExpLvDef => Current!._data.GetConfigValue<List<long>>(ConfigCode.ExpLvDef);
	public static List<long> PalamLvDef => Current!._data.GetConfigValue<List<long>>(ConfigCode.PalamLvDef);
	public static long PbandDef => Current!._data.GetConfigValue<long>(ConfigCode.pbandDef);
	public static long RelationDef => Current!._data.GetConfigValue<long>(ConfigCode.RelationDef);

	// ===== 计算属性：从 _data 派生（无副本，现算现用）=====

	/// <summary>函数/属性名比较的 IgnoreCase 标志（derived）。</summary>
	public static bool IgnoreCase => Current!._data.GetConfigValue<bool>(ConfigCode.IgnoreCase);

	/// <summary>函数/属性名比较标志（derived）。</summary>
	public static StringComparison StringComparison => IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

	/// <summary>文件名比较标志（derived）。</summary>
	public static StringComparer StrComper => IgnoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

	/// <summary>GDI+ 字符串与图形位置偏移修正（derived）。</summary>
	public static int DrawingParam_ShapePositionShift => TextDrawingMode == TextDrawingMode.WINAPI ? 0 : Math.Max(2, FontSize / 6);

	/// <summary>实际可绘制宽度（derived）。</summary>
	public static int DrawableWidth => WindowX - DrawingParam_ShapePositionShift;

	/// <summary>强制存档目录（derived）。SAF 下走 ResolveSubPath(ExeDir, "sav")，禁止字符串拼接。</summary>
	public static string ForceSavDir => SafCompat.ResolveSubPath(Program.ExeDir, "sav");

	/// <summary>存档目录（derived）。跟 WinForms：UseSaveFolder 时 sav/，否则游戏根。</summary>
	public static string SavDir => UseSaveFolder ? ForceSavDir : Program.ExeDir;

	/// <summary>语言 LCID（derived，值由 ConfigData.ApplyPostLoadEffects 经 useLanguage 计算并缓存）。</summary>
	public static int Language => Current!._data.Language;

	/// <summary>加载期是否需要缩减参数（derived/缓存，由 ConfigData.CheckUpdate 设置）。</summary>
	public static bool NeedReduceArgumentOnLoad => Current!._data.NeedReduceArgumentOnLoad;

	/// <summary>默认字体（无 GDI，Headless 返回 EmuFont 值类型）。</summary>
	public static EmuFont DefaultFont => FontFactory.GetFont("", EmuFontStyle.Regular);

	// ===== 实例方法（读 ambient，含副作用/文件系统）=====

	/// <summary>配置项显示名（用于错误提示）。</summary>
	public static string GetConfigName(ConfigCode code) => Current!._data.GetItem(code)?.Text ?? "";

	/// <summary>强制创建 ForceSavDir。</summary>
	public static void ForceCreateSavDir()
	{
		if (!SafCompat.DirectoryExists(ForceSavDir))
			SafCompat.CreateDirectory(ForceSavDir);
	}

	/// <summary>按 UseSaveFolder 创建 SavDir。</summary>
	public static void CreateSavDir()
	{
		if (UseSaveFolder && !SafCompat.DirectoryExists(SavDir))
			SafCompat.CreateDirectory(SavDir);
	}



	/// <summary>语言设置：把语言写入 ConfigData 并保存（不再回填静态字段，视图自动反射）。</summary>
	public static void SetLanguageSetting(ConfigData instance, string lang)
	{
		instance.GetConfigItem(ConfigCode.EmueraLang).SetValue(lang);
		if (!instance.SaveConfig())
			Dialog.Show(trmb.ConfigError.Text, trmb.ConfigSaveFailure.Text);
	}

	/// <summary>KeyValuePair&lt;相对路径, 完全路径&gt; 列表。</summary>
	public static List<KeyValuePair<string, string>> GetFiles(string rootdir, string pattern)
	{
		return getFiles(rootdir, rootdir, pattern, !SearchSubdirectory, SortWithFilename);
	}

	public static List<KeyValuePair<string, string>> GetFiles(string dir, string rootdir, string pattern)
	{
		return getFiles(dir, rootdir, pattern, !SearchSubdirectory, SortWithFilename);
	}

	private static List<KeyValuePair<string, string>> getFiles(string dir, string rootdir, string pattern, bool toponly, bool sort)
	{
		List<KeyValuePair<string, string>> retList = [];

		// SAF：相对路径与短名必须走 SafPath（documentId 内 %2F），不能对 content URI 做字符串切片 / Path.GetFileName
		string RelativePath;
		if (string.Equals(dir, rootdir, StringComparison.OrdinalIgnoreCase))
			RelativePath = "";
		else if (SafPath.IsContentUri(dir) && SafPath.IsContentUri(rootdir))
		{
			RelativePath = SafPath.GetRelativePathFromRoot(rootdir, dir);
			if (!string.IsNullOrEmpty(RelativePath) && !RelativePath.EndsWith('\\') && !RelativePath.EndsWith('/'))
				RelativePath += "\\";
		}
		else
		{
			if (!dir.StartsWith(rootdir, StringComparison.OrdinalIgnoreCase))
				RelativePath = dir;
			else
				RelativePath = dir[rootdir.Length..];
			if (!RelativePath.EndsWith('\\') && !RelativePath.EndsWith('/'))
				RelativePath += "\\";
		}
		string[] filepaths = GamePaths.Current.DirAccessor.GetFiles(dir, pattern, SearchOption.TopDirectoryOnly);
		if (sort)
			Array.Sort(filepaths);
		for (int i = 0; i < filepaths.Length; i++)
			if (Path.GetExtension(SafPath.GetLogicalFileName(filepaths[i])).Length <= 4)
				retList.Add(new KeyValuePair<string, string>(Path.Combine(RelativePath, SafPath.GetLogicalFileName(filepaths[i])), filepaths[i]));

		if (!toponly)
		{
			string[] dirList = GamePaths.Current.DirAccessor.GetDirectories(dir);
			if (dirList.Length > 0)
			{
				if (sort)
					Array.Sort(dirList);
				for (int i = 0; i < dirList.Length; i++)
					retList.AddRange(getFiles(dirList[i], rootdir, pattern, toponly, sort));
			}
		}

		return retList;
	}
}
