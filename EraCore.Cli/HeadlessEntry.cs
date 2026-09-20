using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

/// <summary>
/// Cli 入口点。拆分自原 Emuera.Headless.Program.Main（issue 01）。
/// <para>
/// 三项目拆分后，Core 是 Library（无 Main），Cli 是 Exe——入口由 Emuera.Headless.Cli.dll 提供。
/// <c>Program</c> 留 Core 仅含路径转发 + 状态字段（50+ 处 Shared/ 调用零改动）。
/// </para>
/// <para>
/// issue 02 已抽取 <see cref="EmueraRuntimeInitializer.Initialize"/> 封装共享运行时初始化
/// （encoding/culture/terminalSetup/Program.ExeName/ConfigData/Config/JSONConfig/Lang），
/// Main 仅保留 <see cref="HeadlessOptions.Parse"/> + <see cref="GamePaths.Resolve"/> +
/// <see cref="GamePaths.Validate"/> 校验失败处置 + runner 分流（<see cref="ServerRunner"/> / <see cref="HeadlessRunner"/>）。
/// MAUI 入口（issue 07 落地）同调 <see cref="EmueraRuntimeInitializer.Initialize"/>。
/// </para>
/// </summary>
internal static class HeadlessEntry
{
    [STAThread]
    internal static async Task Main(string[] args)
    {
        // Console.OutputEncoding 必须在 HeadlessOptions.Parse 之前设置——Parse 失败时
        // Console.Error.WriteLine 输出含中文（如 "server 模式不支持 --protocol 参数"），
        // 默认系统代码页（如 GBK）会乱码。Encoding.RegisterProvider 留 Initialize（ERB 读取需要）。
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var options = HeadlessOptions.Parse(args);
        if (options == null) return;

        var paths = GamePaths.Resolve(options.ExeDir, new FileSystemGameDirAccessor());

        // Initialize 完成所有共享 bootstrap（encoding/culture/ConfigData/Lang 等）但不调 paths.Validate——
        // 这样 Validate 失败时调用方仍持有已加载的 configData + 已启用 ANSI 的 terminalSetup，
        // Server 空闲模式 fallback 复用这两者，避免 "AsyncLocal 指向已加载 config 而本地变量是空 config"
        // 的 split-brain 与 ANSI 丢失回归（重构前行为）。
        var (configData, terminalSetup) = EmueraRuntimeInitializer.Initialize(paths, new FileSystemGameDirAccessor(), options.DisplayReportOverride);

        // T-025 D1/D2：GamePaths.Validate 失败按模式分流——
        // - server 模式：降级 warn 继续（空闲启动，浏览器可打开选择器，真正的加载推迟到 /load-game）
        // - CLI 模式：打印提示 + 等待玩家按回车再退出（不再静默 Environment.Exit，避免双击时窗口一闪即关）
        //
        // CLI 等回车在非交互终端的边界（D17）：stdin 重定向到 /dev/null 或文件时 Console.ReadLine
        // 立即返 null/EOF → 进程退出，等同原 Environment.Exit(1)；CI/CD 不受影响。stdin 完全无句柄
        // 时 ReadLine 可能抛 InvalidOperationException——catch 兜底等同 EOF 退出。仅 stdin 是管道且
        // 管道不关闭（如 `echo | exe`）才会挂起，此场景罕见，可接受。
        try
        {
            paths.Validate();
        }
        catch (GamePathValidationException ex)
        {
            EmueraLog.Error("error", $"{ex.Code}: {ex.Message}");
            if (options.Server)
            {
                // D1：server 模式空闲启动——降级 warn 继续，真正的游戏加载推迟到 /load-game。
                // 复用 Initialize 返回的 configData + terminalSetup（已加载 config + 已启用 ANSI），
                // 与重构前行为一致；ConfigData.Current AsyncLocal 也指向同一 configData，无 split-brain。
                EmueraLog.Error("server", "游戏目录校验失败，进入空闲模式。请在浏览器中选择游戏目录。");
            }
            else
            {
                // D2/D17：CLI 模式——打印提示后等回车再退出
                EmueraLog.Error("headless", "按回车键退出...");
                try
                {
                    Console.ReadLine();
                }
                catch (InvalidOperationException)
                {
                    // stdin 完全无句柄（CI 中非重定向而是无 stdin）——等同 EOF 退出
                }
                return;
            }
        }

        // 字体加载迁移至 runners 内 scope 打开后执行（ADR-0008：Pfc 是实例成员，随 scope 生灭）
        if (options.Server)
        {
#if !ANDROID_NO_SERVER
            // --open-browser 便捷模式且未显式 --port → 传 0，由 ServerRunner 自动挑空闲端口。
            var port = options.OpenBrowser && !options.PortExplicit ? 0 : options.Port;
            await ServerRunner.RunAsync(port, terminalSetup, configData, options.DisplayReportOverride, options.OpenBrowser, BuildAppSettings(options));
#else
            // android 交叉产物（3.2 验证）：无 Kestrel（AspNetCore 无 android runtime pack），
            // server 模式不可用——明确提示后走 CLI 模式，避免静默忽略 --server。
            EmueraLog.Error("server", "server 模式在 Android 上不可用（无 Kestrel），已忽略 --server 参数");
            await HeadlessRunner.RunAsync(paths, options.Protocol, options.TermWidthHint, terminalSetup, configData);
#endif
        }
        else
        {
            // ITerminalInput 在 HeadlessRunner 内创建：CLI 模式专属，stdin 重定向
            // 等环境错误由 HeadlessRunner 的 HeadlessFatalException 捕获块统一处理。
            await HeadlessRunner.RunAsync(paths, options.Protocol, options.TermWidthHint, terminalSetup, configData);
        }
    }

    /// <summary>
    /// ADR-0030：构造 headless 全局设置（内存 store）——<c>--setting</c> 种子。
    /// 非法 key/value 仅警告并忽略（不中断启动）；<c>--loading-report/--no-loading-report</c>
    /// 由 <see cref="GameConfigService"/> 构造时折叠进同一 store（--setting 优先）。
    /// </summary>
    private static AppSettings BuildAppSettings(HeadlessOptions options)
    {
        var appSettings = new AppSettings(new InMemoryAppSettingsStore());
        foreach (var entry in options.Settings)
        {
            var eq = entry.IndexOf('=');
            var key = eq > 0 ? entry[..eq] : entry;
            var value = eq >= 0 ? entry[(eq + 1)..] : null;
            if (!appSettings.Set(key, value))
                EmueraLog.Warn("options", $"--setting 忽略非法项: {entry}（key 或 value 不在 canonical 词表）");
            else
                EmueraLog.Info("options", $"--setting {entry}");
        }
        return appSettings;
    }
}
