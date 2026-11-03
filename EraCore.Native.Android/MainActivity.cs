using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Android.Util;

namespace EraCore.Native.Android;

/// <summary>
/// 原生前端宿主 Activity：
/// - 只负责平台生命周期（启动会话 / 停止会话 / 转屏配置变更）。
/// - 画面完全由 <see cref="NativeRoot"/> + <see cref="NativeGameView"/> 自绘，不加载 WebView。
/// </summary>
[Activity(
    Label = "era-core Native",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.FullUser,
    // 转屏 / 分屏 / 显示尺寸变化时不重建 Activity：会话（以及游戏运行状态）跨配置变更存活。
    // ScreenLayout / SmallestScreenSize / Density 缺一不可——缺少任一都会让旋转或 wm size 触发
    // Activity 重建，旧实现会在 OnDestroy 里停掉会话，新 Activity 起来后只剩空白。
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize |
                           ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize |
                           ConfigChanges.UiMode | ConfigChanges.KeyboardHidden |
                           ConfigChanges.Density | ConfigChanges.FontScale |
                           ConfigChanges.LayoutDirection)]
public class MainActivity : AvaloniaMainActivity
{
    private const string LogTag = "EraCoreNative";

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        try
        {
            NativeRuntime.EnsureStarted(ApplicationContext ?? this);
        }
        catch (Exception ex)
        {
            Log.Error(LogTag, "EnsureStarted failed: " + ex);
        }
    }

    protected override void OnDestroy()
    {
        // 仅真正退出（返回键 / 划掉任务）时停会话；配置变更（转屏/尺寸/字体）触发的
        // Activity 重建必须保留游戏循环，否则新 Activity 会拿到一个已停止的会话。
        if (IsFinishing)
        {
            try
            {
                NativeRuntime.Stop();
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, "Stop failed: " + ex.Message);
            }
        }
        base.OnDestroy();
    }
}
