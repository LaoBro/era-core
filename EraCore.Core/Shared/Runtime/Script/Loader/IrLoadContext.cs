using System.Collections.Generic;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// 内容指纹的**配置输入集**（design.md §6 白名单，6 位）：影响预解析 / 函数签名 / 比较的配置。
/// <para>
/// ADR-0027 第四刀（issue 05）：原由 <see cref="ErbIrCache.ComputeContentFingerprint"/> 就地直读
/// <c>Config.Config.*</c>，现改为**显式入参**——指纹的配置来源从此可被单点审计与测试构造。
/// </para>
/// <para>
/// <b>不可轻改</b>：本集合是磁盘格式兼容面的一部分（进哈希 ⇒ 变更即让既有 <c>era_ir.dat</c> 判废）。
/// 增删项或改变顺序前请先看 <c>ErbIrCacheTests.Fingerprint_values_are_frozen</c> 的取值钉与
/// <c>IrFingerprintConfig</c> 的各字段注释（含「曾经哈希 NeedReduceArgumentOnLoad 后移除」的循环依赖记录）。
/// </para>
/// </summary>
internal readonly struct IrFingerprintConfig
{
	internal readonly bool IgnoreCase;
	internal readonly bool UseRenameFile;
	internal readonly bool UseReplaceFile;
	internal readonly ReduceArgumentOnLoadFlag ReduceArgumentOnLoad;
	internal readonly bool SystemIgnoreTripleSymbol;
	/// <summary>firstDir 清单是扫描结果的存储形态，其口径受本配置影响（TopDirectoryOnly 只扫 erb 根级 <c>*#*</c>）。</summary>
	internal readonly bool SearchSubdirectory;

	internal IrFingerprintConfig(bool ignoreCase, bool useRenameFile, bool useReplaceFile,
		ReduceArgumentOnLoadFlag reduceArgumentOnLoad, bool systemIgnoreTripleSymbol, bool searchSubdirectory)
	{
		IgnoreCase = ignoreCase;
		UseRenameFile = useRenameFile;
		UseReplaceFile = useReplaceFile;
		ReduceArgumentOnLoad = reduceArgumentOnLoad;
		SystemIgnoreTripleSymbol = systemIgnoreTripleSymbol;
		SearchSubdirectory = searchSubdirectory;
	}

	/// <summary>
	/// 从当前会话配置取快照。**这是本结构唯一的 ambient 读取点**：生产侧只在装载入口调用一次
	/// （<see cref="IrLoadContext.FromCurrent"/>），此后整条 IR 路径只用快照值。
	/// </summary>
	internal static IrFingerprintConfig FromCurrent() => new(
		Config.Config.IgnoreCase,
		Config.Config.UseRenameFile,
		Config.Config.UseReplaceFile,
		Config.Config.ReduceArgumentOnLoad,
		Config.Config.SystemIgnoreTripleSymbol,
		Config.Config.SearchSubdirectory);
}

