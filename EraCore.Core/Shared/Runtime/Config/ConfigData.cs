using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script.Statements.Expression;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Sub;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using trerror = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.Error;
using trmb = MinorShift.Emuera.Runtime.Utils.EvilMask.Lang.MessageBox;
#if !HEADLESS
using System.Windows.Forms;
#endif

namespace MinorShift.Emuera.Runtime.Config;

/// <summary>
/// プログラム全体で使用される値でWindow作成前に設定して以後変更されないもの
/// (という予定だったが今は違う)
/// 1756 Config → ConfigDataへ改名
/// </summary>
internal sealed class ConfigData
{
	#region eee_カレントディレクトリー
	// Issue 12：configPath 从 static readonly 改为实例 readonly 字段——
	// /load-game 切换游戏目录后，新 ConfigData 必须读取新目录的 emuera.config。
	// 构造时根据当前 GamePaths.Current.ExeDir（以分隔符结尾）确定；
	// 启动期 / CLI 无 GamePaths 时 fallback 到 Program.ExeDir。
	readonly string configPath;
	#endregion
	private static string ConfigDebugPath => SafCompat.CombinePath(Program.DebugDir, "debug.config");

	private static readonly AsyncLocal<ConfigData?> _current = new();
	public static ConfigData? Current => _current.Value;
	internal static void SetCurrent(ConfigData data) => _current.Value = data;
	internal static void ClearCurrent() => _current.Value = null;

	private readonly Dictionary<ConfigCode, AConfigItem> _index = new();
	public bool NeedReduceArgumentOnLoad { get; private set; }
	public int Language { get; private set; }

	/// <summary>
	/// 覆盖游戏配置的加载时显示报告（<see cref="ConfigCode.DisplayReport"/>）——三态：
	/// null=不覆盖（默认，保持游戏作者行为）；false=强制关（<c>--no-loading-report</c>，
	/// agent 场景逐文件报告对回合输出是噪音）；true=强制开（<c>--loading-report</c>，
	/// 性能测量需要 time.log 阶段计时时用）。
	/// <see cref="LoadConfigCore"/> 三源合并后压过一切来源；仅内存覆盖，不落盘。
	/// </summary>
	public bool? OverrideDisplayReport { get; set; }

	/// <summary>
	/// 诊断旋钮（ADR-0026）：并行未调用遍历平台门策略的内存覆盖——null=不覆盖（默认，
	/// <see cref="SweepGatePolicy.Auto"/> 跟随平台门）；ForceOn/ForceOff 由 app 壳层经
	/// Preferences 写入，ErbLoader.ParseScript 读并提供给
	/// <c>ShouldRunParallelSweep</c>（唯一决策点）。无对应 ConfigCode——游戏随包的
	/// emuera.config 无法设置（loadConfig 对未知键静默跳过）；仅内存覆盖，不落盘。
	/// </summary>
	public SweepGatePolicy? OverrideSweepGatePolicy { get; set; }

	/// <summary>
	/// 诊断旋钮（ADR-0026）：SAF content-URI 读取并发度的内存覆盖——null=不覆盖（默认，
	/// 每树 1 并发，与止血值逐位一致）；显式数值（钳制 1–64）由 app 壳层经 Preferences 写入，
	/// Preload.Load 读取并经 <c>preload_policy</c> 日志自证，真机崩溃阈值二分取证用。仅绑
	/// content-URI 读取分支，桌面文件系统路径不受限。无对应 ConfigCode——游戏随包的
	/// emuera.config 无法设置（loadConfig 对未知键静默跳过）；仅内存覆盖，不落盘。
	/// </summary>
	public int? OverrideSafReadDegree { get; set; }

	/// <summary>
	/// 诊断旋钮（ADR-0026，06-K1）：并行生成度上限的内存覆盖——null=不覆盖（默认 min(核数,8)，
	/// 与现状逐位一致）；显式 4/8/16（词表见 <see cref="MaxParallelErbFilesCodec"/>）=取证档直接生效
	/// （与核数解耦），ERB 生成窗口与未调用遍历清扫度同源同拨。无对应 ConfigCode——游戏随包的
	/// emuera.config 无法设置（loadConfig 对未知键静默跳过）；仅内存覆盖，不落盘。
	/// </summary>
	public int? OverrideMaxParallelErbFiles { get; set; }

	/// <summary>
	/// 诊断旋钮（ADR-0026，06-K2）：privateDimList 生成期全局锁策略的内存覆盖——null/Auto=恒加锁
	/// （与 771708b 方案 B 止血逐位一致）；NoLock=纯取证档（关锁重开并发损坏竞态，可致状态损坏，
	/// issue 04-H1 判别用）。无对应 ConfigCode——游戏随包的 emuera.config 无法设置
	/// （loadConfig 对未知键静默跳过）；仅内存覆盖，不落盘。
	/// </summary>
	public DimLockPolicy? OverrideDimLockPolicy { get; set; }

	internal ConfigData()
	{
		// Issue 12：构造时绑定当前游戏目录的 emuera.config 路径。
		// /load-game 会先 GamePaths.Resolve(newDir) 再 new ConfigData()，
		// 因此此处读取的是新目录；启动期 GamePaths.Current 可能已指向 --ExeDir。
		// SAF：禁止 ExeDir + "emuera.config" 字符串拼接（documentId 会粘成错误段）。
		var exeDir = GamePaths.Current?.ExeDir ?? Program.ExeDir;
		configPath = CombineConfigPath(exeDir, "emuera.config");
		setDefault();
		BuildIndex();
	}
	#region EM_私家版_Emuera多言語化改造
	//適当に大き目の配列を作っておく。
	#region EE_configArrayの拡張
	// private AConfigItem[] configArray = new AConfigItem[80];
	private List<AConfigItem> configArray = [];
	#endregion
	//private AConfigItem[] replaceArray = new AConfigItem[50];
	//private AConfigItem[] debugArray = new AConfigItem[20];
	private List<AConfigItem> replaceArray = [];
	private List<AConfigItem> debugArray = [];
	#endregion

