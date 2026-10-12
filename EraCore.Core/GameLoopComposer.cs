using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using MinorShift.Emuera.UI;
using MinorShift.Emuera.UI.Game;
using System;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal enum GameLoopResult
{
    Completed,
    Aborted,
}

internal static class GameLoopComposer
{
    internal static async Task<GameLoopResult> RunAsync(
        ConfigData configData,
        ITerminalSetup terminalSetup,
        Func<EmueraConsole, IConsoleUI, ITerminalSetup, AgentProtocolBase?> buildProtocol,
        Func<AgentProtocolBase, Task> runLoop,
        string? dialectProfileId = null,
        IInputHost? inputHost = null)
    {
        using (var scope = GlobalStatic.OpenScope(configData, dialectProfileId))
        {
            // ADR-0031 / spec-v3 M1：输入宿主注入点。缺省为 NullInputHost（无任何输入能力），
            // 保证骨架阶段可观测行为与接入前一致。
            var ui = new HeadlessConsole(inputHost);
            var console = new EmueraConsole(ui, terminalSetup);

            var protocol = buildProtocol(console, ui, terminalSetup);
            if (protocol == null)
                return GameLoopResult.Aborted;

            console.SetAgentBridge(protocol);

            // issue 02：原 Program.LoadFonts() 调用已删除——HeadlessFontCollection.AddFontFile 是空实现 no-op，
            // 删除零行为变化，同时切断 Core→Cli 非法引用（Program 瘦身后不再含 LoadFonts）。
            await console.Initialize();
            await runLoop(protocol);
        }

        return GameLoopResult.Completed;
    }
}
