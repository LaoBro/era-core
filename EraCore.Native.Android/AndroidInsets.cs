using System;
using Android.App;
using Android.Content.Res;
using Android.Util;
using Android.Views;

namespace EraCore.Native.Android;

/// <summary>系统栏安全区（Avalonia 逻辑像素）。</summary>
internal readonly record struct SystemBarInsets(double Left, double Top, double Right, double Bottom);

/// <summary>
/// Android 系统栏安全区（Avalonia 在 Android 上边到边绘制）。
///
/// <para>顶部状态栏、底部手势条/导航栏，以及横屏时可能在右侧的导航栏都要让位。
/// 与 IME inset 合并时底部取 <c>max</c>（键盘弹出时系统栏通常已被遮挡，相加会重复留白）。</para>
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
                    return new SystemBarInsets(
                        bars.Left / density,
                        bars.Top / density,
                        bars.Right / density,
                        bars.Bottom / density);
                }
                if (OperatingSystem.IsAndroidVersionAtLeast(28))
                {
                    return new SystemBarInsets(
                        insets.StableInsetLeft / density,
                        insets.StableInsetTop / density,
                        insets.StableInsetRight / density,
                        insets.StableInsetBottom / density);
                }
                return new SystemBarInsets(
                    insets.SystemWindowInsetLeft / density,
                    insets.SystemWindowInsetTop / density,
                    insets.SystemWindowInsetRight / density,
                    insets.SystemWindowInsetBottom / density);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "system bar inset query failed: " + ex.Message);
        }

        // 兜底：资源里的状态栏 / 导航栏高度（手势导航下通常是手势条高度）。
        return new SystemBarInsets(
            0,
            ResourceDimensionDp("status_bar_height"),
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