	#region EM_私家版_Emuera多言語化改造
	private void setDefault()
	{
		configArray.Add(new ConfigItem<bool>(ConfigCode.IgnoreCase, "大文字小文字の違いを無視する", "Ignore case", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseRenameFile, "_Rename.csvを利用する", "Use _Rename.csv file", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseReplaceFile, "_Replace.csvを利用する", "Use _Replace.csv file", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseMouse, "マウスを使用する", "Use mouse", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseMenu, "メニューを使用する", "Show menu", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseDebugCommand, "デバッグコマンドを使用する", "Allow debug commands", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.AllowMultipleInstances, "多重起動を許可する", "Allow multiple instances", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.AutoSave, "オートセーブを行なう", "Make autosaves", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseKeyMacro, "キーボードマクロを使用する", "Use keyboard macros", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SizableWindow, "ウィンドウの高さを可変にする", "Changeable window height", true));
		configArray.Add(new ConfigItem<TextDrawingMode>(ConfigCode.TextDrawingMode, "描画インターフェース", "Drawing interface", TextDrawingMode.TEXTRENDERER));

		configArray.Add(new ConfigItem<int>(ConfigCode.WindowX, "ウィンドウ幅", "Window width", 760));
		configArray.Add(new ConfigItem<int>(ConfigCode.WindowY, "ウィンドウ高さ", "Window height", 480));
		configArray.Add(new ConfigItem<int>(ConfigCode.WindowPosX, "ウィンドウ位置X", "Window X position", 0));
		configArray.Add(new ConfigItem<int>(ConfigCode.WindowPosY, "ウィンドウ位置Y", "Window Y position", 0));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SetWindowPos, "起動時のウィンドウ位置を指定する", "Fixed window starting position", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.WindowMaximixed, "起動時にウィンドウを最大化する", "Maximize window on startup", false));
		configArray.Add(new ConfigItem<int>(ConfigCode.MaxLog, "履歴ログの行数", "Max history log lines", 5000));
		configArray.Add(new ConfigItem<int>(ConfigCode.PrintCPerLine, "PRINTCを並べる数", "Items per line for PRINTC", 3));
		configArray.Add(new ConfigItem<int>(ConfigCode.PrintCLength, "PRINTCの文字数", "Number of Item characters for PRINTC", 25));
		configArray.Add(new ConfigItem<string>(ConfigCode.FontName, "フォント名", "Font name", "ＭＳ ゴシック"));
		configArray.Add(new ConfigItem<int>(ConfigCode.FontSize, "フォントサイズ", "Font size", 18));
		configArray.Add(new ConfigItem<int>(ConfigCode.LineHeight, "一行の高さ", "Line height", 19));
		configArray.Add(new ConfigItem<EmuColor>(ConfigCode.ForeColor, "文字色", "Text color", EmuColor.FromArgb(192, 192, 192)));//LIGHTGRAY
		configArray.Add(new ConfigItem<EmuColor>(ConfigCode.BackColor, "背景色", "Background color", EmuColor.FromArgb(0, 0, 0)));//BLACK
		configArray.Add(new ConfigItem<EmuColor>(ConfigCode.FocusColor, "選択中文字色", "Highlight color", EmuColor.FromArgb(255, 255, 0)));//YELLOW
		configArray.Add(new ConfigItem<EmuColor>(ConfigCode.LogColor, "履歴文字色", "History log color", EmuColor.FromArgb(192, 192, 192)));//LIGHTGRAY//EmuColor.FromArgb(128, 128, 128));//GRAY
		configArray.Add(new ConfigItem<int>(ConfigCode.FPS, "フレーム毎秒", "FPS", 5));
		configArray.Add(new ConfigItem<int>(ConfigCode.SkipFrame, "最大スキップフレーム数", "Skip frames", 3));
		configArray.Add(new ConfigItem<int>(ConfigCode.ScrollHeight, "スクロール行数", "Lines per scroll", 1));
		configArray.Add(new ConfigItem<int>(ConfigCode.DisplayWarningLevel, "表示する最低警告レベル", "Minimum warning level", 1));
		configArray.Add(new ConfigItem<bool>(ConfigCode.DisplayReport, "ロード時にレポートを表示する", "Display loading report", false));
		// erb-loader-parallel 03：ERB 並列読み込み（false = 直列フォールバック、AnalysisMode は常に直列）
		configArray.Add(new ConfigItem<bool>(ConfigCode.ParallelErbLoad, "ERBファイルを並列して読み込む", "Load ERB files in parallel", true));
		// erb-loader-parallel 08：ParseScript 未呼び出し関数の解析並列化（false = 直列フォールバック、AnalysisMode は常に直列）
		configArray.Add(new ConfigItem<bool>(ConfigCode.ParallelParseScript, "未呼び出し関数の解析を並列して行う", "Parse uncalled functions in parallel", true));
		configArray.Add(new ConfigItem<ReduceArgumentOnLoadFlag>(ConfigCode.ReduceArgumentOnLoad, "ロード時に引数を解析する", "Reduce argument on load", ReduceArgumentOnLoadFlag.NO));

		configArray.Add(new ConfigItem<bool>(ConfigCode.IgnoreUncalledFunction, "呼び出されなかった関数を無視する", "Ignore uncalled functions", true));
		configArray.Add(new ConfigItem<DisplayWarningFlag>(ConfigCode.FunctionNotFoundWarning, "関数が見つからない警告の扱い", "Function is not found warning", DisplayWarningFlag.IGNORE));
		configArray.Add(new ConfigItem<DisplayWarningFlag>(ConfigCode.FunctionNotCalledWarning, "関数が呼び出されなかった警告の扱い", "Function not called warning", DisplayWarningFlag.IGNORE));
		// lazy-function-load（B 线 B1）：默认 true（Issue 04 门禁全绿 + 复测远超预注册线 → 默认开，沿 ParallelErbLoad 先例不做观望期）。
		configArray.Add(new ConfigItem<bool>(ConfigCode.LazyFunctionLoad, "未呼び出し関数の本体解析を遅延する", "Defer uncalled function body parsing", true));
		// b2-irdiskcache（B 线 B2）：默认 true（spec 已决）；FALSE = 完全回到 B1 现状（回退硬门）。
		configArray.Add(new ConfigItem<bool>(ConfigCode.IrDiskCacheLoad, "関数の内容をIRディスクキャッシュする", "Cache function bodies to IR disk cache", true));
		// issues/12 M4：命中轮 IR 头存 firstDir 清单开关（默认 true）；FALSE = 命中轮恒走 SAF 扫描（回退硬门），写侧/指纹不受影响。
		configArray.Add(new ConfigItem<bool>(ConfigCode.IrFirstDirList, "命中ラウンドでIR保存のfirstDirリストを使う", "Use IR-stored firstDir list on hit rounds", true));
		// issues/13 M5：命中轮清单 sidecar 回放开关（默认 true，仅 content-URI 游戏生效）；FALSE = 恒走真实枚举，写侧不受影响。
		configArray.Add(new ConfigItem<bool>(ConfigCode.IrFileListCache, "命中ラウンドでIR保存のファイルリストを使う", "Use IR-stored file list on hit rounds", true));
		// issues/10 10-B：CSV/ERH 源内容缓存开关（默认 true，仅 content-URI 游戏生效）；FALSE = 命中轮回到懒读现状，写侧不受影响。
		configArray.Add(new ConfigItem<bool>(ConfigCode.SrcContentCache, "命中ラウンドでCSV/ERH内容キャッシュを使う", "Use CSV/ERH content cache on hit rounds", true));


		configArray.Add(new ConfigItem<bool>(ConfigCode.ChangeMasterNameIfDebug, "デバッグコマンドを使用した時にMASTERの名前を変更する", "Change MASTER mame in debug", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.ButtonWrap, "ボタンの途中で行を折りかえさない", "Button wrapping", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SearchSubdirectory, "サブディレクトリを検索する", "Search subfolders", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SortWithFilename, "読み込み順をファイル名順にソートする", "Sort filenames", false));
		configArray.Add(new ConfigItem<long>(ConfigCode.LastKey, "最終更新コード", "Latest identify code", 0));
		configArray.Add(new ConfigItem<int>(ConfigCode.SaveDataNos, "表示するセーブデータ数", "Save data count per page", 20));
		configArray.Add(new ConfigItem<bool>(ConfigCode.WarnBackCompatibility, "eramaker互換性に関する警告を表示する", "Eramaker compatibility warning", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.AllowFunctionOverloading, "システム関数の上書きを許可する", "Allow overriding system functions", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.WarnFunctionOverloading, "システム関数が上書きされたとき警告を表示する", "System function override warning", true));
		configArray.Add(new ConfigItem<string>(ConfigCode.TextEditor, "関連づけるテキストエディタ", "Text editor", "notepad"));
		configArray.Add(new ConfigItem<TextEditorType>(ConfigCode.EditorType, "テキストエディタコマンドライン指定", "Text editor command line setting", TextEditorType.USER_SETTING));
		configArray.Add(new ConfigItem<string>(ConfigCode.EditorArgument, "エディタに渡す行指定引数", "Text editor command line arguments", ""));
		configArray.Add(new ConfigItem<bool>(ConfigCode.WarnNormalFunctionOverloading, "同名の非イベント関数が複数定義されたとき警告する", "Duplicated functions warning", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiErrorLine, "解釈不可能な行があっても実行する", "Execute error lines", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiCALLNAME, "CALLNAMEが空文字列の時にNAMEを代入する", "Use NAME if CALLNAME is empty", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseSaveFolder, "セーブデータをsavフォルダ内に作成する", "Use sav folder", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiRAND, "擬似変数RANDの仕様をeramakerに合わせる", "Imitate behavior for RAND", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiDRAWLINE, "DRAWLINEを常に新しい行で行う", "Always start DRAWLINE in a new line", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiFunctionNoignoreCase, "関数・属性については大文字小文字を無視しない", "Do not ignore case for functions and attributes", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SystemAllowFullSpace, "全角スペースをホワイトスペースに含める", "Whitespace includes full-width space", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiLinefeedAs1739, "ver1739以前の非ボタン折り返しを再現する", "Reproduce wrapping behavior like in pre ver1739", false));
		configArray.Add(new ConfigItem<UseLanguage>(ConfigCode.useLanguage, "内部で使用する東アジア言語", "Default ANSI encoding", UseLanguage.JAPANESE));
		configArray.Add(new ConfigItem<bool>(ConfigCode.AllowLongInputByMouse, "ONEINPUT系命令でマウスによる2文字以上の入力を許可する", "Allow long input by mouse for ONEINPUT", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiCallEvent, "イベント関数のCALLを許可する", "Allow CALL on event functions", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiSPChara, "SPキャラを使用する", "Allow SP characters", false));

		configArray.Add(new ConfigItem<bool>(ConfigCode.SystemSaveInBinary, "セーブデータをバイナリ形式で保存する", "Use the binary format for saving data", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiFuncArgOptional, "ユーザー関数の全ての引数の省略を許可する", "Allow arguments omission for user functions", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CompatiFuncArgAutoConvert, "ユーザー関数の引数に自動的にTOSTRを補完する", "Auto TOSTR conversion for user function arguments", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SystemIgnoreTripleSymbol, "FORM中の三連記号を展開しない", "Do not process triple symbols inside FORM", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.TimesNotRigorousCalculation, "TIMESの計算をeramakerにあわせる", "Imitate behavior for TIMES", false));

		configArray.Add(new ConfigItem<bool>(ConfigCode.SystemNoTarget, "キャラクタ変数の引数を補完しない", "Do not auto-complete arguments for character variables", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.SystemIgnoreStringSet, "文字列変数の代入に文字列式を強制する", "String variable assignment on valid with string expression", false));

		#region EE_UPDATECHECK
		configArray.Add(new ConfigItem<bool>(ConfigCode.ForbidUpdateCheck, "UPDATECHECKを許可しない", "Disallow UPDATECHECK", false));
		#endregion
		#region EE_ERDConfig
		configArray.Add(new ConfigItem<bool>(ConfigCode.UseERD, "ERD機能を利用する", "Use ERD", true));
		#endregion
		#region EE_ERDNAME
		configArray.Add(new ConfigItem<bool>(ConfigCode.VarsizeDimConfig, "VARSIZEの次元指定をERD機能に合わせる", "Imitate ERD to VARSIZE dimension specification", false));
		#endregion
		#region EE_重複定義の確認
		configArray.Add(new ConfigItem<bool>(ConfigCode.CheckDuplicateIdentifier, "ERDで定義した識別子とローカル変数の重複を確認する", "Check duplicate ERD identifier and private variablea", false));
		#endregion
		#region EE_行連結の改行コード置換
		configArray.Add(new ConfigItem<string>(ConfigCode.ReplaceContinuationBR, "行連結の改行コードの置換文字列", "String of replacing new line code inside continuation", "\" \""));
		#endregion
		#region EM_私家版_LoadText＆SaveText機能拡張
		configArray.Add(new ConfigItem<List<string>>(ConfigCode.ValidExtension, "LOADTEXTとSAVETEXTで使える拡張子", "Valid extensions for LOADTEXT and SAVETEXT", ["txt"]));
		#endregion
		#region EM_私家版_セーブ圧縮
		configArray.Add(new ConfigItem<bool>(ConfigCode.ZipSaveData, "セーブデータを圧縮して保存する", "Compress save data", false));
		#endregion
		#region EM_私家版_Emuera多言語化改造
		configArray.Add(new ConfigItem<bool>(ConfigCode.EnglishConfigOutput, "CONFIGファイルの内容を英語で保存する", "Output English items in the config file", false));
		configArray.Add(new ConfigItem<string>(ConfigCode.EmueraLang, "Emueraの表示言語", "Emuera interface language", string.Empty));
		#endregion
		#region EM_私家版_Icon指定機能
		configArray.Add(new ConfigItem<string>(ConfigCode.EmueraIcon, "Emueraのアイコンのパス", "Path to a custom window icon", string.Empty));
		#endregion
		#region EE_AnchorのCB機能移植
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBUseClipboard, "表示したテキストをクリップボードにコピーする", "Clipboard- Copy text to Clipboard during Game", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBIgnoreTags, "テキスト中の<>タグを無視する", "Clipboard- ignore <> tags in text", false));
		configArray.Add(new ConfigItem<string>(ConfigCode.CBReplaceTags, "<>を次の文で置き換える", "Clipboard- Replace <> with this", "."));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBNewLinesOnly, "新しい行のみコピーする", "Clipboard- Show new lines only", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBClearBuffer, "画面のリフレッシュ時にクリップボードとバッファを消去する", "Clipboard- Clear Buffer when game clears screen", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBTriggerLeftClick, "左クリックをトリガーにする", "Clipboard- LeftClick Trigger", true));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBTriggerMiddleClick, "ホイールクリックをトリガーにする", "Clipboard- MiddleClick Trigger", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBTriggerDoubleLeftClick, "ダブルクリックをトリガーにする", "Clipboard- Double Left Click Trigger", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBTriggerAnyKeyWait, "WAITをトリガーにする", "Clipboard- AnyKey Wait Trigger ", false));
		configArray.Add(new ConfigItem<bool>(ConfigCode.CBTriggerInputWait, "INPUTをトリガーにする", "Clipboard- Wait for Input Trigger", true));
		configArray.Add(new ConfigItem<int>(ConfigCode.CBMaxCB, "クリップボードに貼り付ける行数", "Clipboard- Length of Clipboard", 25));
		configArray.Add(new ConfigItem<int>(ConfigCode.CBBufferSize, "総バッファサイズ", "Clipboard- Buffer Size", 300));
		configArray.Add(new ConfigItem<int>(ConfigCode.CBScrollCount, "スクロールの行数", "Clipboard- Scrolled Lines per Key", 5));
		configArray.Add(new ConfigItem<int>(ConfigCode.CBMinTimer, "クリップボードの更新間隔(ミリ秒)", "Clipboard- min time between pastes", 800));
		#endregion
		#region EmuEra-Rikaichan related settings
		configArray.Add(new ConfigItem<bool>(ConfigCode.RikaiEnabled, "Rikaichanを使用する", "Rikai- Enabled", false));
		configArray.Add(new ConfigItem<string>(ConfigCode.RikaiFilename, "Rikaichanのファイルパス", "Rikai- Dictionary Filename", "Emuera-Rikai-edict.txt-eucjp"));
		configArray.Add(new ConfigItem<EmuColor>(ConfigCode.RikaiColorBack, "ポップアップの背景色", "Rikai- Back Color", EmuColor.FromArgb(0, 0, 0x8B))); ////Color.DarkBlue
		configArray.Add(new ConfigItem<EmuColor>(ConfigCode.RikaiColorText, "ポップアップの文字色", "Rikai- Text Color", EmuColor.FromArgb(0xFF, 0xFF, 0xFF))); ////Color.White
		configArray.Add(new ConfigItem<bool>(ConfigCode.RikaiUseSeparateBoxes, "翻訳中の語句を強調表示する", "Rikai- Use Separate Boxes", true));
		#endregion

		configArray.Add(new ConfigItem<bool>(ConfigCode.Ctrl_Z_Enabled, "Ctrl-Zで元に戻す機能を有効にする", "Enable undo with ctrl-z", false));

		debugArray.Add(new ConfigItem<bool>(ConfigCode.DebugShowWindow, "起動時にデバッグウインドウを表示する", "Show debug window on startup", true));
		debugArray.Add(new ConfigItem<bool>(ConfigCode.DebugWindowTopMost, "デバッグウインドウを最前面に表示する", "Debug window always on top", true));
		debugArray.Add(new ConfigItem<int>(ConfigCode.DebugWindowWidth, "デバッグウィンドウ幅", "Debug window width", 400));
		debugArray.Add(new ConfigItem<int>(ConfigCode.DebugWindowHeight, "デバッグウィンドウ高さ", "Debug window height", 300));
		debugArray.Add(new ConfigItem<bool>(ConfigCode.DebugSetWindowPos, "デバッグウィンドウ位置を指定する", "Fixed debug window starting position", false));
		debugArray.Add(new ConfigItem<int>(ConfigCode.DebugWindowPosX, "デバッグウィンドウ位置X", "Debug window X position", 0));
		debugArray.Add(new ConfigItem<int>(ConfigCode.DebugWindowPosY, "デバッグウィンドウ位置Y", "Debug window Y position", 0));

		replaceArray.Add(new ConfigItem<string>(ConfigCode.MoneyLabel, "お金の単位", "Currency symbol", "$"));
		replaceArray.Add(new ConfigItem<bool>(ConfigCode.MoneyFirst, "単位の位置", "Currency symbol position", true));
		replaceArray.Add(new ConfigItem<string>(ConfigCode.LoadLabel, "起動時簡略表示", "Loading message", "Now Loading..."));
		replaceArray.Add(new ConfigItem<int>(ConfigCode.MaxShopItem, "販売アイテム数", "Max shop item storage", 100));
		replaceArray.Add(new ConfigItem<string>(ConfigCode.DrawLineString, "DRAWLINE文字", "DRAWLINE character", "-"));
		replaceArray.Add(new ConfigItem<char>(ConfigCode.BarChar1, "BAR文字1", "BAR character 1", '*'));
		replaceArray.Add(new ConfigItem<char>(ConfigCode.BarChar2, "BAR文字2", "BAR character 2", '.'));
		replaceArray.Add(new ConfigItem<string>(ConfigCode.TitleMenuString0, "システムメニュー0", "System menu 0", "最初からはじめる"));
		replaceArray.Add(new ConfigItem<string>(ConfigCode.TitleMenuString1, "システムメニュー1", "System menu 1", "ロードしてはじめる"));
		replaceArray.Add(new ConfigItem<int>(ConfigCode.ComAbleDefault, "COM_ABLE初期値", "Default COM_ABLE", 1));
		replaceArray.Add(new ConfigItem<List<long>>(ConfigCode.StainDefault, "汚れの初期値", "Default Stain", new List<long>(new long[] { 0, 0, 2, 1, 8 })));
		replaceArray.Add(new ConfigItem<string>(ConfigCode.TimeupLabel, "時間切れ表示", "Time up message", "時間切れ"));
		replaceArray.Add(new ConfigItem<List<long>>(ConfigCode.ExpLvDef, "EXPLVの初期値", "Default EXPLV", new List<long>(new long[] { 0, 1, 4, 20, 50, 200 })));
		replaceArray.Add(new ConfigItem<List<long>>(ConfigCode.PalamLvDef, "PALAMLVの初期値", "Default PALAMLV", new List<long>(new long[] { 0, 100, 500, 3000, 10000, 30000, 60000, 100000, 150000, 250000 })));
		replaceArray.Add(new ConfigItem<long>(ConfigCode.pbandDef, "PBANDの初期値", "Default PBAND", 4));
		replaceArray.Add(new ConfigItem<long>(ConfigCode.RelationDef, "RELATIONの初期値", "Default RELATION", 0));
	}
	#endregion

	/*
	private void setDefault()
	{
		int i = 0;
		configArray[i++] = new ConfigItem<bool>(ConfigCode.IgnoreCase, "大文字小文字の違いを無視する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseRenameFile, "_Rename.csvを利用する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseReplaceFile, "_Replace.csvを利用する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseMouse, "マウスを使用する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseMenu, "メニューを使用する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseDebugCommand, "デバッグコマンドを使用する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.AllowMultipleInstances, "多重起動を許可する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.AutoSave, "オートセーブを行なう", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseKeyMacro, "キーボードマクロを使用する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SizableWindow, "ウィンドウの高さを可変にする", true);
		configArray[i++] = new ConfigItem<TextDrawingMode>(ConfigCode.TextDrawingMode, "描画インターフェース", TextDrawingMode.TEXTRENDERER);
		//configArray[i++] = new ConfigItem<bool>(ConfigCode.UseImageBuffer, "イメージバッファを使用する", true);
		configArray[i++] = new ConfigItem<int>(ConfigCode.WindowX, "ウィンドウ幅", 760);
		configArray[i++] = new ConfigItem<int>(ConfigCode.WindowY, "ウィンドウ高さ", 480);
		configArray[i++] = new ConfigItem<int>(ConfigCode.WindowPosX, "ウィンドウ位置X", 0);
		configArray[i++] = new ConfigItem<int>(ConfigCode.WindowPosY, "ウィンドウ位置Y", 0);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SetWindowPos, "起動時のウィンドウ位置を指定する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.WindowMaximixed, "起動時にウィンドウを最大化する", false);
		configArray[i++] = new ConfigItem<int>(ConfigCode.MaxLog, "履歴ログの行数", 5000);
		configArray[i++] = new ConfigItem<int>(ConfigCode.PrintCPerLine, "PRINTCを並べる数", 3);
		configArray[i++] = new ConfigItem<int>(ConfigCode.PrintCLength, "PRINTCの文字数", 25);
		configArray[i++] = new ConfigItem<string>(ConfigCode.FontName, "フォント名", "ＭＳ ゴシック");
		configArray[i++] = new ConfigItem<int>(ConfigCode.FontSize, "フォントサイズ", 18);
		configArray[i++] = new ConfigItem<int>(ConfigCode.LineHeight, "一行の高さ", 19);
		configArray[i++] = new ConfigItem<EmuColor>(ConfigCode.ForeColor, "文字色", EmuColor.FromArgb(192, 192, 192));//LIGHTGRAY
		configArray[i++] = new ConfigItem<EmuColor>(ConfigCode.BackColor, "背景色", EmuColor.FromArgb(0, 0, 0));//BLACK
		configArray[i++] = new ConfigItem<EmuColor>(ConfigCode.FocusColor, "選択中文字色", EmuColor.FromArgb(255, 255, 0));//YELLOW
		configArray[i++] = new ConfigItem<EmuColor>(ConfigCode.LogColor, "履歴文字色", EmuColor.FromArgb(192, 192, 192));//LIGHTGRAY//EmuColor.FromArgb(128, 128, 128);//GRAY
		configArray[i++] = new ConfigItem<int>(ConfigCode.FPS, "フレーム毎秒", 5);
		configArray[i++] = new ConfigItem<int>(ConfigCode.SkipFrame, "最大スキップフレーム数", 3);
		configArray[i++] = new ConfigItem<int>(ConfigCode.ScrollHeight, "スクロール行数", 1);
		configArray[i++] = new ConfigItem<int>(ConfigCode.DisplayWarningLevel, "表示する最低警告レベル", 1);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.DisplayReport, "ロード時にレポートを表示する", false);
		configArray[i++] = new ConfigItem<ReduceArgumentOnLoadFlag>(ConfigCode.ReduceArgumentOnLoad, "ロード時に引数を解析する", ReduceArgumentOnLoadFlag.NO);
		//configArray[i++] = new ConfigItem<bool>(ConfigCode.ReduceFormattedStringOnLoad, "ロード時にFORM文字列を解析する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.IgnoreUncalledFunction, "呼び出されなかった関数を無視する", true);
		configArray[i++] = new ConfigItem<DisplayWarningFlag>(ConfigCode.FunctionNotFoundWarning, "関数が見つからない警告の扱い", DisplayWarningFlag.IGNORE);
		configArray[i++] = new ConfigItem<DisplayWarningFlag>(ConfigCode.FunctionNotCalledWarning, "関数が呼び出されなかった警告の扱い", DisplayWarningFlag.IGNORE);
		// lazy-function-load（B 线 B1）：默认 true（Issue 04 门禁全绿 + 复测远超预注册线 → 默认开，沿 ParallelErbLoad 先例不做观望期）。
		configArray[i++] = new ConfigItem<bool>(ConfigCode.LazyFunctionLoad, "未呼び出し関数の本体解析を遅延する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.IrDiskCacheLoad, "関数の内容をIRディスクキャッシュする", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.IrFirstDirList, "命中ラウンドでIR保存のfirstDirリストを使う", true);
		//configArray[i++] = new ConfigItem<List<string>>(ConfigCode.IgnoreWarningFiles, "指定したファイル中の警告を無視する", new List<string>());
		configArray[i++] = new ConfigItem<bool>(ConfigCode.ChangeMasterNameIfDebug, "デバッグコマンドを使用した時にMASTERの名前を変更する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.ButtonWrap, "ボタンの途中で行を折りかえさない", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SearchSubdirectory, "サブディレクトリを検索する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SortWithFilename, "読み込み順をファイル名順にソートする", false);
		configArray[i++] = new ConfigItem<long>(ConfigCode.LastKey, "最終更新コード", 0);
		configArray[i++] = new ConfigItem<int>(ConfigCode.SaveDataNos, "表示するセーブデータ数", 20);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.WarnBackCompatibility, "eramaker互換性に関する警告を表示する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.AllowFunctionOverloading, "システム関数の上書きを許可する", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.WarnFunctionOverloading, "システム関数が上書きされたとき警告を表示する", true);
		configArray[i++] = new ConfigItem<string>(ConfigCode.TextEditor, "関連づけるテキストエディタ", "notepad");
		configArray[i++] = new ConfigItem<TextEditorType>(ConfigCode.EditorType, "テキストエディタコマンドライン指定", TextEditorType.USER_SETTING);
		configArray[i++] = new ConfigItem<string>(ConfigCode.EditorArgument, "エディタに渡す行指定引数", "");
		configArray[i++] = new ConfigItem<bool>(ConfigCode.WarnNormalFunctionOverloading, "同名の非イベント関数が複数定義されたとき警告する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiErrorLine, "解釈不可能な行があっても実行する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiCALLNAME, "CALLNAMEが空文字列の時にNAMEを代入する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseSaveFolder, "セーブデータをsavフォルダ内に作成する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiRAND, "擬似変数RANDの仕様をeramakerに合わせる", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiDRAWLINE, "DRAWLINEを常に新しい行で行う", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiFunctionNoignoreCase, "関数・属性については大文字小文字を無視しない", false); ;
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SystemAllowFullSpace, "全角スペースをホワイトスペースに含める", true);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SystemSaveInUTF8, "セーブデータをUTF-8で保存する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiLinefeedAs1739, "ver1739以前の非ボタン折り返しを再現する", false);
		configArray[i++] = new ConfigItem<UseLanguage>(ConfigCode.useLanguage, "内部で使用する東アジア言語", UseLanguage.JAPANESE);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.AllowLongInputByMouse, "ONEINPUT系命令でマウスによる2文字以上の入力を許可する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiCallEvent, "イベント関数のCALLを許可する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiSPChara, "SPキャラを使用する", false);

		configArray[i++] = new ConfigItem<bool>(ConfigCode.SystemSaveInBinary, "セーブデータをバイナリ形式で保存する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiFuncArgOptional, "ユーザー関数の全ての引数の省略を許可する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.CompatiFuncArgAutoConvert, "ユーザー関数の引数に自動的にTOSTRを補完する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SystemIgnoreTripleSymbol, "FORM中の三連記号を展開しない", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.TimesNotRigorousCalculation, "TIMESの計算をeramakerにあわせる", false);
		//一文字変数の禁止オプションを考えた名残
		//configArray[i++] = new ConfigItem<bool>(ConfigCode.ForbidOneCodeVariable, "一文字変数の使用を禁止する", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SystemNoTarget, "キャラクタ変数の引数を補完しない", false);
		configArray[i++] = new ConfigItem<bool>(ConfigCode.SystemIgnoreStringSet, "文字列変数の代入に文字列式を強制する", false);

		#region EE_UPDATECHECK
		configArray[i++] = new ConfigItem<bool>(ConfigCode.ForbidUpdateCheck, "UPDATECHECKを許可しない", false);
		#endregion
		#region EE_ERDConfig
		configArray[i++] = new ConfigItem<bool>(ConfigCode.UseERD, "ERD機能を利用する", true);
		#endregion

		#region EM_私家版_LoadText＆SaveText機能拡張
		configArray[i++] = new ConfigItem<List<string>>(ConfigCode.ValidExtension, "LOADTEXTとSAVETEXTで使える拡張子", new List<string> { "txt" });
		#endregion
		#region EM_私家版_セーブ圧縮
		configArray[i++] = new ConfigItem<bool>(ConfigCode.ZipSaveData, "セーブデータを圧縮して保存する", false);
		#endregion

		i = 0;
		debugArray[i++] = new ConfigItem<bool>(ConfigCode.DebugShowWindow, "起動時にデバッグウインドウを表示する", true);
		debugArray[i++] = new ConfigItem<bool>(ConfigCode.DebugWindowTopMost, "デバッグウインドウを最前面に表示する", true);
		debugArray[i++] = new ConfigItem<int>(ConfigCode.DebugWindowWidth, "デバッグウィンドウ幅", 400);
		debugArray[i++] = new ConfigItem<int>(ConfigCode.DebugWindowHeight, "デバッグウィンドウ高さ", 300);
		debugArray[i++] = new ConfigItem<bool>(ConfigCode.DebugSetWindowPos, "デバッグウィンドウ位置を指定する", false);
		debugArray[i++] = new ConfigItem<int>(ConfigCode.DebugWindowPosX, "デバッグウィンドウ位置X", 0);
		debugArray[i++] = new ConfigItem<int>(ConfigCode.DebugWindowPosY, "デバッグウィンドウ位置Y", 0);

		i = 0;
		replaceArray[i++] = new ConfigItem<string>(ConfigCode.MoneyLabel, "お金の単位", "$");
		replaceArray[i++] = new ConfigItem<bool>(ConfigCode.MoneyFirst, "単位の位置", true);
		replaceArray[i++] = new ConfigItem<string>(ConfigCode.LoadLabel, "起動時簡略表示", "Now Loading...");
		replaceArray[i++] = new ConfigItem<int>(ConfigCode.MaxShopItem, "販売アイテム数", 100);
		replaceArray[i++] = new ConfigItem<string>(ConfigCode.DrawLineString, "DRAWLINE文字", "-");
		replaceArray[i++] = new ConfigItem<char>(ConfigCode.BarChar1, "BAR文字1", '*');
		replaceArray[i++] = new ConfigItem<char>(ConfigCode.BarChar2, "BAR文字2", '.');
		replaceArray[i++] = new ConfigItem<string>(ConfigCode.TitleMenuString0, "システムメニュー0", "最初からはじめる");
		replaceArray[i++] = new ConfigItem<string>(ConfigCode.TitleMenuString1, "システムメニュー1", "ロードしてはじめる");
		replaceArray[i++] = new ConfigItem<int>(ConfigCode.ComAbleDefault, "COM_ABLE初期値", 1);
		replaceArray[i++] = new ConfigItem<List<Int64>>(ConfigCode.StainDefault, "汚れの初期値", new List<Int64>(new Int64[] { 0, 0, 2, 1, 8 }));
		replaceArray[i++] = new ConfigItem<string>(ConfigCode.TimeupLabel, "時間切れ表示", "時間切れ");
		replaceArray[i++] = new ConfigItem<List<Int64>>(ConfigCode.ExpLvDef, "EXPLVの初期値", new List<long>(new Int64[] { 0, 1, 4, 20, 50, 200 }));
		replaceArray[i++] = new ConfigItem<List<Int64>>(ConfigCode.PalamLvDef, "PALAMLVの初期値", new List<long>(new Int64[] { 0, 100, 500, 3000, 10000, 30000, 60000, 100000, 150000, 250000 }));
		replaceArray[i++] = new ConfigItem<Int64>(ConfigCode.pbandDef, "PBANDの初期値", 4);
		replaceArray[i++] = new ConfigItem<Int64>(ConfigCode.RelationDef, "RELATIONの初期値", 0);
	}
	*/

	public ConfigData Copy()
	{
		#region EM_私家版_Emuera多言語化改造
		ConfigData config = new();
		// for (int i = 0; i < configArray.Length; i++)
		for (int i = 0; i < configArray.Count; i++)
			if ((configArray[i] != null) && (config.configArray[i] != null))
				configArray[i].CopyTo(config.configArray[i]);
		//for (int i = 0; i < configArray.Length; i++)
		for (int i = 0; i < debugArray.Count; i++)
			if ((debugArray[i] != null) && (config.debugArray[i] != null))
				debugArray[i].CopyTo(config.debugArray[i]);
		//for (int i = 0; i < replaceArray.Length; i++)
		for (int i = 0; i < replaceArray.Count; i++)
			if ((replaceArray[i] != null) && (config.replaceArray[i] != null))
				replaceArray[i].CopyTo(config.replaceArray[i]);
		return config;
		#endregion
	}

	public Dictionary<ConfigCode, string> GetConfigNameDic()
	{
		Dictionary<ConfigCode, string> ret = [];
		#region EM_私家版_Emuera多言語化改造
		foreach (AConfigItem item in configArray)
		{
			if (item != null)
				// ret.Add(item.Code, item.Text);
				ret.Add(item.Code, $"{item.Text}/{item.EngText}");
		}
		#endregion
		return ret;
	}

	public T GetConfigValue<T>(ConfigCode code)
	{
		AConfigItem item = GetItem(code);
		//if ((item != null) && (item is ConfigItem<T>))
		return ((ConfigItem<T>)item).Value;
		//throw new ExeEE("GetConfigValueのCodeまたは型が不適切");
	}

	#region getitem
	public AConfigItem GetItem(ConfigCode code)
	{
		AConfigItem item = GetConfigItem(code);
		if (item == null)
		{
			item = GetReplaceItem(code);
			if (item == null)
			{
				item = GetDebugItem(code);
			}
		}
		return item;
	}
	public AConfigItem GetItem(string key)
	{
		AConfigItem item = GetConfigItem(key);
		if (item == null)
		{
			item = GetReplaceItem(key);
			if (item == null)
			{
				item = GetDebugItem(key);
			}
		}
		return item;
	}

	private void BuildIndex()
	{
		foreach (AConfigItem item in configArray)
			if (item != null && !_index.ContainsKey(item.Code)) _index[item.Code] = item;
		foreach (AConfigItem item in replaceArray)
			if (item != null && !_index.ContainsKey(item.Code)) _index[item.Code] = item;
		foreach (AConfigItem item in debugArray)
			if (item != null && !_index.ContainsKey(item.Code)) _index[item.Code] = item;
	}

	public AConfigItem GetConfigItem(ConfigCode code)
	{
		return _index.TryGetValue(code, out var item) ? item : null!;
	}
	public AConfigItem GetConfigItem(string key)
	{
		#region EM_私家版_Emuera多言語化改造
		key = key.ToUpper();
		foreach (AConfigItem item in configArray)
		{
			if (item == null)
				continue;
			if (item.Name == key)
				return item;
			if (item.Text == key)
				return item;
			if (item.EngText == key)
				return item;
		}
		return null!;
		#endregion
	}

	public AConfigItem GetReplaceItem(ConfigCode code)
	{
		foreach (AConfigItem item in replaceArray)
		{
			if (item == null)
				continue;
			if (item.Code == code)
				return item;
		}
		return null!;
	}
	public AConfigItem GetReplaceItem(string key)
	{
		#region EM_私家版_Emuera多言語化改造
		key = key.ToUpper();
		foreach (AConfigItem item in replaceArray)
		{
			if (item == null)
				continue;
			if (item.Name == key)
				return item;
			if (item.Text == key)
				return item;
		}
		return null!;
		#endregion
	}

	public AConfigItem GetDebugItem(ConfigCode code)
	{
		foreach (AConfigItem item in debugArray)
		{
			if (item == null)
				continue;
			if (item.Code == code)
				return item;
		}
		return null!;
	}
	public AConfigItem GetDebugItem(string key)
	{
		#region EM_私家版_Emuera多言語化改造
		key = key.ToUpper();
		foreach (AConfigItem item in debugArray)
		{
			if (item == null)
				continue;
			if (item.Name == key)
				return item;
			if (item.Text == key)
				return item;
		}
		return null!;
		#endregion
	}

	public static SingleTerm GetConfigValueInERB(string text, ref string errMes)
	{
		AConfigItem item = Current!.GetItem(text);
		if (item == null)
		{
			errMes = string.Format(trerror.InvalidConfigName.Text, text);
			return null!;
		}
		SingleTerm term;
		switch (item.Code)
		{
			//<bool>
			case ConfigCode.AutoSave://"オートセーブを行なう"
			case ConfigCode.MoneyFirst://"単位の位置"
				if (item.GetValue<bool>())
					term = new SingleLongTerm(1);
				else
					term = new SingleLongTerm(0);
				break;
			//<int>
			case ConfigCode.WindowX:// "ウィンドウ幅"
			case ConfigCode.PrintCPerLine:// "PRINTCを並べる数"
			case ConfigCode.PrintCLength:// "PRINTCの文字数"
			case ConfigCode.FontSize:// "フォントサイズ"
			case ConfigCode.LineHeight:// "一行の高さ"
			case ConfigCode.SaveDataNos:// "表示するセーブデータ数"
			case ConfigCode.MaxShopItem:// "販売アイテム数"
			case ConfigCode.ComAbleDefault:// "COM_ABLE初期値"
				term = new SingleLongTerm(item.GetValue<int>());
				break;
			//<Color>
			case ConfigCode.ForeColor://"文字色"
			case ConfigCode.BackColor://"背景色"
			case ConfigCode.FocusColor://"選択中文字色"
			case ConfigCode.LogColor://"履歴文字色"
				{
					EmuColor color = item.GetValue<EmuColor>();
				term = new SingleLongTerm(((color.R * 256) + color.G) * 256 + color.B);
				}
				break;

			//<Int64>
			case ConfigCode.pbandDef:// "PBANDの初期値"
			case ConfigCode.RelationDef:// "RELATIONの初期値"
				term = new SingleLongTerm(item.GetValue<long>());
				break;

			//<string>
			case ConfigCode.FontName:// "フォント名"
			case ConfigCode.MoneyLabel:// "お金の単位"
			case ConfigCode.LoadLabel:// "起動時簡略表示"
			case ConfigCode.DrawLineString:// "DRAWLINE文字"
			case ConfigCode.TitleMenuString0:// "システムメニュー0"
			case ConfigCode.TitleMenuString1:// "システムメニュー1"
			case ConfigCode.TimeupLabel:// "時間切れ表示"
				term = new SingleStrTerm(item.GetValue<string>());
				break;

			//<char>
			case ConfigCode.BarChar1:// "BAR文字1"
			case ConfigCode.BarChar2:// "BAR文字2"
				term = new SingleStrTerm(item.GetValue<char>().ToString());
				break;
			//<TextDrawingMode>
			case ConfigCode.TextDrawingMode:// "描画インターフェース"
				term = new SingleStrTerm(item.GetValue<TextDrawingMode>().ToString());
				break;
			default:
				{
					if (Enum.IsDefined<ConfigCode>(item.Code))
					{
						switch (item.ValueToString())
						{
							case "YES":
								term = new SingleLongTerm(1);
								break;
							case "NO":
								term = new SingleLongTerm(0);
								break;
							default:
								string val = item.ValueToString();
								if (long.TryParse(val, out long i))
									term = new SingleLongTerm(i);
								else
									term = new SingleStrTerm(val);
								break;
						}
					}
					else
					{
						errMes = string.Format(trerror.NotAllowGetConfigValue.Text, text);
						return null!;
					}
					break;
					/** try-catchで解決しようとした名残
					try
					{
						term = item.GetValue<bool>() == true ? new SingleTerm(1) : new SingleTerm(0) ;
					}
					catch
					{
						try
						{
							term = new SingleTerm(item.GetValue<int>());
						}
						catch
						{
							try
							{
								term = new SingleTerm(item.GetValue<string>());
							}
							catch
							{
								errMes = string.Format(trerror.NotAllowGetConfigValue.Text, text);
								return null!;
							}
						}
					}
					break;
					**/
				}
		}
		return term;
	}
	#endregion


	public bool SaveConfig()
	{
		return SaveConfig(configPath);
	}

	private bool SaveConfig(string targetPath)
	{
		try
		{
			#region EM_私家版_Emuera多言語化改造
			using var writer = new StreamWriter(SafCompat.OpenWrite(targetPath), Config.Encode);

			// for (int i = 0; i < configArray.Length; i++)
			for (int i = 0; i < configArray.Count; i++)
			#endregion
			{
				AConfigItem item = configArray[i];
				if (item == null)
					continue;

				//1806beta001 CompatiDRAWLINEの廃止、CompatiLinefeedAs1739へ移行
				if (item.Code == ConfigCode.CompatiDRAWLINE)
					continue;
				if ((item.Code == ConfigCode.ChangeMasterNameIfDebug) && item.GetValue<bool>())
					continue;
				if ((item.Code == ConfigCode.LastKey) && (item.GetValue<long>() == 0))
					continue;
				#region EM_私家版_LoadText＆SaveText機能拡張
				if (item.Code == ConfigCode.ValidExtension)
				{
					var ex = (ConfigItem<List<string>>)item;
					var sb = new System.Text.StringBuilder();
					#region EM_私家版_Emuera多言語化改造
					// sb.Append(ex.Text).Append(":");
					sb.Append(GetConfigValue<bool>(ConfigCode.EnglishConfigOutput) ? ex.EngText : ex.Text).Append(':');
					#endregion
					foreach (var str in ex.Value)
					{
						sb.Append(str).Append(',');
					}
					sb.Remove(sb.Length - 1, 1);
					writer.WriteLine(sb.ToString());
					continue;
				}
				#endregion
				//if (item.Code == ConfigCode.IgnoreWarningFiles)
				//{
				//    List<string> files = item.GetValue<List<string>>();
				//    foreach (string filename in files)
				//        writer.WriteLine(item.Text + ":" + filename.ToString());
				//    continue;
				//}
				writer.WriteLine(item.ToString());
			}
		}
		catch (Exception ex)
		{
			EmueraLog.Warn("Config", $"SaveConfig failed: {ex}");
			return false;
		}
		return true;
	}

	public bool ReLoadConfig()
	{
		//_fixed.configの中身が変わった場合、非固定になったものが保持されてしまうので、ここで一旦すべて解除
		foreach (AConfigItem item in configArray)
		{
			if (item == null)
				continue;
			if (item.Fixed)
				item.Fixed = false;
		}
		LoadConfig();
		return true;
	}

	public bool LoadConfig()
	{
		return LoadConfigCore(configPath, Program.CsvDir);
	}

	/// <summary>
	/// Issue 12：显式指定游戏目录加载 config——/load-game 切换目录后使用。
	/// 避免依赖构造时 GamePaths.Current 的时序，直接读 exeDir 下的 emuera.config
	/// 及 exeDir/csv 下的 default/fixed config。
	/// </summary>
	public bool LoadConfig(string exeDir)
	{
		string csvDir;
		if (SafPath.IsContentUri(exeDir))
		{
			var accessor = GamePaths.Current?.DirAccessor;
			csvDir = accessor != null
				? accessor.ResolveSubPath(exeDir, "csv")
				: CombineConfigPath(exeDir, "csv");
		}
		else
			csvDir = Path.Combine(exeDir, "csv") + Path.DirectorySeparatorChar;
		string configPathLocal = CombineConfigPath(exeDir, "emuera.config");
		return LoadConfigCore(configPathLocal, csvDir);
	}

	private bool LoadConfigCore(string configPathLocal, string csvDir)
	{
		string defaultConfigPath = CombineConfigPath(csvDir, "_default.config");
		string fixedConfigPath = CombineConfigPath(csvDir, "_fixed.config");
		if (!ConfigFileExists(defaultConfigPath))
			defaultConfigPath = CombineConfigPath(csvDir, "default.config");
		if (!ConfigFileExists(fixedConfigPath))
			fixedConfigPath = CombineConfigPath(csvDir, "fixed.config");

		bool loadedDefault = loadConfig(defaultConfigPath, false);
		bool loadedMain = loadConfig(configPathLocal, false);
		bool loadedFixed = loadConfig(fixedConfigPath, true);

		// 候选 2 / ADR-0009：原 Config.SetConfig(this) 已坍缩——视图（Config）是薄转发层，
		// 无副本可刷新；clamp/语言/存档目录逻辑下沉为实例方法 ApplyPostLoadEffects。
		ApplyPostLoadEffects();

		// DisplayReport 覆盖：三源（default/emuera/fixed.config）已合并且 post-load 已应用，
		// 此时压 DisplayReport 能盖过一切来源（含 fixed.config 的强制值）。
		// 仅内存覆盖，不落盘（不影响下方 needSave/SaveConfig 逻辑）。
		if (OverrideDisplayReport.HasValue)
			GetItem(ConfigCode.DisplayReport).SetValue(OverrideDisplayReport.Value);

		bool needSave = false;
		// Issue 12：使用当前实际读取的 config 文件路径判断是否需要创建默认 config。
		// SAF：不能用 File.Exists（content URI 恒 false），否则会误判缺失并 SaveConfig。
		if (!ConfigFileExists(configPathLocal))
			needSave = true;
		if (CheckUpdate())
		{
			GetItem(ConfigCode.LastKey).SetValue(GetConfigValue<long>(ConfigCode.LastKey));
			needSave = true;
		}
		if (needSave && !SaveConfig(configPathLocal))
			EmueraLog.Error("Config", $"Could not create or update {configPathLocal}.");

		EmueraLog.Info("Config",
			$"LoadConfig: path={configPathLocal}, main={loadedMain}, default={loadedDefault}, fixed={loadedFixed}, " +
			$"SystemSaveInBinary={GetConfigValue<bool>(ConfigCode.SystemSaveInBinary)}, UseERD={GetConfigValue<bool>(ConfigCode.UseERD)}");
		return true;
	}

	/// <summary>游戏目录下拼配置文件路径：SAF 走 DirAccessor.CombinePath，本地走字符串拼接（ExeDir 已带分隔符）。</summary>
	private static string CombineConfigPath(string dir, string fileName)
	{
		if (string.IsNullOrEmpty(dir))
			return fileName;
		if (SafPath.IsContentUri(dir))
		{
			var accessor = GamePaths.Current?.DirAccessor;
			if (accessor != null)
				return accessor.CombinePath(dir, fileName);
			return dir.TrimEnd('/') + "/" + fileName;
		}
		if (dir.EndsWith('\\') || dir.EndsWith('/'))
			return dir + fileName;
		return Path.Combine(dir, fileName);
	}

	private static bool ConfigFileExists(string path)
	{
		if (string.IsNullOrEmpty(path))
			return false;
		return SafCompat.FileExists(path);
	}

	#region EM_私家版_Emuera多言語化改造
	// 候选 2 / ADR-0009：原 Config.SetConfig 的 clamp/语言/存档目录逻辑下沉为实例方法。
	// 原 Config 静态镜像已移除，视图（Config）直接转发 _data，故此处就地修正 _data 的 item。
	/// <summary>
	/// 加载后置处理：clamp 非法范围、按 useLanguage 计算语言 LCID 与编码、必要时创建/迁移存档目录。
	/// 原 Config.SetConfig 的等价逻辑（视图无副本后，直接写回 _data 的 item）。
	/// </summary>
	public void ApplyPostLoadEffects()
	{
		UseLanguage lang = GetConfigValue<UseLanguage>(ConfigCode.useLanguage);
		switch (lang)
		{
			case UseLanguage.JAPANESE:
				Language = 0x0411; LangManager.setEncode(932); break;
			case UseLanguage.KOREAN:
				Language = 0x0412; LangManager.setEncode(949); break;
			case UseLanguage.CHINESE_HANS:
				Language = 0x0804; LangManager.setEncode(936); break;
			case UseLanguage.CHINESE_HANT:
				Language = 0x0404; LangManager.setEncode(950); break;
		}

		if (GetConfigValue<int>(ConfigCode.FontSize) < 8)
		{
			Dialog.Show(trmb.ConfigError.Text, trmb.TooSmallFontSize.Text);
			GetConfigItem(ConfigCode.FontSize).SetValue(8);
		}
		if (GetConfigValue<int>(ConfigCode.LineHeight) < GetConfigValue<int>(ConfigCode.FontSize))
		{
			Dialog.Show(trmb.ConfigError.Text, trmb.LineHeightLessThanFontSize.Text);
			GetConfigItem(ConfigCode.LineHeight).SetValue(GetConfigValue<int>(ConfigCode.FontSize));
		}
		if (GetConfigValue<int>(ConfigCode.SaveDataNos) < 20)
		{
			Dialog.Show(trmb.ConfigError.Text, trmb.TooSmallDisplaySaveData.Text);
			GetConfigItem(ConfigCode.SaveDataNos).SetValue(20);
		}
		if (GetConfigValue<int>(ConfigCode.SaveDataNos) > 80)
		{
			Dialog.Show(trmb.ConfigError.Text, trmb.TooLargeDisplaySaveData.Text);
			GetConfigItem(ConfigCode.SaveDataNos).SetValue(80);
		}
		if (GetConfigValue<int>(ConfigCode.MaxLog) < 500)
		{
			Dialog.Show(trmb.ConfigError.Text, trmb.TooSmallLogSize.Text);
			GetConfigItem(ConfigCode.MaxLog).SetValue(500);
		}
		if (GetConfigValue<TextDrawingMode>(ConfigCode.TextDrawingMode) == TextDrawingMode.WINAPI)
		{
#if HEADLESS
			EmueraLog.Warn("Config", trmb.DoNotSupportWINAPI.Text);
#else
			MessageBox.Show(trmb.DoNotSupportWINAPI.Text);
#endif
			GetConfigItem(ConfigCode.TextDrawingMode).SetValue(TextDrawingMode.TEXTRENDERER);
		}

		if (!GetConfigValue<bool>(ConfigCode.AllowFunctionOverloading))
			GetConfigItem(ConfigCode.WarnFunctionOverloading).SetValue(true);

		// SAF：跳过本地 Directory + 根目录挪档；仅确保 sav 目录可建（方案 B）
		if (GetConfigValue<bool>(ConfigCode.UseSaveFolder))
		{
			if (SafPath.IsContentUri(Program.ExeDir))
			{
				try
				{
					if (!SafCompat.DirectoryExists(Config.ForceSavDir))
						SafCompat.CreateDirectory(Config.ForceSavDir);
				}
				catch (Exception ex)
				{
#if HEADLESS
					EmueraLog.Warn("Config", $"SAF CreateSavDir skipped/failed: {ex.Message}");
#endif
				}
			}
			else if (!Directory.Exists(SavDir))
			{
				createSavDirAndMoveFiles();
			}
		}
	}

	/// <summary>存档目录（derived，与 Config.SavDir 同义；不缓存，现算现用）。</summary>
	private string SavDir
	{
		get
		{
			string forceSavDir = SafCompat.ResolveSubPath(Program.ExeDir, "sav");
			return GetConfigValue<bool>(ConfigCode.UseSaveFolder) ? forceSavDir : Program.ExeDir;
		}
	}

	//先にApplyPostLoadEffectsを呼ぶこと
	//戻り値はセーブが必要かどうか
	public bool CheckUpdate()
	{
		ReduceArgumentOnLoadFlag flag = GetConfigValue<ReduceArgumentOnLoadFlag>(ConfigCode.ReduceArgumentOnLoad);
		if (flag != ReduceArgumentOnLoadFlag.ONCE)
		{
			if (flag == ReduceArgumentOnLoadFlag.YES)
				NeedReduceArgumentOnLoad = true;
			else if (flag == ReduceArgumentOnLoadFlag.NO)
				NeedReduceArgumentOnLoad = false;
			return false;
		}

		long key = getUpdateKey();
		bool updated = GetConfigValue<long>(ConfigCode.LastKey) != key;
		NeedReduceArgumentOnLoad = updated;
		if (updated)
			GetConfigItem(ConfigCode.LastKey).SetValue(key);
		return updated;
	}

	private long getUpdateKey()
	{
		SearchOption option = SearchOption.TopDirectoryOnly;
		if (GetConfigValue<bool>(ConfigCode.SearchSubdirectory))
			option = SearchOption.AllDirectories;
		if (SafPath.IsContentUri(Program.ExeDir))
			return getSafUpdateKey(option);

		// issue 19：本地分支也必须经 accessor——直连 Directory.GetFiles 会绕过通配大小写不敏感
		// （.NET 在 Unix/Android 由托管代码 glob），只命中全大写扩展名，与 SAF 分支算出不同 key。
		var accessor = GamePaths.Current.DirAccessor;
		string[] erbFiles = accessor.GetFiles(Program.ErbDir, "*.ERB", option);
		string[] csvFiles = accessor.GetFiles(Program.CsvDir, "*.CSV", option);
		long[] writetimes = new long[erbFiles.Length + csvFiles.Length];
		for (int i = 0; i < erbFiles.Length; i++)
			if (Path.GetExtension(erbFiles[i]).Equals(".ERB", StringComparison.OrdinalIgnoreCase))
				writetimes[i] = File.GetLastWriteTime(erbFiles[i]).ToBinary();
		for (int i = 0; i < csvFiles.Length; i++)
			if (Path.GetExtension(csvFiles[i]).Equals(".CSV", StringComparison.OrdinalIgnoreCase))
				writetimes[i + erbFiles.Length] = File.GetLastWriteTime(csvFiles[i]).ToBinary();
		long key = 0;
		for (int i = 0; i < writetimes.Length; i++)
		{
			unchecked
			{
				key ^= writetimes[i] * 1103515245 + 12345;
			}
		}
		return key;
	}

	/// <summary>
	/// SAF providers do not expose a portable last-write-time contract. Keep ONCE
	/// deterministic by hashing the logical ERB/CSV paths, which still detects
	/// added/removed files without calling File.GetLastWriteTime(content://...).
	/// </summary>
	private static long getSafUpdateKey(SearchOption option)
	{
		try
		{
			var accessor = GamePaths.Current.DirAccessor;
			var files = new List<string>();
			files.AddRange(accessor.GetFiles(Program.ErbDir, "*.ERB", option)
				.Where(path => string.Equals(Path.GetExtension(SafPath.GetLogicalFileName(path)), ".ERB", StringComparison.OrdinalIgnoreCase))
				.Select(path => "erb:" + SafPath.GetRelativePathFromRoot(Program.ExeDir, path)));
			files.AddRange(accessor.GetFiles(Program.CsvDir, "*.CSV", option)
				.Where(path => string.Equals(Path.GetExtension(SafPath.GetLogicalFileName(path)), ".CSV", StringComparison.OrdinalIgnoreCase))
				.Select(path => "csv:" + SafPath.GetRelativePathFromRoot(Program.ExeDir, path)));

			files.Sort(StringComparer.OrdinalIgnoreCase);
			unchecked
			{
				long key = 1469598103934665603L;
				foreach (var file in files)
				{
					foreach (var ch in file)
					{
						key ^= ch;
						key *= 1099511628211L;
					}
				}
				return key;
			}
		}
		catch (Exception ex)
		{
			EmueraLog.Warn("Config", $"SAF update detection unavailable; continuing without timestamp reduction: {ex.Message}");
			return 0;
		}
	}

	private void createSavDirAndMoveFiles()
	{
		string savDir = SavDir;
		try
		{
			Directory.CreateDirectory(savDir);
		}
		catch
		{
			Dialog.Show(trmb.FolderCreationFailure.Text, trmb.FailedCreateSavFolder.Text);
			return;
		}
		bool existGlobal = File.Exists(Program.ExeDir + "global.sav");
		string[] savFiles = Directory.GetFiles(Program.ExeDir, "save*.sav", SearchOption.TopDirectoryOnly);
		if (!existGlobal && savFiles.Length == 0)
			return;
		var result = Dialog.ShowPrompt(trmb.SavFolderCreated.Text, trmb.DataTransfer.Text);
		if (result == false)
			return;
		if (!Directory.Exists(savDir))
		{
			Dialog.Show(trmb.DataTransferFailure.Text, trmb.MissingSavFolder.Text);
			return;
		}
		try
		{
			if (File.Exists(Program.ExeDir + "global.sav"))
				File.Move(Program.ExeDir + "global.sav", savDir + "global.sav");
			savFiles = Directory.GetFiles(Program.ExeDir, "save*.sav", SearchOption.TopDirectoryOnly);
			foreach (string oldpath in savFiles)
				File.Move(oldpath, savDir + Path.GetFileName(oldpath));
		}
		catch
		{
			Dialog.Show(trmb.DataTransferFailure.Text, trmb.FailedMoveSavFiles.Text);
		}
	}
	#endregion

	private bool loadConfig(string confPath, bool fix)
	{
		// SAF：必须用 SafCompat / DirAccessor，File.Exists(content://) 恒 false
		if (!ConfigFileExists(confPath))
			return false;
		using var eReader = new EraStreamReader(false);
		if (!eReader.Open(confPath))
			return false;
		ScriptPosition? pos = null;
		try
		{
			string line = null!;
			//bool defineIgnoreWarningFiles = false;
			while ((line = eReader.ReadLine()) != null)
			{
				if ((line.Length == 0) || (line[0] == ';'))
					continue;
				pos = new ScriptPosition(eReader.Filename, eReader.LineNo);
				string[] tokens = line.Split([':']);
				if (tokens.Length < 2)
					continue;
				#region EM_私家版_Emuera多言語化改造
				AConfigItem item = GetConfigItem(tokens[0].Trim());
				#endregion
				if (item != null)
				{
					//1806beta001 CompatiDRAWLINEの廃止、CompatiLinefeedAs1739へ移行
					if (item.Code == ConfigCode.CompatiDRAWLINE)
					{
						item = GetConfigItem(ConfigCode.CompatiLinefeedAs1739);
					}
					//if ((item.Code == ConfigCode.IgnoreWarningFiles))
					//{ 
					//    if (!defineIgnoreWarningFiles)
					//        (item.GetValue<List<string>>()).Clear();
					//    defineIgnoreWarningFiles = true;
					//    if ((item.Fixed) && (fix))
					//        item.Fixed = false;
					//}

					if (item.Code == ConfigCode.TextEditor)
					{
						//パスの関係上tokens[2]は使わないといけない
						if (tokens.Length > 2)
						{
							if (tokens[2].StartsWith('\\'))
								tokens[1] += ":" + tokens[2];
							if (tokens.Length > 3)
							{
								for (int i = 3; i < tokens.Length; i++)
								{
									tokens[1] += ":" + tokens[i];
								}
							}
						}
					}
					if (item.Code == ConfigCode.EditorArgument)
					{
						//半角スペースを要求する引数が必要なエディタがあるので別処理で
						((ConfigItem<string>)item).Value = tokens[1];
						continue;
					}
					if (item.Code == ConfigCode.MaxLog && Program.AnalysisMode)
					{
						//解析モード時はここを上書きして十分な長さを確保する
						tokens[1] = "10000";
					}
					if (item.TryParse(tokens[1]) && fix)
						item.Fixed = true;
				}
			}
		}
		catch (EmueraException ee)
		{
			ParserMediator.ConfigWarn(ee.Message, pos, 1, null!);
		}
		catch (Exception exc)
		{
			ParserMediator.ConfigWarn(exc.GetType().ToString() + ":" + exc.Message, pos, 1, exc.StackTrace!);
		}
		finally { eReader.Dispose(); }
		return true;
	}

	#region replace
	// 1.52a改変部分　（単位の差し替えおよび前置、後置のためのコンフィグ処理）
	public void LoadReplaceFile(string filename)
	{
		using var eReader = new EraStreamReader(false);
		if (!eReader.Open(filename))
			return;
		ScriptPosition? pos = null;
		try
		{
			string line = null!;
			while ((line = eReader.ReadLine()) != null)
			{
				line = line.Trim();
				if (line.Length == 0)
					continue;
				if (line[0] == ';')
					continue;
				pos = new ScriptPosition(eReader.Filename, eReader.LineNo);
				string[] tokens = line.Split(',', ':');
				if (tokens.Length < 2)
					continue;
				string itemName = tokens[0].Trim();
				tokens[1] = line[(tokens[0].Length + 1)..];
				if (string.IsNullOrEmpty(tokens[1].Trim()))
					continue;
				AConfigItem item = GetReplaceItem(itemName);
				if (item != null)
					item.TryParse(tokens[1]);
			}
		}
		catch (EmueraException ee)
		{
			ParserMediator.Warn(ee.Message, pos, 1);
		}
		catch (Exception exc)
		{
			ParserMediator.Warn(exc.GetType().ToString() + ":" + exc.Message, pos, 1, exc.StackTrace!);
		}
		finally { eReader.Dispose(); }
	}

	#endregion

	#region debug


	public bool SaveDebugConfig()
	{
		try
		{
			#region EM_私家版_Emuera多言語化改造
			using var writer = new StreamWriter(SafCompat.OpenWrite(ConfigDebugPath), Config.Encode);

			// for (int i = 0; i < debugArray.Length; i++)
			for (int i = 0; i < debugArray.Count; i++)
			#endregion
			{
				AConfigItem item = debugArray[i];
				if (item == null)
					continue;
				writer.WriteLine(item.ToString());
			}
		}
		catch (Exception ex)
		{
			EmueraLog.Warn("Config", $"SaveDebugConfig failed: {ex}");
			return false;
		}
		return true;
	}

	public bool LoadDebugConfig()
	{
		using var eReader = new EraStreamReader(false);
		if (!SafCompat.FileExists(ConfigDebugPath))
			goto err;
		if (!eReader.Open(ConfigDebugPath))
			goto err;
		ScriptPosition? pos = null;
		try
		{
			string line = null!;
			while ((line = eReader.ReadLine()) != null)
			{
				if ((line.Length == 0) || (line[0] == ';'))
					continue;
				pos = new ScriptPosition(eReader.Filename, eReader.LineNo);
				string[] tokens = line.Split([':']);
				if (tokens.Length < 2)
					continue;
				AConfigItem item = GetDebugItem(tokens[0].Trim());
				if (item != null)
				{
					item.TryParse(tokens[1]);
				}
#if DEBUG
				//else
				//	throw new Exception("コンフィグファイルが変");
#endif
			}
		}
		catch (EmueraException ee)
		{
			ParserMediator.ConfigWarn(ee.Message, pos, 1, null!);
			goto err;
		}
		catch (Exception exc)
		{
			ParserMediator.ConfigWarn(exc.GetType().ToString() + ":" + exc.Message, pos, 1, exc.StackTrace!);
			goto err;
		}
		finally { eReader.Dispose(); }
		return true;
	err:
		return false;
	}

	#endregion
}
