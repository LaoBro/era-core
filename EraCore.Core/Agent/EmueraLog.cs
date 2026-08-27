using System;
using System.Diagnostics;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// 统一日志门面（T-027 Phase 1）：分级 + 多目标 Sink，收敛散落的
/// <see cref="Console.Error.WriteLine"/> / <see cref="Console.WriteLine"/> /
/// <see cref="System.Diagnostics.Debug.WriteLine"/> / <see cref="AgentLog.Write"/> 调用。
///
/// 设计：
/// - 级别：Debug &lt; Info &lt; Warn &lt; Error（Off 关闭）。
/// - 三 Sink（可独立阈值）：
///   - FileSink → <see cref="AgentLog"/>（agent.log，时间戳/文件语义/MAUI 开关保留）；
///   - TerminalSink → stderr（默认阈值 Warn：交互式 CLI 只显示警告与错误，Debug/Info 诊断不污染画面）；
///   - DebuggerSink → <see cref="Debug.WriteLine"/>（ADB logcat 的 DOTNET tag，Release 下自动空操作）。
/// - 配置（环境变量）：
///   - EMUERA_LOG_LEVEL    = debug|info|warn|error|off（默认 debug：agent.log 全量保留）；
///   - EMUERA_LOG_TERMINAL  = debug|info|warn|error|off（默认 warn：终端阈值，独立于全局下限）；
///   - EMUERA_AGENT_LOG     = 0|1（既有开关，继续控制 FileSink 是否落盘，向后兼容）。
/// - category 保留既有标签风格（[SaveGlobal] 等），输出格式与历史一致，grep 习惯不破坏。
/// </summary>
internal static class EmueraLog
{
    internal enum Level
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3,
        Off = 4,
    }

    /// <summary>全局下限：低于该级别不记录到任何 Sink。默认 Debug（agent.log 全量）。</summary>
    private static readonly Level MinLevel = ParseLevel("EMUERA_LOG_LEVEL", Level.Debug);

    /// <summary>TerminalSink 独立阈值：低于该级别不进 stderr。默认 Warn（CLI 只显警告+错误）。</summary>
    private static Level TerminalLevel = ParseLevel("EMUERA_LOG_TERMINAL", Level.Warn);

    /// <summary>
    /// 运行时调整终端阈值（T-027 Phase 3）：Server 模式无交互画面，终端即其日志，
    /// 启动横幅/端口提示等 Info 应可见——ServerRunner 启动时调此设为 Info；
    /// CLI 交互保持默认 Warn（Debug/Info 诊断不污染画面）。线程安全用简单赋值即可
    /// （阈值是单调配置，读方取到旧值一瞬无害）。
    /// </summary>
    internal static void SetTerminalLevel(Level level) => TerminalLevel = level;

    /// <summary>
    /// erb-bounded-lifecycle 观测收尾：平台 sink（MAUI Android 启动时注册 → Android.Util.Log）。
    /// 历史：Release 下 DebuggerSink 空操作、stderr 不可见——真机 logcat 里看不到任何阶段标记
    /// （LoadPendingParallel / phase=genmerge / csm），崩溃判别只能靠 SAF 侧散点日志。注册后
    /// 每条 Write 都透传 (level, 整行文本)；Core 保持平台中立，不引用 Android 程序集。
    /// </summary>
    private static Action<Level, string>? _platformSink;

    /// <summary>注册/替换平台 sink（null = 注销）。跟随全局 MinLevel 阈值，不另设独立阈值。</summary>
    internal static void SetPlatformSink(Action<Level, string>? sink) => _platformSink = sink;

    internal static void Debug(string category, string message) => Write(Level.Debug, category, message);
    internal static void Info(string category, string message) => Write(Level.Info, category, message);
    internal static void Warn(string category, string message) => Write(Level.Warn, category, message);
    internal static void Error(string category, string message) => Write(Level.Error, category, message);

    /// <summary>是否记录到文件/调试器（全局下限判定；Off 恒 false）。单测用。</summary>
    internal static bool IsEnabled(Level level) => level != Level.Off && level >= MinLevel;

    /// <summary>是否显示到终端（终端独立阈值判定）。单测用。</summary>
    internal static bool IsTerminalVisible(Level level) => level != Level.Off && level >= TerminalLevel;

    private static void Write(Level level, string category, string message)
    {
        if (level < MinLevel || level == Level.Off) return;
        string line = $"[{category}] {message}";

        // FileSink：agent.log（AgentLog 内部处理启用/关闭/时序）
        AgentLog.Instance.Write(line);

        // TerminalSink：stderr；终端阈值独立（默认 Warn）
        if (level >= TerminalLevel)
        {
            Console.Error.WriteLine(line);
            Console.Error.Flush(); // 确保在游戏画面渲染前输出/按序显示
        }

        // DebuggerSink：ADB logcat（DEBUG 构建才实际输出）
        System.Diagnostics.Debug.WriteLine(line);

        // PlatformSink：宿主注册（MAUI Android → Android.Util.Log；Release 真机唯一可见通道）
        _platformSink?.Invoke(level, line);
    }

    internal static Level ParseLevel(string envVar, Level fallback)
    {
        try
        {
            return ParseLevelValue(Environment.GetEnvironmentVariable(envVar), fallback);
        }
        catch
        {
            return fallback;
        }
    }

    /// <summary>环境变量字符串 → 级别映射（纯函数，单测直接测）。</summary>
    internal static Level ParseLevelValue(string? value, Level fallback)
    {
        if (string.IsNullOrEmpty(value)) return fallback;
        return value.Trim().ToLowerInvariant() switch
        {
            "debug" => Level.Debug,
            "info" => Level.Info,
            "warn" or "warning" => Level.Warn,
            "error" => Level.Error,
            "off" or "none" or "0" or "false" => Level.Off,
            _ => fallback,
        };
    }
}
