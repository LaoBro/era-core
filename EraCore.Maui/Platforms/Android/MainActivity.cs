using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;
using AndroidX.Core.View;
using EraCore.Maui.Services;
using MinorShift.Emuera;

namespace EraCore.Maui;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ScreenOrientation = ScreenOrientation.FullSensor, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private SafGameDirAccessor? _safAccessor;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // 沉浸式全屏：内容延伸到状态栏/导航栏区域（配合隐藏系统栏，见 HideSystemBars）。
        // 之前用 ApplyAndroidStatusBarPadding 给 WebView 加 margin 让位——沉浸式后不再需要。
        WindowCompat.SetDecorFitsSystemWindows(Window!, false);

        try
        {
            var launcher = RegisterForActivityResult(
                new ActivityResultContracts.OpenDocumentTree(),
                new SafResultCallback(this));

            _safAccessor = new SafGameDirAccessor(Android.App.Application.Context, launcher);
            Android.Util.Log.Info("EmueraMaui", "MainActivity: SafGameDirAccessor initialized");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("EmueraMaui", $"MainActivity.OnCreate SAF init failed: {ex}");
        }

        // issues/15 修复（安卓8 legacy 存储）：注册 READ/WRITE_EXTERNAL_STORAGE 运行时请求。
        // 结果回调复用 SafResultCallback 的判类型分发；API 30+ 不走这条链。
        try
        {
            var permissionLauncher = RegisterForActivityResult(
                new ActivityResultContracts.RequestMultiplePermissions(),
                new PermissionResultCallback());
            _ = new ExternalStoragePermission(Android.App.Application.Context, permissionLauncher);
            Android.Util.Log.Info("EmueraMaui", "MainActivity: ExternalStoragePermission initialized");
        }
        catch (Exception ex)
        {
            Android.Util.Log.Error("EmueraMaui", $"MainActivity.OnCreate permission init failed: {ex}");
        }
    }

    /// <summary>
    /// 窗口重新获得焦点时隐藏系统栏（沉浸式 sticky）——获得焦点是重新隐藏栏的最可靠时机。
    /// 玩家从屏幕边缘下滑可临时唤出状态栏/导航栏，几秒后自动再隐藏。
    /// </summary>
    public override void OnWindowFocusChanged(bool hasFocus)
    {
        base.OnWindowFocusChanged(hasFocus);
        if (hasFocus)
            HideSystemBars();
    }

    private void HideSystemBars()
    {
        if (Window == null || Window!.DecorView == null)
            return;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
            {
                var controller = WindowCompat.GetInsetsController(Window!, Window!.DecorView!);
                if (controller != null)
                {
                    controller.Hide(WindowInsetsCompat.Type.SystemBars());
                    controller.SystemBarsBehavior = WindowInsetsControllerCompat.BehaviorShowTransientBarsBySwipe;
                }
            }
            else
            {
                Window!.DecorView!.SystemUiFlags = SystemUiFlags.Fullscreen | SystemUiFlags.HideNavigation | SystemUiFlags.ImmersiveSticky;
            }
        }
        catch (Exception ex)
        {
            Android.Util.Log.Warn("EmueraMaui", $"HideSystemBars failed: {ex.Message}");
        }
    }

    private sealed class SafResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        private readonly MainActivity _activity;
        public SafResultCallback(MainActivity activity) => _activity = activity;
        public void OnActivityResult(Java.Lang.Object? result)
        {
            var uri = result as Android.Net.Uri;
            _activity._safAccessor?.OnTreeResult(uri);
        }
    }

    /// <summary>
    /// roles/15 修复：<c>RequestMultiplePermissions</c> 结果回调——READ 授予即视为达成
    /// （写入由存储根浏览入口在请求时一并携带 <see cref="ExternalStoragePermission.LegacyPermissions"/>）。
    /// READ/WRITE 任一 pending 被拒都会到 <see cref="ExternalStoragePermission.OnPermissionResult"/>（false）。
    /// </summary>
    private sealed class PermissionResultCallback : Java.Lang.Object, IActivityResultCallback
    {
        public void OnActivityResult(Java.Lang.Object? result)
        {
            bool granted = false;
            if (result is System.Collections.IDictionary dict)
            {
                foreach (var key in dict.Keys)
                {
                    var perm = key as string;
                    var value = dict[key];
                    if (perm == Android.Manifest.Permission.ReadExternalStorage
                        && value is Permission p
                        && p == Android.Content.PM.Permission.Granted)
                    {
                        granted = true;
                        break;
                    }
                }
            }
            ExternalStoragePermission.Instance?.OnPermissionResult(granted);
        }
    }
}
