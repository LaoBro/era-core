using System;
using Android.App;
using Android.Content.Res;
using Android.Util;
using Android.Views;

namespace EraCore.Native.Android;

/// <summary>系统栏安全区（Avalonia 逻辑像素）。</summary>
internal readonly record struct SystemBarInsets(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Android 系统栏安全区（Avalonia 逻辑像素）。
///
/// <para>状态栏已通过 <c>WindowInsetsControllerCompat</c> 隐藏，内容要顶到屏幕最上方，
/// 因此顶部只避让 <b>display cutout</b>（挖孔/刘海），不能继续预留状态栏高度。
/// 最近一次实测：无挖孔设备上 cutout=0，内容真正贴顶；有挖孔设备取 SafeInsetTop。</para>
///
/// <para>顶部刻意不使用 <c>SystemBars()</c> 的 top：状态栏被滑动临时唤出时该值会变，
/// 会让画布 <c>Bounds</c> 抖动。底部导航栏未隐藏，继续按系统栏底部让位。</para>
///
/// <para>注意：只在 Activity 尺寸/方向变化后重算，避免每帧查询 insets。</para>
/// </summary>
internal static class AndroidInsets
{
    private const string LogTag = "EraCoreNative";

    public static SystemBarInsets SystemBars()
    {
        try
        {
            var activity = MainActivity.Instance;
            var insets = activity?.Window?.DecorView?.RootWindowInsets;
            if (insets is not null && activity?.Resources is { } resources)
            {
                var density = Density(resources);
                if (OperatingSystem.IsAndroidVersionAtLeast(30))
                {
                    var bars = insets.GetInsets(WindowInsets.Type.SystemBars());
                    var cutout = insets.GetInsets(WindowInsets.Type.DisplayCutout());
                    // 顶部状态栏已隐藏，只避让挖孔；左右取系统栏与挖孔的较大值（横屏刘海在侧边）。
                    return new SystemBarInsets(
                        Math.Max(bars.Left, cutout.Left) / density,
                        cutout.Top / density,
                        Math.Max(bars.Right, cutout.Right) / density,
                        bars.Bottom / density);
                }
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    var cutout = insets.DisplayCutout;
                    return new SystemBarInsets(
                        Math.Max(insets.SystemWindowInsetLeft, cutout?.SafeInsetLeft ?? 0) / density,
                        (cutout?.SafeInsetTop ?? 0) / density,
                        Math.Max(insets.SystemWindowInsetRight, cutout?.SafeInsetRight ?? 0) / density,
                        insets.SystemWindowInsetBottom / density);
                }
                // API 28 以下没有 display cutout 概念；状态栏隐藏后顶部无需让位。
                return new SystemBarInsets(
                    insets.SystemWindowInsetLeft / density,
                    0,
                    insets.SystemWindowInsetRight / density,
                    insets.SystemWindowInsetBottom / density);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "system bar inset query failed: " + ex.Message);
        }

        // 兜底：状态栏已隐藏，顶部不再预留；底部用导航栏/手势条高度。
        return new SystemBarInsets(
            0,
            0,
            0,
            ResourceDimensionDp("navigation_bar_height"));
    }

    private static double ResourceDimensionDp(string name)
    {
        try
        {
            var resources = Application.Context.Resources;
            if (resources is null)
                return 0;
            var id = resources.GetIdentifier(name, "dimen", "android");
            if (id <= 0)
                return 0;
            return resources.GetDimensionPixelSize(id) / Density(resources);
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private static float Density(Resources resources)
    {
        var density = resources.DisplayMetrics?.Density ?? 1f;
        return density > 0 ? density : 1f;
    }
}
