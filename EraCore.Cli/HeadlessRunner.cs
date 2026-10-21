using MinorShift.Emuera.Files;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Input;
using MinorShift.Emuera.Output;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Utils.EvilMask;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.UI.Game;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Sessions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal static class HeadlessRunner
{
    public static async Task RunAsync(GamePaths paths, string protocolArg, string termWidthHint, ITerminalSetup terminalSetup, ConfigData configData, string? dialectProfileId = null)
    {
        EmueraLog.Info("headless", $"Emuera {AssemblyData.EmueraVersionText} 无头模式启动");
        EmueraLog.Info("headless", $"工作目录: {paths.ExeDir}");
        EmueraLog.Info("headless", $"协议模式: {protocolArg}");
        EmueraLog.Info("headless", $"字符宽度提示: {termWidthHint}");
        if (dialectProfileId != null)
            EmueraLog.Info("headless", $"方言 profile: {dialectProfileId}");

        try
        {
            ITerminalInput terminalInput;
            try
            {
                terminalInput = OperatingSystem.IsWindows()
                    ? new WindowsTerminalInput()
                    : new PosixTerminalInput();
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                // stdin 重定向 / 终端不支持 raw 模式等环境错误——视为 fatal。
                // ADR-0005：CLI 协议层 VT-only，无法在非交互式终端运行。
                throw new HeadlessFatalException(
                    "终端输入初始化失败：" + ex.Message + "。" +
                    "请在真实交互式终端运行（如 Windows Terminal / ConPTY / 现代 SSH），" +
                    "或改用 --server 模式。",
                    ex);
            }

            using (terminalInput)
            {
                // issue-006：CLI 也经 CoreSession 组装（三轴门面由会话持有），
                // 协议只管渲染与 VT 输入编排。
                var session = CoreSession.CreateForCli(
                    configData,
                    terminalSetup,
                    // ADR-0031 / spec-v3 M4/M4b：CLI 只声明能力位，宿主实例由组装根创建。
                    // - Geometry：VT 鼠标位置换算成 console 局部像素 ⇒ MOUSEX / MOUSEY / MOUSEB 有真值。
                    // - KeyboardState：VT 鼠标键电平 ⇒ GETKEY / GETKEYTRIGGERED 有真值。
                    InputCapability.Geometry | InputCapability.KeyboardState,
                    dialectProfileId,
                    (console, ui, ts, displayState, input, output) =>
                    {
                        string hint = (termWidthHint ?? "auto").Trim().ToLowerInvariant();
                        TerminalCharWidthConfig charWidthConfig = hint == "auto"
                            ? TerminalDisplayWidth.DetectCharWidths()
                            : TerminalDisplayWidth.ApplyWidthHint(hint);
                        console.CharWidthConfig = charWidthConfig;

                        ts.DetectFont();
                        PrintTerminalGuidance(charWidthConfig);

                        int charWidth = Math.Max(Config.FontSize / 2, 1);
                        int gameColumns = Config.DrawableWidth / charWidth;
                        int gameRows = Config.WindowY / Config.LineHeight;
                        if (gameColumns > 0 && gameRows > 0)
                            ts.TrySetConsoleSize(gameColumns, gameRows + 4);

                        return SelectCliProtocol(protocolArg, console, ui, ts, terminalInput, configData,
                            input, output, displayState);
                    },
                    p =>
                    {
                        ((AgentCliProtocol)p).RunCliLoop();
                        return Task.CompletedTask;
                    });

                var result = await session.WaitForLoopAsync();

                if (session.StartupException is ArgumentException argEx)
                {
                    EmueraLog.Error("headless", argEx.Message);
                    Environment.Exit(1);
                }
                if (session.StartupException != null)
                {
                    EmueraLog.Error("headless", $"致命错误: {session.StartupException.Message}");
                    AgentLog.Instance.Write("session startup exception: " + session.StartupException);
                    Environment.Exit(1);
                }
                if (session.State == CoreSessionState.Failed)
                {
                    EmueraLog.Error("headless", session.LastError ?? "游戏循环异常终止。");
                    Environment.Exit(1);
                }
                if (result == GameLoopResult.Aborted)
                {
                    Environment.Exit(1);
                }
            }
        }
        catch (HeadlessFatalException ex)
        {
            // ADR-0005：CLI 协议层 VT 初始化失败等不可恢复的环境问题。
            // 输出 stderr 提示（含原因 + 解决方案）+ 写 AgentLog，然后非零退出。
            EmueraLog.Error("headless", $"致命错误: {ex.Message}");
            EmueraLog.Error("headless", "进程将以非零退出码终止。");
            AgentLog.Instance.Write("HeadlessFatalException: " + ex);
            Environment.Exit(1);
        }
        catch (GameExitException)
        {
        }
        catch (ArgumentException ex)
        {
            EmueraLog.Error("headless", ex.Message);
            Environment.Exit(1);
        }
    }

    private static AgentCliProtocol? SelectCliProtocol(string protocolArg, EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput, ConfigData configData, InputPort inputPort, OutputPort outputPort, DisplayState displayState)
    {
        return protocolArg.Trim().ToLowerInvariant() switch
        {
            "auto" => DetectCliProtocol(console, ui, terminalSetup, terminalInput, configData, inputPort, outputPort, displayState),
            "cli" => new AgentCliProtocol(console, ui, terminalSetup, terminalInput, configData, inputPort, outputPort, displayState),
            "jsonl" => throw new ArgumentException(
                "stdin 管道 JSONL 模式已废弃（T-024），请使用 --server 模式", nameof(protocolArg)),
            _ => throw new ArgumentException($"未知协议模式: {protocolArg}", nameof(protocolArg))
        };
    }

    private static AgentCliProtocol? DetectCliProtocol(EmueraConsole console, IConsoleUI ui, ITerminalSetup terminalSetup, ITerminalInput terminalInput, ConfigData configData, InputPort inputPort, OutputPort outputPort, DisplayState displayState)
    {
        // T-024：stdin 管道模式已废弃，非 server 模式仅支持交互式 CLI 终端。
        if (Console.IsInputRedirected)
        {
            EmueraLog.Error("headless", "stdin 管道模式已废弃（T-024），请使用 --server 模式或交互式终端");
            return null;
        }

        try
        {
            _ = Console.KeyAvailable;
        }
        catch
        {
            return null;
        }

        return new AgentCliProtocol(console, ui, terminalSetup, terminalInput, configData, inputPort, outputPort, displayState);
    }

    private static void PrintTerminalGuidance(TerminalCharWidthConfig config)
    {
        bool anySymbolHalf = !config.BoxDrawingIsWide
                          || !config.GeometricIsWide
                          || !config.MiscSymbolsIsWide;
        bool blockReplaced = config.BlockElementsIsWide;

        if (anySymbolHalf)
        {
            EmueraLog.Info("terminal", "检测到制表符/几何/符号字符被渲染为半角，已自动补空格补偿对齐。");
        }
        if (blockReplaced)
        {
            EmueraLog.Info("terminal", "检测到方块字符被渲染为全角，已自动替换为盲文点阵以保持游戏布局。");
        }
        if (anySymbolHalf || blockReplaced)
        {
            EmueraLog.Info("terminal", "建议：选择字形宽度与终端占位匹配的字体可改善视觉效果。");
            EmueraLog.Info("terminal", "  若占位半角但字形全角（字符重叠），选字形本身为半角的字体（如 Cascadia Mono）。");
            EmueraLog.Info("terminal", "  若补空格后出现线条空缺，选字形本身为全角的字体（如 MS Gothic）。");
            EmueraLog.Info("terminal", "  理想字体：各字符组的字形宽度恰好等于终端的占位列数。");
        }
        // 正常路径不记录（从上面是否有异常行即可推断），避免每次启动的零价值噪音
    }
}
