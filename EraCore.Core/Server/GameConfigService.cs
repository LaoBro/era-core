using System;
using MinorShift.Emuera.Runtime.Config;

namespace MinorShift.Emuera.Server;

/// <summary>
/// 当前游戏配置持有者（issue 05 热替换重构）——从 <see cref="KestrelGameServer"/> 拆出。
/// 持有可变 <c>_configData</c> 引用，提供配置读取、窗口元信息计算与 /load-game 时的整组替换。
/// </summary>
internal sealed class GameConfigService
{
    private ConfigData _configData;
    private readonly bool _overrideDisplayReport;

    public GameConfigService(ConfigData configData, bool overrideDisplayReport = false)
    {
        _configData = configData ?? throw new ArgumentNullException(nameof(configData));
        _overrideDisplayReport = overrideDisplayReport;
    }

    /// <summary>
    /// 当前 ConfigData。issue 05 起可变——/load-game 重载游戏目录时整体重建并替换。
    /// </summary>
    public ConfigData Current => _configData;

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
        int charWidth = Math.Max(fontSize / 2, 1);
        int shapeShift = Math.Max(2, fontSize / 6);
        int drawableWidth = windowWidth - shapeShift;
        int gameColumns = drawableWidth / charWidth;
        // 一屏可见行数：headless 的 ClientHeight 即 WindowY（HeadlessConsole 构造时快照），
        // 除以 LineHeight 换算——era 游戏的 emuera.config 各自定义，无通用常量。
        int visibleRows = Math.Max(1, windowHeight / lineHeight);
        return new WindowMetrics(windowWidth, fontSize, lineHeight, gameColumns, fontName, visibleRows);
    }

    /// <summary>
    /// 重建 ConfigData 并替换引用（issue 05 /load-game 步骤 5）。
    /// 使用 LoadConfig(exeDir) 显式读取新游戏目录的 emuera.config，避免
    /// ConfigData.configPath（实例 readonly，构造时绑定）绑定到旧目录。
    ///
    /// 注意：本方法不持锁（锁归 <see cref="SessionRegistry"/>）；
    /// ConfigData.SetCurrent（AsyncLocal）与之后的新 Session 构造必须在同一 async 流内
    /// 连续执行（AsyncLocal 经 ExecutionContext 传播给 Task.Run 起的游戏循环，
    /// 供其间接读 Config.Current）——调用方禁止在两者之间插 Task.Run。
    /// </summary>
    public ConfigData Reload(string exeDir)
    {
        var newConfig = new ConfigData();
        // --no-loading-report：在 LoadConfigCore 三源合并前置位，让拦截生效
        //（配置在 LoadConfig(exeDir) 内读取，须先设标志再读）。
        newConfig.OverrideDisplayReport = _overrideDisplayReport;
        newConfig.LoadConfig(exeDir);
        ConfigData.SetCurrent(newConfig);
        _configData = newConfig;
        return newConfig;
    }
}

/// <summary>窗口布局元信息（issue 12），供 GET /state 返回。visibleRows = WindowY / LineHeight（agent 紧凑视图的页高）。</summary>
internal sealed record WindowMetrics(int WindowWidth, int FontSize, int LineHeight, int GameColumns, string FontName, int VisibleRows);
