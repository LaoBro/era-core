using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MinorShift.Emuera.GameProc;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Config.JSON;
using MinorShift.Emuera.Runtime.Script;
using MinorShift.Emuera.Runtime.Script.Data;
using MinorShift.Emuera.Runtime.Script.Loader;
using MinorShift.Emuera.Runtime.Script.Statements;
using MinorShift.Emuera.Runtime.Script.Statements.Variable;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI.Game;

namespace MinorShift.Emuera.Tests;

/// <summary>
/// ADR-0012：ErhLoader/ErbLoader 隔离单测的共享依赖装配。
///
/// 经 <see cref="Process"/>.Initialize 启动一个最小真实引擎（空 csv/erb 目录），使下游 parser
/// （LogicalLineParser/LexicalAnalyzer 读 GlobalStatic.IdentifierDictionary / GlobalStatic.Process.scaningLine）
/// 拥有有效上下文。sub-loader 自身的 F2 不变量仍由测试证明：它们以显式注入依赖构造、不再收 Process 参数、
/// 不再直读 Program.*/GlobalStatic.IdentifierDictionary（loaders 自身代码已 ADR-0012 清除）。
///
/// 下游 parser 仍经 ADR-0011 兼容层读 GlobalStatic——那是 SystemProc/ScriptProc 同款 Phase 2 约束，非 loaders 自身耦合。
///
/// 注：scope 必须在测试方法的 async 流内开启（AsyncLocal 不跨 await 上浮），故本类用公开构造器（同步开 scope）
/// + <see cref="InitializeAsync"/>（异步启动引擎）两段式，由测试方法直接 `new` 再 `await`。
/// </summary>
internal sealed class LoaderTestHarness : IDisposable
{
	public string TempRoot { get; }
	public string CsvDir { get; }
	public string ErbDir { get; }
	public EmueraConsole Console { get; }
	public Process Process { get; }
	public VariableEvaluator VEvaluator => Process.VEvaluator;
	public IdentifierDictionary IdDic => Process.IdentifierDictionary;
	public ExpressionMediator Exm => Process.EMediator;
	public LabelDictionary LabelDic => Process.LabelDictionary;
	public LoaderEnv Env { get; }

	private IDisposable? _scope;
	private bool _disposed;
	private readonly bool _ownsTempRoot;

	/// <param name="gameDir">
	/// 可选：直接对**真实游戏目录**装配（csv/erb 用该目录下同名子目录），用于 issue 22 的真实语料普查。
	/// 传 null（既有全部调用点）= 原来的行为：临时目录 + 自建空 csv/erb + Dispose 时删除。
	/// 传真实目录时**不创建也不删除**任何东西（只读使用）。
	/// </param>
	/// <param name="dialectProfile">
	/// 可选：会话级方言 profile（实现票 16）。null（既有全部调用点）= <c>v24pure</c>，行为与今天逐位一致。
	/// </param>
	/// <param name="inputHost">
	/// 可选：注入统一输入宿主（ADR-0031）。null = <see cref="NullInputHost"/>（无任何输入能力），
	/// 与骨架前行为逐项一致。M2 起脚本级 <c>GETKEY</c> / <c>MOUSEB</c> 测试经此驱动。
	/// </param>
	/// <param name="viewport">
	/// 可选：注入视口真相源（ADR-0031 决策 4 / M3）。null = <see cref="HeadlessConsole"/> 自身
	/// （由内部 ScrollBar 派生，与 M3 前行为等价）。M3 起几何命中测试经此驱动。
	/// </param>
	public LoaderTestHarness(string? gameDir = null, DialectProfile? dialectProfile = null, IInputHost? inputHost = null, IViewportProvider? viewport = null)
	{
		_ownsTempRoot = gameDir == null;
		TempRoot = gameDir ?? Path.Combine(Path.GetTempPath(), "emuera-loader-test-" + Guid.NewGuid().ToString("N"));
		CsvDir = Path.Combine(TempRoot, "csv") + Path.DirectorySeparatorChar;
		ErbDir = Path.Combine(TempRoot, "erb") + Path.DirectorySeparatorChar;
		if (_ownsTempRoot)
		{
			Directory.CreateDirectory(CsvDir);
			Directory.CreateDirectory(ErbDir);
		}

		// GamePaths.Resolve 使 Program.CsvDir/ErbDir 指向临时目录。
		// 注意：Program 的静态构造器（首次触达 Program.* 时运行）会 GamePaths.Resolve(null, new FileSystemGameDirAccessor()) 重置为默认，
		// 故此处先调一次（触发 EmueraConsole 构造可能间接触发 Program 静态 ctor），再在 InitializeAsync 内重调一次确保生效。
		GamePaths.Resolve(TempRoot, new FileSystemGameDirAccessor());

		// Config scope 在测试方法的 async 流内开启——AsyncLocal 才能下渗到后续 await Task.Run。
		_scope = GlobalStatic.OpenScope(new ConfigData(), dialectProfile);
		EnsureEnginePreInit();

		var ui = new HeadlessConsole(inputHost);
		// M3：宿主可把视口真相源换成自己的（CLI = ScrollController）。
		if (viewport != null)
			ui.AttachViewport(viewport);
		Console = new EmueraConsole(ui, new NullTerminalSetup());
		// EmueraConsole 构造可能触发 Program 静态 ctor（重置 GamePaths），故再 Resolve 一次。
		GamePaths.Resolve(TempRoot, new FileSystemGameDirAccessor());
		// 复刻 ConsoleStateManager.Initialize 的 Process 装配段（不走其 RunEmueraProgram）：
		Process = new Process(Console);
		GlobalStatic.Console = Console;
		GlobalStatic.Process = Process;

		Env = new LoaderEnv(CsvDir, ErbDir, analysisMode: false, analysisFiles: new List<string>(), debugMode: false) { DirAccessor = new FileSystemGameDirAccessor() };
	}

