using MinorShift.Emuera.Files;
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using System.Threading;
using MinorShift.Emuera.GameView;
using MinorShift.Emuera.Runtime.Config;
using MinorShift.Emuera.Runtime.Utils;

namespace MinorShift.Emuera.Compat;

/// <summary>
/// legacy emuera.em 已编译插件（引用程序集 <c>Emuera</c>）的加载兼容层——单一、可整体删除的模块。
/// <para>
/// 职责三件事：
/// ① 解析插件自带的兄弟依赖（<see cref="Prepare"/>）；
/// ② 平台能力判定（<see cref="CanLoadPluginsHere"/>）；
/// ③ 安全门禁策略（<see cref="IsAwareGateEnforced"/>）。
/// </para>
/// <para>
/// 删除兼容时：删本文件 + <c>EraCore.Compat.Emuera</c> 项目 + <c>PluginManager.LoadPlugins</c> 中的
/// 调用点 + 配置项，即可完全摘除，不影响未来重设计的 C# 脚本系统。
/// </para>
/// </summary>
internal static class LegacyPluginCompat
{
    private static int _resolverInstalled;
    private static string? _pluginsDir;
    private static IGameDirAccessor? _dirAccessor;

    /// <summary>
    /// 幂等安装 <see cref="AssemblyLoadContext.Default.Resolving"/> 处理器，并记住插件目录与其访问器。
    /// <para>
    /// <b>不变量 F-1</b>：依赖探测的存在性判定原为裸 <c>File.Exists</c>；已改走
    /// <paramref name="dirAccessor"/> ——该项已收敛。
    /// 注意此处<b>不</b>放宽 <see cref="CanLoadPluginsHere"/>：<c>Assembly.LoadFrom</c> 仍需真实路径，
    /// content URI 下本resolver 只是恒解析不到（既有权衡，未变）。
    /// </para>
    /// </summary>
    internal static void Prepare(string pluginsDir, IGameDirAccessor dirAccessor)
    {
        _pluginsDir = pluginsDir;
        _dirAccessor = dirAccessor;
        if (Interlocked.Exchange(ref _resolverInstalled, 1) == 0)
            AssemblyLoadContext.Default.Resolving += ResolveFromPluginsDir;
    }

    private static Assembly? ResolveFromPluginsDir(AssemblyLoadContext context, AssemblyName name)
    {
        var dir = _pluginsDir;
        if (string.IsNullOrEmpty(dir) || string.IsNullOrEmpty(name.Name))
            return null;
        try
        {
            var accessor = _dirAccessor;
            if (accessor == null)
                return null; // 未 Prepare（尚未加载任何插件）：无目录亦无访问器，无依赖可解析
            var candidate = accessor.CombinePath(dir, name.Name + ".dll");
            if (!accessor.FileExists(candidate))
                return null;
            return context.LoadFromAssemblyPath(candidate);
        }
        catch (Exception ex)
        {
            EmueraLog.Warn("Plugin", $"解析插件依赖 {name.Name} 失败：{ex.Message}");
        }
        return null;
    }

    /// <summary>
    /// 当前运行形态是否允许加载 DLL 插件。需要同时满足：
    /// <list type="bullet">
    /// <item>真实文件路径（非 SAF <c>content://</c> URI）——<c>Assembly.LoadFrom</c> 无法从内容 URI 加载；</item>
    /// <item>运行时支持动态代码（JIT/解释器）——NativeAOT 等纯 AOT 形态为 <c>false</c>，动态 IL 无法执行。</item>
    /// </list>
    /// 桌面恒为 true；Android 仅在「所有文件访问」真实路径 + 非 NativeAOT 构建下为 true。
    /// </summary>
    internal static bool CanLoadPluginsHere()
        => !SafPath.IsContentUri(Program.ExeDir) && RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>无法加载时的原因文案（供 <c>ExeEE</c> 提示）。</summary>
    internal static string UnsupportedReason =>
        SafPath.IsContentUri(Program.ExeDir)
            ? "Android/SAF does not support loading game DLL plugins. Remove the Plugins/*.dll files or run this game from a real directory / on desktop."
            : "This runtime does not support loading game DLL plugins (no dynamic code support, e.g. NativeAOT). Remove the Plugins/*.dll files or use a JIT-capable build.";

    /// <summary>
    /// 安全门禁（<c>pluginsAware.txt</c>）是否强制。默认 false = 兼容优先：
    /// eraTW 等社区游戏不自带 pluginsAware.txt，其可运行的 Skia 引擎亦无此门禁。
    /// 开启后行为回到原版严格模式（发现带 PluginManifest 的 DLL 且无该文件即抛 <c>ExeEE</c>）。
    /// </summary>
    internal static bool IsAwareGateEnforced => Config.PluginsAwareRequired;
}