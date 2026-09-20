#if !ANDROID_NO_SERVER
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Utils;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Server;
using MinorShift.Emuera.Terminal.Platform;
using System;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace MinorShift.Emuera;

internal static class ServerRunner
{
    /// <summary>
    /// 运行 Kestrel server。port==0 时自动挑选空闲端口（--open-browser 便捷模式下
    /// 用户未显式 --port 时由 HeadlessEntry 传 0）。
    /// <paramref name="openBrowser"/> 为 true 时监听成功后自动打开默认浏览器。
    /// </summary>
    /// <param name="appSettings">全局设置服务（ADR-0030）——headless 内存 store，种子 = --setting；null 时空 store。</param>
    public static async Task RunAsync(int port, ITerminalSetup terminalSetup, ConfigData configData, bool? displayReportOverride = null, bool openBrowser = false, AppSettings? appSettings = null)
    {
        // Server 模式无交互画面，终端即其日志——把终端阈值提到 Info，
        // 让启动横幅/端口提示等用户可见输出正常显示（CLI 交互保持默认 Warn）。
        EmueraLog.SetTerminalLevel(EmueraLog.Level.Info);

        if (port == 0)
        {
            port = FindFreePort();
            EmueraLog.Info("server", $"端口未指定，自动选择空闲端口: {port}");
        }
        EmueraLog.Info("server", $"Emuera {AssemblyData.EmueraVersionText} 服务器模式启动");
        EmueraLog.Info("server", $"监听端口: {port}");

        using var server = new KestrelGameServer(port, terminalSetup, configData, displayReportOverride, appSettings);
        await server.StartAsync();

        if (openBrowser)
        {
            var url = $"http://localhost:{port}";
            EmueraLog.Info("server", $"自动打开浏览器: {url}");
            OpenBrowser(url);
        }

        await server.WaitForShutdownAsync();
    }

    /// <summary>挑选空闲端口（port==0 时）：临时占一个 loopback 端口后释放，交给 Kestrel 绑定。</summary>
    private static int FindFreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    /// <summary>用系统默认浏览器打开 URL——失败不致命，提示手动访问。</summary>
    private static void OpenBrowser(string url)
    {
        try
        {
            using var p = new Process();
            p.StartInfo.FileName = url;
            p.StartInfo.UseShellExecute = true;
            p.Start();
        }
        catch (Exception ex)
        {
            EmueraLog.Warn("server", $"打开浏览器失败: {ex.Message}（可手动访问 {url}）");
        }
    }
}
#endif