	/// <summary>启动引擎（空目录 Initialize），使 Process 的引擎字段与 GlobalStatic 上下文就绪。须在构造器之后 await。</summary>
	public async Task InitializeAsync()
	{
		// Program 静态 ctor 可能在此前的任意 Program.* 触达时已重置 GamePaths；进入 Initialize 前最终确认。
		GamePaths.Resolve(TempRoot, new FileSystemGameDirAccessor());
		// Preload.Clear 复刻 ConsoleStateManager.Initialize 的清理——避免跨测试 stale 缓存。
		Preload.Clear();
		ForceEagerMode();
		var ok = await Process.Initialize(Env, null);
		if (!ok)
		{
			var lines = string.Join("\n", Console.DisplayLineList.Select(l => l?.ToString() ?? ""));
			throw new InvalidOperationException("Process.Initialize failed in test harness.\nConsole output:\n" + lines);
		}
	}

	/// <summary>
	/// 强制 Loader 测试使用 eager 模式。
	/// Issue 04 后产品默认 LazyFunctionLoad=true，但既有等价性/重解析套件的对照语义都基于 eager 行为；
	/// 本方法在 Initialize 入口自动调用一次，lazy 行为专由 <see cref="LazyFunctionLoadTests"/> 在测试体内显式 <c>SetValue(true)</c> 覆盖。
	/// </summary>
	private void ForceEagerMode()
		=> ConfigData.Current!.GetConfigItem(ConfigCode.LazyFunctionLoad).SetValue(false);

	public string WriteErh(string name, string content)
	{
		var path = Path.Combine(ErbDir, name);
		File.WriteAllText(path, content);
		return path;
	}

	public string WriteErb(string name, string content)
	{
		var path = Path.Combine(ErbDir, name);
		File.WriteAllText(path, content);
		return path;
	}

	/// <summary>写入 ERD 文件（与 ERB 同住 ErbDir——PrepareERDFileNames 以 ErbDir 为根收 .erd）。</summary>
	public string WriteErd(string name, string content)
	{
		var path = Path.Combine(ErbDir, name);
		File.WriteAllText(path, content);
		return path;
	}

	/// <summary>写入 CSV 文件。须在 <see cref="InitializeAsync"/> 之前调用（Initialize 内预加载 CSV）。
	/// <paramref name="name"/> 可含子目录（如 <c>Chara/Chara14 アリス.csv</c>——SearchSubdirectory=YES 的游戏布局），父目录自动创建。</summary>
	public string WriteCsv(string name, string content)
	{
		var path = Path.Combine(CsvDir, name);
		var dir = Path.GetDirectoryName(path);
		if (!string.IsNullOrEmpty(dir))
			Directory.CreateDirectory(dir);
		File.WriteAllText(path, content);
		return path;
	}

	/// <summary>
	/// 预加载 erb/csv 目录（EraStreamReader.OpenOnCache 经 Preload.GetFileLines 读缓存）。
	/// 测试在写入 ERH/ERB 文件后、调用 loader 前必须 await 本方法。
	/// erb-loader-parallel 03：与启动路径共用 Preload.LoadTreesAsync 单一入口。
	/// </summary>
	public async Task PreloadAsync()
	{
		await Preload.LoadTreesAsync(ErbDir, CsvDir, new FileSystemGameDirAccessor());
	}

	/// <summary>
	/// 引擎静态初始化一次性预热：FunctionIdentifier 静态构造器读 JSONConfig.Data 与 Config.IgnoreCase；
	/// IdentifierDictionary..ctor 依赖 Lang。Interlocked 守护只跑一次。
	/// </summary>
	private static int _preInitDone;
	private static void EnsureEnginePreInit()
	{
		if (Interlocked.CompareExchange(ref _preInitDone, 1, 0) != 0) return;
		JSONConfig.Data ??= new JSONConfigData();
		try { Lang.LoadLanguageFiles(); } catch { /* 嵌入资源缺失不致命 */ }
		try { Lang.SetLanguage(); } catch { /* 默认语言即日文，早退或失败均不阻断 */ }
	}

	public void Dispose()
	{
		if (_disposed) return;
		_disposed = true;
		_scope?.Dispose();
		if (!_ownsTempRoot)
			return; // 真实游戏目录：只读使用，绝不删除
		try { if (Directory.Exists(TempRoot)) Directory.Delete(TempRoot, recursive: true); }
		catch { /* 测试临时目录清理失败不致测试失败 */ }
	}

	private sealed class NullTerminalSetup : ITerminalSetup
	{
		public bool IsAnsiEnabled => false;
		public bool TryEnableAnsi() => false;
		public bool TrySetConsoleSize(int cols, int rows) => false;
		public string? DetectFont() => null;
		public bool TryPrepareVtInput() => false;
	}
}
