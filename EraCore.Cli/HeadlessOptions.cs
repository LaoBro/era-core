using MinorShift.Emuera.GameView;
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Parsing;

namespace MinorShift.Emuera;

internal sealed class HeadlessOptions
{
    public string? ExeDir { get; }
    public string Protocol { get; }
    public bool Server { get; }
    public int Port { get; }
    public string TermWidthHint { get; }
    public bool NoLoadingReport { get; }
    public bool LoadingReport { get; }
    public bool OpenBrowser { get; }
    /// <summary>用户是否显式传了 --port——open-browser 便捷模式下未显式指定时自动挑空闲端口。</summary>
    public bool PortExplicit { get; }
    /// <summary>ADR-0030：--setting key=value 列表（仅 --server 模式生效；headless 内存 store 种子，不持久化）。</summary>
    public IReadOnlyList<string> Settings { get; }

    private HeadlessOptions(string? exeDir, string protocol, bool server, int port, string termWidthHint, bool noLoadingReport, bool loadingReport, bool openBrowser, bool portExplicit, IReadOnlyList<string> settings)
    {
        ExeDir = exeDir;
        Protocol = protocol;
        Server = server;
        Port = port;
        TermWidthHint = termWidthHint;
        NoLoadingReport = noLoadingReport;
        LoadingReport = loadingReport;
        OpenBrowser = openBrowser;
        PortExplicit = portExplicit;
        Settings = settings;
    }

    /// <summary>DisplayReport 三态覆盖：null=随游戏配置；false=--no-loading-report 强制关；true=--loading-report 强制开。</summary>
    public bool? DisplayReportOverride => LoadingReport ? true : NoLoadingReport ? false : null;

    // 2.0 GA API：构造器直接接收 name + 别名，描述与默认值通过初始化器设置
    internal static readonly Option<string> ExeDirOption = new(
        "--ExeDir", "-exedir", "-EXEDIR")
    {
        Description = "游戏目录（包含 CSV/ERB 等子目录）"
    };

    internal static readonly Option<string> ProtocolOption = new(
        "--protocol", "-protocol", "-PROTOCOL")
    {
        Description = "协议模式：auto(默认,检测终端), cli。stdin 管道模式已废弃（T-024），脚本/自动化请使用 --server",
        DefaultValueFactory = _ => "auto"
    };

    internal static readonly Option<bool> ServerOption = new(
        "--server", "-server", "-SERVER")
    {
        Description = "服务器模式：通过 HTTP 接口提供单会话服务"
    };

    internal static readonly Option<int> PortOption = new(
        "--port", "-port", "-PORT")
    {
        Description = "服务器监听端口（默认 8080）",
        DefaultValueFactory = _ => 8080
    };

    internal static readonly Option<string> TermWidthHintOption = new(
        "--term-width-hint", "-term-width-hint", "-TERM-WIDTH-HINT")
    {
        Description = "字符宽度提示：auto(默认,探测), cjk(全角), latin(半角)",
        DefaultValueFactory = _ => "auto"
    };

    internal static readonly Option<bool> NoLoadingReportOption = new(
        "--no-loading-report")
    {
        Description = "覆盖游戏配置的加载时显示报告（DisplayReport）为 off，隐藏启动读取日志（agent 用）"
    };

    internal static readonly Option<bool> LoadingReportOption = new(
        "--loading-report")
    {
        Description = "覆盖游戏配置的加载时显示报告（DisplayReport）为 on，输出逐文件报告与 time.log 阶段计时（性能测量用）"
    };

    internal static readonly Option<bool> OpenBrowserOption = new(
        "--open-browser")
    {
        Description = "server 启动后自动打开默认浏览器（配合 --server 使用；未显式 --port 时自动挑空闲端口）"
    };

    internal static readonly Option<string[]> SettingOption = new("--setting")
    {
        Description = "全局设置（ADR-0030）：key=value，可重复，仅 --server 模式生效（进程内存种子，不持久化）。" +
                      "键：displayReportOverride/agentLogEnabled/sweepGatePolicy/safReadDegree/maxParallelErbFiles/" +
                      "dimLockPolicy/lazyFunctionLoad/irDiskCacheLoad/irFirstDirList/irFileListCache/srcContentCache/" +
                      "parallelErbLoad/parallelParseScript；值域：auto/off/on、auto/forceon/forceoff、auto/1-64、auto/4/8/16、auto/nolock、true/false"
    };

    public static HeadlessOptions? Parse(string[] args)
    {
        var rootCommand = new RootCommand("EraCore - Emuera 无头模式运行器");
        rootCommand.Options.Add(ExeDirOption);
        rootCommand.Options.Add(ProtocolOption);
        rootCommand.Options.Add(ServerOption);
        rootCommand.Options.Add(PortOption);
        rootCommand.Options.Add(TermWidthHintOption);
        rootCommand.Options.Add(NoLoadingReportOption);
        rootCommand.Options.Add(LoadingReportOption);
        rootCommand.Options.Add(OpenBrowserOption);
        rootCommand.Options.Add(SettingOption);

        var result = rootCommand.Parse(args);

        // --help / -h / -? / --version：交给内置 Invoke() 输出后退出
        if (Array.IndexOf(args, "--help") >= 0 ||
            Array.IndexOf(args, "-h") >= 0 ||
            Array.IndexOf(args, "-?") >= 0 ||
            Array.IndexOf(args, "--version") >= 0)
        {
            result.Invoke();
            return null;
        }

        // 解析错误：输出错误信息后退出
        if (result.Errors.Count > 0)
        {
            foreach (var err in result.Errors)
                EmueraLog.Error("options", err.Message);
            return null;
        }

        var exeDir = result.GetValue(ExeDirOption);
        var protocol = result.GetValue(ProtocolOption) ?? "auto";
        var server = result.GetValue(ServerOption);
        var port = result.GetValue(PortOption);
        var termWidthHint = result.GetValue(TermWidthHintOption) ?? "auto";
        var noLoadingReport = result.GetValue(NoLoadingReportOption);
        var loadingReport = result.GetValue(LoadingReportOption);
        var openBrowser = result.GetValue(OpenBrowserOption);
        var settings = result.GetValue(SettingOption) ?? Array.Empty<string>();

        // 互斥：强制开/关同时给出时无意义，直接报错退出（对齐解析错误的既有处理）。
        if (noLoadingReport && loadingReport)
        {
            EmueraLog.Error("options", "--no-loading-report 与 --loading-report 互斥，只能传其中一个");
            return null;
        }
        var portExplicit = ArgsContainPort(args);

        if (server && ArgsContainProtocol(args))
        {
            EmueraLog.Error("server", "server 模式不支持 --protocol 参数");
            Environment.Exit(1);
            return null;
        }

        // --setting 仅 server 模式生效（CLI 交互模式不走 GameConfigService.Reload，设置无从应用）。
        if (!server && settings.Length > 0)
        {
            EmueraLog.Warn("options", "--setting 仅 --server 模式生效，CLI 模式下已忽略");
            settings = Array.Empty<string>();
        }

        return new HeadlessOptions(exeDir, protocol, server, port, termWidthHint, noLoadingReport, loadingReport, openBrowser, portExplicit, settings);
    }

    private static bool ArgsContainPort(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--port", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-port", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-PORT", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool ArgsContainProtocol(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.Equals("--protocol", StringComparison.OrdinalIgnoreCase) ||
                arg.Equals("-protocol", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
