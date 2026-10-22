using MinorShift.Emuera.Files;
using System.Collections.Generic;

namespace MinorShift.Emuera;

/// <summary>
/// 共享运行时上下文（路径转发 + 状态字段）。拆分自原 Emuera.Headless.Program（issue 01）。
/// 50+ 处 Shared/ 源码调用 <c>Program.xxx</c> 转发到会话作用域的根
/// （<see cref="GameRootEnvironment.Current"/>）——issue-008 后本类是<b>唯一</b>保留的兼容转发垫片。
/// <para>
/// issue-009 contract：装配期提交已删除，本垫片只在 <see cref="CoreSession"/> 建立的游戏循环
/// 作用域内有值；作用域外访问路径属性会抛 <see cref="System.InvalidOperationException"/>，
/// 而不是回落到进程启动目录。初始化与配置加载不走本垫片——它们显式携带根/访问器。
/// </para>
/// <para>
/// 此类必须留在 Core 且名字不变（Phase 2 重命名为 <c>EmueraContext</c>）。
/// issue 02 已删除 <c>Main</c>（issue 01 移至 <c>Emuera.Headless.Cli/HeadlessEntry.cs</c>）+
/// <c>[STAThread]</c>（随 Main 迁移）+ <c>LoadFonts</c> 方法（<see cref="HeadlessFontCollection.AddFontFile"/>
/// 是空实现 no-op，GameLoopComposer.RunAsync 内的调用一并移除，零行为变化）。运行时初始化由
/// <see cref="EmueraRuntimeInitializer.Initialize"/> 封装，Cli 与 MAUI 共用。
/// </para>
/// </summary>
static class Program
{
    // === 路径属性转发（保持共享文件零改动；会话作用域唯一来源）===
    public static string ExeDir => GameRootEnvironment.RequireCurrent(nameof(ExeDir)).ExeDir;
    public static string CsvDir => GameRootEnvironment.RequireCurrent(nameof(CsvDir)).CsvDir;
    public static string ErbDir => GameRootEnvironment.RequireCurrent(nameof(ErbDir)).ErbDir;
    public static string DebugDir => GameRootEnvironment.RequireCurrent(nameof(DebugDir)).DebugDir;
    public static string DatDir => GameRootEnvironment.RequireCurrent(nameof(DatDir)).DatDir;
    public static string ContentDir => GameRootEnvironment.RequireCurrent(nameof(ContentDir)).ContentDir;
    public static string SoundDir => GameRootEnvironment.RequireCurrent(nameof(SoundDir)).SoundDir;
    public static string FontDir => GameRootEnvironment.RequireCurrent(nameof(FontDir)).FontDir;

    /// <summary>
    /// 当前会话作用域的目录访问器——issue-008 起引擎侧经此取访问抽象。
    /// Program 是唯一保留的兼容转发垫片（issue-009：无装配期视图，作用域外抛异常）。
    /// </summary>
    internal static IGameDirAccessor DirAccessor => GameRootEnvironment.RequireCurrent(nameof(DirAccessor)).DirAccessor;
    /// <summary>
    /// ExeName 的 setter 改 internal（原 private）——拆分后由 Cli 的 <c>HeadlessEntry.Main</c>
    /// （经 <see cref="EmueraRuntimeInitializer.Initialize"/>）设置。
    /// </summary>
    public static string ExeName { get; internal set; } = "";

    // === 运行时状态 ===
    public static bool rebootFlag;
    public static bool AnalysisMode;
    public static List<string> AnalysisFiles = new();
    public static bool DebugMode { get; private set; }
}
