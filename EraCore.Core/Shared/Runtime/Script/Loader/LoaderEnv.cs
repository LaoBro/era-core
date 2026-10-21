using MinorShift.Emuera.Files;
using System.Collections.Generic;

namespace MinorShift.Emuera.Runtime.Script.Loader;

/// <summary>
/// ADR-0012：加载环境窄 seam。
/// 把 <see cref="Program"/> 的进程级 static（目录 + Analysis/Debug 标志）收口为一个可注入对象，
/// 使 <see cref="ErhLoader"/>/<see cref="ErbLoader"/> 不再直读进程级 static，可在测试中用临时目录构造。
/// <see cref="Process"/>.Initialize 作为 composition root 从 <c>Program.*</c> 构造一次。
/// </summary>
internal sealed class LoaderEnv
{
	public string CsvDir;
	public string ErbDir;
	public bool AnalysisMode;
	public List<string> AnalysisFiles;
	public bool DebugMode;
	/// <summary>ADR-0019：游戏目录访问抽象（SAF vs 文件系统）。必传。</summary>
	public required IGameDirAccessor DirAccessor;

	public LoaderEnv(string csvDir, string erbDir, bool analysisMode, List<string> analysisFiles, bool debugMode)
	{
		CsvDir = csvDir;
		ErbDir = erbDir;
		AnalysisMode = analysisMode;
		AnalysisFiles = analysisFiles;
		DebugMode = debugMode;
	}
}