/// <summary>
/// IR 磁盘缓存路径的**显式上下文**（ADR-0027 第四刀 / issue 05）：本轮装载用到的全部 ambient
/// （开关旋钮、指纹配置、Rename 宏表、缓存路径基座）在此一次性快照，此后 IR 路径各处只问本对象。
/// <para>
/// 形状与第一刀的 <see cref="ErbLoadStrategy"/> 同源：<see cref="FromCurrent"/> 是唯一 ambient 读取点，
/// 调用点在装载入口（<see cref="ErbLoader.LoadErbDir"/> 与启动预判定
/// <see cref="ErbIrLoadPipeline.IsIrHitRound"/> 各自一次——两者是两个独立的装载入口，与第一刀
/// 「两个入口各构造一次」的既有约定一致）。
/// </para>
/// <para>
/// <b>为什么快照而不直读</b>：<see cref="ErbIrLoadPipeline"/> 的方法体内原先散落 <c>Config.Config.*</c>
/// 直读（扫描深度 / 参与判定 / 三个读侧旋钮 / 日志），使「IR 路径读了哪些配置」无法单点审计；
/// 快照后该清单就是本类型的字段清单。
/// </para>
/// </summary>
internal sealed class IrLoadContext
{
	/// <summary>IR 参与判定的主开关（<c>IrDiskCacheLoad</c>）。</summary>
	internal readonly bool IrDiskCacheConfigured;
	/// <summary>懒加载开关（IR 参与的从属前提；AnalysisMode 下恒不参与）。</summary>
	internal readonly bool LazyFunctionLoad;
	/// <summary>AnalysisMode（冻结 IR 参与；见 <see cref="ShouldUseIrCache"/>）。</summary>
	internal readonly bool AnalysisMode;
	/// <summary>命中轮用 IR 头存 firstDir 清单替代通配扫描（读侧旋钮，FALSE = 恒扫描）。</summary>
	internal readonly bool FirstDirListEnabled;
	/// <summary>命中轮用清单 sidecar 回放替代全树枚举（读侧旋钮）。</summary>
	internal readonly bool FileListCacheEnabled;
	/// <summary>内容指纹的配置输入集（同时也供 <see cref="ErbIrLoadPipeline.BuildPendingList"/> 的扫描深度）。</summary>
	internal readonly IrFingerprintConfig Fingerprint;
	/// <summary>Rename 宏表（<c>_Replace</c>/<c>_Rename</c> 预处理依赖；进指纹，加载期由配置装载填充）。</summary>
	internal readonly IReadOnlyDictionary<string, string>? RenameDic;
	/// <summary>IR 缓存路径基座（原 <c>ErbIrLoadPipeline.IrCachePath()</c> 直读 <see cref="Program.ExeDir"/>）。</summary>
	internal readonly string ExeDir;

	private IrLoadContext(bool irDiskCacheConfigured, bool lazyFunctionLoad, bool analysisMode,
		bool firstDirListEnabled, bool fileListCacheEnabled, in IrFingerprintConfig fingerprint,
		IReadOnlyDictionary<string, string>? renameDic, string exeDir)
	{
		IrDiskCacheConfigured = irDiskCacheConfigured;
		LazyFunctionLoad = lazyFunctionLoad;
		AnalysisMode = analysisMode;
		FirstDirListEnabled = firstDirListEnabled;
		FileListCacheEnabled = fileListCacheEnabled;
		Fingerprint = fingerprint;
		RenameDic = renameDic;
		ExeDir = exeDir;
	}

	/// <summary>从当前会话配置 + 进程目录取快照（<b>唯一 ambient 读取点</b>；见类文档）。</summary>
	internal static IrLoadContext FromCurrent(LoaderEnv env) => new(
		Config.Config.IrDiskCacheLoad,
		Config.Config.LazyFunctionLoad,
		env.AnalysisMode,
		Config.Config.IrFirstDirList,
		Config.Config.IrFileListCache,
		IrFingerprintConfig.FromCurrent(),
		ParserMediator.RenameDic,
		Program.ExeDir);

	/// <summary>IR 是否参与本轮（原 <see cref="ErbIrLoadPipeline.ShouldUseIrCache"/> 的入参来源）。</summary>
	internal bool ShouldUseIrCache =>
		ErbIrLoadPipeline.ShouldUseIrCache(IrDiskCacheConfigured, LazyFunctionLoad, AnalysisMode);

	/// <summary>IR 缓存文件路径（content-URI 经 <see cref="ErbIrCache.GetIrCachePath"/> 镜像到 app 私有存储）。</summary>
	internal string IrCachePath() => ErbIrCache.GetIrCachePath(ExeDir);

	/// <summary>内容指纹（文件集合 + firstDir 清单 + 本条上下文携带的配置/Rename 表）。</summary>
	internal long ComputeContentFingerprint(IReadOnlyList<(string Logical, string Physical)> files,
		IReadOnlyList<string>? firstDirs = null)
		=> ErbIrCache.ComputeContentFingerprint(files, RenameDic, firstDirs, Fingerprint);
}
