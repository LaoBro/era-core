using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Util;
using Android.Views;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using Avalonia.Android;

namespace EraCore.Native.Android;

/// <summary>
/// 原生前端宿主 Activity：
/// - 负责平台生命周期（初始化设置/字体/会话、退出时停会话、转屏配置变更、返回键导航）。
/// - 画面完全由 <see cref="NativeShell"/> + <see cref="NativeGameView"/> 自绘，不加载 WebView。
/// </summary>
[Activity(
    Label = "era-core Native",
    MainLauncher = true,
    // 单 Activity 实例：Avalonia 的 SingleView 生命周期持有同一个 NativeShell 控件。
    // 标准 launchMode 下 `am start -n` 会在旧实例仍持有（visual parent = 旧 AvaloniaView 的
    // ContentPresenter）时再建一个实例，新 AvaloniaView 重新挂载同一 NativeShell 会抛
    // "already has a visual parent ContentPresenter"。SingleTop 让重入复用已有实例（走 OnNewIntent），
    // 不改变进程重建 / 配置变更时的正常创建流程。
    LaunchMode = LaunchMode.SingleTop,
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

    public static MainActivity? Instance { get; private set; }

    private ActivityResultLauncher? _safFolderLauncher;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Instance = this;
        // 平台初始化（设置 / 内置字体 / 内置示例解压）已在 NativeAndroidApplication.OnCreate 完成，
        // 那里早于 Avalonia 构造 NativeShell，能保证持久化的主题/方向在首帧前生效。
        base.OnCreate(savedInstanceState);

        // 原生 SAF 目录选择注册在 STARTED 之前（AndroidX 要求）。
        _safFolderLauncher = RegisterForActivityResult(
            new SafOpenTreeContract(), new SafFolderCallback(this));

        // API 29 及以下：READ/WRITE_EXTERNAL_STORAGE 运行时权限（API 30+ 走全盘访问开关页，不走这条）。
        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                var storageLauncher = RegisterForActivityResult(
                    new ActivityResultContracts.RequestMultiplePermissions(),
                    new StoragePermissionCallback());
                ExternalStorageAccess.AttachRuntimeLauncher(storageLauncher);
                Log.Info(LogTag, "storage permission: runtime launcher registered");
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, "storage permission: launcher registration failed: " + ex);
            }
        }

        // NativeShell 在 Application 阶段创建时 Activity 还不存在，无法直接落地屏幕方向；
        // Activity 就绪后补一次，保证重启后仍按已保存的方向显示。
        ApplyOrientation(NativeSettings.Current.Orientation);
    }

    /// <summary>由 <see cref="SafFolderPicker"/> 调用：拉起 ACTION_OPEN_DOCUMENT_TREE。</summary>
    internal void LaunchSafFolderPicker()
    {
        var launcher = _safFolderLauncher;
        if (launcher is null)
        {
            Log.Warn(LogTag, "saf picker: launcher not registered");
            return;
        }
        launcher.Launch(null);
    }

    /// <summary>
    /// 从系统「所有文件访问」开关页或目录选择器返回：通知壳刷新库页，
    /// 让刚授予/收回的权限立即反映到原目录条目的可用状态。
    /// </summary>
    protected override void OnResume()
    {
        base.OnResume();
        try
        {
            NativeApp.Shell?.OnResume();
        }
        catch (Exception ex)
        {
            Log.Warn(LogTag, "shell onResume failed: " + ex.Message);
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
                NativeRuntime.StopOnExit();
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, "StopOnExit failed: " + ex.Message);
            }
            Instance = null;
        }
        base.OnDestroy();
    }

    /// <summary>返回键先交给 UI 导航；只有游戏库页才退出应用。</summary>
    public override bool OnKeyDown(Keycode keyCode, KeyEvent? e)
    {
        if (keyCode == Keycode.Back && NativeApp.Shell?.HandleBack() == true)
            return true;
        return base.OnKeyDown(keyCode, e);
    }

    /// <summary>设置页「屏幕方向」落地：跟随系统 / 竖屏 / 横屏。</summary>
    internal static void ApplyOrientation(AppOrientation orientation)
    {
        var activity = Instance;
        if (activity is null)
            return;

        var target = orientation switch
        {
            AppOrientation.Portrait => ScreenOrientation.Portrait,
            AppOrientation.Landscape => ScreenOrientation.Landscape,
            _ => ScreenOrientation.Unspecified,
        };
        if (activity.RequestedOrientation != target)
        {
            Log.Info(LogTag, $"orientation -> {target}");
            activity.RequestedOrientation = target;
        }
    }
}
