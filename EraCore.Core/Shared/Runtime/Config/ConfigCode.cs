namespace MinorShift.Emuera.Runtime.Config;

internal enum DisplayWarningFlag
{
	IGNORE = 0,
	LATER = 1,
	ONCE = 2,
	DISPLAY = 3,
}

internal enum ReduceArgumentOnLoadFlag
{
	YES = 0,
	ONCE = 1,
	NO = 2,
}

internal enum TextDrawingMode
{
	GRAPHICS = 0,
	TEXTRENDERER = 1,
	WINAPI = 2,
}

internal enum UseLanguage
{
	JAPANESE = 0,
	KOREAN = 1,
	CHINESE_HANS = 2,
	CHINESE_HANT = 3,
}

internal enum TextEditorType
{
	SAKURA = 0,
	TERAPAD = 1,
	EMEDITOR = 2,
	USER_SETTING = 3,
}

internal enum ConfigCode
{
	IgnoreCase = 0,
	UseRenameFile = 1,
	UseReplaceFile = 2,
	UseMouse = 3,
	UseMenu = 4,
	UseDebugCommand = 5,
	AllowMultipleInstances = 6,
	AutoSave = 7,
	SizableWindow = 8,
	TextDrawingMode = 9,
	UseImageBuffer = 10,
	WindowX = 11,
	WindowY = 12,
	MaxLog = 13,
	PrintCPerLine = 14,
	PrintCLength = 15,
	FontName = 16,
	FontSize = 17,
	LineHeight = 18,
	ForeColor = 19,
	BackColor = 20,
	FocusColor = 21,
	LogColor = 22,
	FPS = 23,
	SkipFrame = 24,
	DisplayWarningLevel = 26,
	DisplayReport = 27,
	ReduceArgumentOnLoad = 28,
	//ReduceFormattedStringOnLoad = 29,
	IgnoreUncalledFunction = 30,
	FunctionNotFoundWarning = 31,
	FunctionNotCalledWarning = 32,
	//IgnoreWarningFiles = 33,
	// lazy-function-load（B 线 B1）：函数级惰性物化；AnalysisMode 一律强制 eager。Issue 04 全绿即默认 true。
	LazyFunctionLoad = 300,
	// b2-irdiskcache（B 线 B2）：函数粒度 IR 磁盘缓存开关（默认 true）；FALSE = 完全回到 B1 现状。
	// 生效前提 = IrDiskCacheLoad && LazyFunctionLoad && !AnalysisMode；eager 下 IR 不建不读。沿 LazyFunctionLoad 先例。
	IrDiskCacheLoad = 301,
	// issues/12 M4：命中轮用 IR 头存 firstDir 清单替代 SAF GetDirectories("*#*") 通配扫描（默认 true）。
	// FALSE = 命中轮恒走扫描（回退硬门）；建库轮写侧与指纹口径不受此开关影响，翻回 true 免整库重建。
	IrFirstDirList = 302,
	// issues/13 M5：命中轮用清单 sidecar 回放替代全树枚举（默认 true，仅 content-URI 游戏生效——
	// 本地路径真实 stat 自愈，不走回放）。FALSE = 命中轮恒走真实枚举（回退硬门）；建库轮写侧不受影响。
	IrFileListCache = 303,
	// issues/10 10-B：CSV/ERH 源内容缓存（era_src.dat，默认 true，仅 content-URI 游戏生效）——
	// 命中轮免 SAF 逐文件懒读（真机读窗 13.3s），重水化前逐目录元数据校验（修改/删除/已知目录
	// 内新增可检）。FALSE = 命中轮回到懒读现状；建库轮写侧不受影响。
	SrcContentCache = 304,
	// legacy-plugin-compat：DLL 插件安全门禁（pluginsAware.txt）是否强制。默认 false = 兼容优先——
	// eraTW 等社区游戏不自带 pluginsAware.txt，其可在原版引擎运行的 Skia 亦无此门禁；
	// 开启后行为回到严格模式（发现带 PluginManifest 的 DLL 且无该文件即抛 ExeEE）。
	PluginsAwareRequired = 305,
	ChangeMasterNameIfDebug = 34,
	// 35 曾为 LastKey（最終更新コード）——已移除：ONCE 的变更判据改寄生到 IR 指纹，
	// 本引擎不再读写该键（见 ConfigData.ApplyReduceArgumentOnLoad / LoadConfigCore 的写回策略）。
	ButtonWrap = 36,
	SearchSubdirectory = 37,
	SortWithFilename = 38,
	SetWindowPos = 39,
	WindowPosX = 40,
	WindowPosY = 41,
	ScrollHeight = 42,
	SaveDataNos = 43,
	WarnBackCompatibility = 44,
	AllowFunctionOverloading = 45,
	WarnFunctionOverloading = 46,
	WindowMaximixed = 47,
	TextEditor = 48,
	EditorType = 99,
	EditorArgument = 49,
	WarnNormalFunctionOverloading = 50,
	CompatiErrorLine = 51,
	CompatiCALLNAME = 52,
	DebugShowWindow = 53,
	DebugWindowTopMost = 54,
	DebugWindowWidth = 55,
	DebugWindowHeight = 56,
	DebugSetWindowPos = 57,
	DebugWindowPosX = 58,
	DebugWindowPosY = 59,
	UseSaveFolder = 60,
	CompatiRAND = 61,
	CompatiDRAWLINE = 62,
	CompatiFunctionNoignoreCase,
	SystemAllowFullSpace,
	CompatiLinefeedAs1739,
	useLanguage,
	SystemSaveInBinary,
	CompatiFuncArgAutoConvert,
	CompatiFuncArgOptional,
	AllowLongInputByMouse,
	CompatiCallEvent,
	SystemIgnoreTripleSymbol,
	CompatiSPChara,
	TimesNotRigorousCalculation,
	//一文字変数の禁止オプションを考えた名残
	//ForbidOneCodeVariable,
	SystemNoTarget,
	SystemIgnoreStringSet,

