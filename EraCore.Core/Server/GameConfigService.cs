using System;
using MinorShift.Emuera.Primitives;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 当前游戏配置持有者（issue 05 热替换重构）——从 <see cref="KestrelGameServer"/> 拆出。
/// 持有可变 <c>_configData</c> 引用，提供配置读取、窗口元信息计算与 /load-game 时的整组替换。
/// </summary>
internal sealed class GameConfigService
{
    private ConfigData _configData;
    private readonly AppSettings _appSettings;

    /// <param name="appSettings">全局设置服务（ADR-0030）——<c>null</c> 时用空内存 store（测试/占位路径）。</param>
    public GameConfigService(ConfigData configData, bool? displayReportOverride = null, AppSettings? appSettings = null)
    {
        _configData = configData ?? throw new ArgumentNullException(nameof(configData));
        _appSettings = appSettings ?? new AppSettings();
        // --loading-report/--no-loading-report 兼容（ADR-0030）：启动参数作 displayReportOverride 种子——
        // 仅当 store 未显式设置时写入，--setting displayReportOverride 优先。
        if (displayReportOverride is { } v && _appSettings.Get(AppSettings.DisplayReportOverrideKey) == "auto")
            _appSettings.Set(AppSettings.DisplayReportOverrideKey, v ? "on" : "off");
    }

    /// <summary>
    /// 当前 ConfigData。issue 05 起可变——/load-game 重载游戏目录时整体重建并替换。
    /// </summary>
    public ConfigData Current => _configData;

    /// <summary>全局设置服务（ADR-0030）——/settings 端点与 Reload 应用点共用。</summary>
    public AppSettings AppSettings => _appSettings;

    public T GetValue<T>(ConfigCode code) => _configData.GetConfigValue<T>(code);

    /// <summary>
    /// 窗口布局元信息（issue 12）——前端以 CSS ch 单位设置容器宽度。
    /// gameColumns = DrawableWidth / (FontSize/2)，与 CLI 模式
    /// TerminalLineFormatter.GetGameColumnWidth() 一致——浏览器 monospace≈0.6em 与
    /// GDI ASCII=FontSize/2≈0.5em 的差异需要按字符列数而非像素布局。
    /// Headless 模式 TextDrawingMode != WINAPI，故 ShapePositionShift = Max(2, FontSize/6)。
    /// </summary>
    public WindowMetrics GetWindowMetrics()
    {
        var windowWidth = _configData.GetConfigValue<int>(ConfigCode.WindowX);
        var windowHeight = _configData.GetConfigValue<int>(ConfigCode.WindowY);
        var fontSize = _configData.GetConfigValue<int>(ConfigCode.FontSize);
        var lineHeight = _configData.GetConfigValue<int>(ConfigCode.LineHeight);
        var fontName = _configData.GetConfigValue<string>(ConfigCode.FontName);
        // 默认文字色（文字色）——turn 协议只在「与默认色不同」的 segment 上带 color
        // （ConsolePrintManager.BuildPrintOpsForLine 的 ColorChanged 判断），故渲染端必须
        // 拿到默认色才能画对普通文字；CLI 靠 ANSI 首段显式发一次 Config.ForeColor 免疫此问题。
        var foreColor = _configData.GetConfigValue<EmuColor>(ConfigCode.ForeColor);
        int charWidth = Math.Max(fontSize / 2, 1);
        int shapeShift = Math.Max(2, fontSize / 6);
        int drawableWidth = windowWidth - shapeShift;
        int gameColumns = drawableWidth / charWidth;
        // 一屏可见行数：headless 的 ClientHeight 即 WindowY（HeadlessConsole 构造时快照），
        // 除以 LineHeight 换算——era 游戏的 emuera.config 各自定义，无通用常量。
        int visibleRows = Math.Max(1, windowHeight / lineHeight);
        return new WindowMetrics(windowWidth, fontSize, lineHeight, gameColumns, fontName, visibleRows, foreColor.ToHex());
    }

    /// <summary>
    /// 重建 ConfigData 并替换引用（issue 05 /load-game 步骤 5）。
    /// 使用 LoadConfig(exeDir) 显式读取新游戏目录的 emuera.config，避免
    /// ConfigData.configPath（实例 readonly，构造时绑定）绑定到旧目录。
    ///
    /// 注意：本方法不持锁（锁归 <see cref="SessionRegistry"/>）；
    /// ConfigData.SetCurrent（AsyncLocal）与之后的新 CoreSession 构造必须在同一 async 流内
    /// 连续执行（AsyncLocal 经 ExecutionContext 传播给 Task.Run 起的游戏循环，
    /// 供其间接读 Config.Current）——调用方禁止在两者之间插 Task.Run。
    /// </summary>
    public ConfigData Reload(string exeDir)
    {
        var newConfig = new ConfigData();
        newConfig.LoadConfig(exeDir);
        ConfigData.SetCurrent(newConfig);
        _configData = newConfig;
        // ADR-0030：加载后应用全局设置（perf 覆盖/诊断旋钮/displayReportOverride/agentLog）——
        // 压过三源合并结果；旧 --loading-report 路径已折叠进 store 种子，由 ApplyTo 统一生效。
        _appSettings.ApplyTo(newConfig);
        return newConfig;
    }
}

/// <summary>窗口布局元信息（issue 12），供 GET /state 返回。visibleRows = WindowY / LineHeight（agent 紧凑视图的页高）。
/// foreColor = ConfigCode.ForeColor（"#RRGGBB"）——turn 协议只带「非默认色」的 segment 颜色，
/// 渲染端（Web/MAUI）需靠此值画普通文字，否则落回主题默认色。</summary>
internal sealed record WindowMetrics(int WindowWidth, int FontSize, int LineHeight, int GameColumns, string FontName, int VisibleRows, string ForeColor);