	MoneyLabel = 100,
	MoneyFirst = 101,
	LoadLabel = 102,
	MaxShopItem = 103,
	DrawLineString = 104,
	BarChar1 = 105,
	BarChar2 = 106,
	TitleMenuString0 = 107,
	TitleMenuString1 = 108,
	ComAbleDefault = 109,
	StainDefault = 110,
	TimeupLabel = 111,
	ExpLvDef = 112,
	PalamLvDef = 113,
	pbandDef = 114,
	RelationDef = 115,

	UseKeyMacro = 162,

	#region EE_UPDATECHECK
	ForbidUpdateCheck,
	#endregion
	#region EE_ERDConfig
	UseERD,
	#endregion
	#region EE_ERDNAME
	VarsizeDimConfig,
	#endregion
	#region EE_重複定義の確認
	CheckDuplicateIdentifier,
	#endregion
	#region EE_行連結の改行コード置換
	ReplaceContinuationBR,
	#endregion

	#region EM_私家版_LoadText＆SaveText機能拡張
	ValidExtension = 200,
	#endregion
	#region EM_私家版_セーブ圧縮
	ZipSaveData,
	#endregion
	#region EmuEra-Rikaichan related settings
	RikaiEnabled = 250,
	RikaiFilename,
	RikaiColorBack,
	RikaiColorText,
	RikaiUseSeparateBoxes,
	#endregion
	Ctrl_Z_Enabled,
	#region EM_私家版_Emuera多言語化改造
	EnglishConfigOutput,
	EmueraLang,
	#endregion
	#region EM_私家版_Icon指定機能
	EmueraIcon,
	#endregion
	#region EE_AnchorのCB機能移植
	CBUseClipboard,
	CBIgnoreTags,
	CBReplaceTags,
	CBNewLinesOnly,
	CBClearBuffer,
	CBTriggerLeftClick,
	CBTriggerMiddleClick,
	CBTriggerDoubleLeftClick,
	CBTriggerAnyKeyWait,
	CBTriggerInputWait,
	CBMaxCB,
	CBBufferSize,
	CBScrollCount,
	CBMinTimer,
	#endregion
	#region erb-loader-parallel
	ParallelErbLoad,
	ParallelParseScript,
	#endregion
}
