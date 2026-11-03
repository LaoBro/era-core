using System;
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;
using Android.Util;
using AndroidX.Activity.Result;
using AndroidX.Core.Content;

namespace EraCore.Native.Android;

/// <summary>
/// 「所有文件访问」权限的判定与申请，对齐 MAUI 侧 <c>ExternalStoragePermission</c> 的两条路径：
/// API 30+ 用 <c>MANAGE_EXTERNAL_STORAGE</c> + （必要时）声明在 Manifest 的
/// <c>MANAGE_EXTERNAL_STORAGE</c> 开关页；API 29 及以下退回 <c>READ_EXTERNAL_STORAGE</c> 运行时权限。
///
/// <para>授权失败不是错误：调用方据此回退 SAF 复制模式。</para>
/// </summary>
internal static class ExternalStorageAccess
{
    private const string LogTag = "EraCoreNative";
    private const string ReadPermission = "android.permission.READ_EXTERNAL_STORAGE";

    /// <summary>API 29 及以下需要一并申请的旧存储权限（写权限在 API 29 起已无意义）。</summary>
    internal static readonly string[] LegacyPermissions =
    {
        ReadPermission,
        "android.permission.WRITE_EXTERNAL_STORAGE",
    };

    private static ActivityResultLauncher? _runtimeLauncher;
    private static TaskCompletionSource<bool>? _pending;

    /// <summary>是否已具备真实路径直接读写能力（API 30+ 查全盘访问开关；否则查读权限）。</summary>
    public static bool IsGranted()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            try
            {
                return global::Android.OS.Environment.IsExternalStorageManager;
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, "storage access check failed: " + ex.Message);
                return false;
            }
        }

        return ContextCompat.CheckSelfPermission(
            global::Android.App.Application.Context, ReadPermission) == Permission.Granted;
    }

    /// <summary>由 <see cref="MainActivity"/> 在 <c>OnCreate</c> 注册后注入（API 29 及以下使用）。</summary>
    public static void AttachRuntimeLauncher(ActivityResultLauncher launcher) => _runtimeLauncher = launcher;

    /// <summary>
    /// 拉起授权入口。API 30+ 打开系统「所有文件访问」开关页（无法等待结果，返回后由库页刷新检测）；
    /// API 29 及以下走运行时权限对话框并等待结果。
    /// </summary>
    public static async Task RequestAsync()
    {
        if (IsGranted())
            return;

        if (!OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            if (_runtimeLauncher is null)
            {
                Log.Warn(LogTag, "storage permission: no runtime launcher");
                return;
            }

            var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var previous = _pending;
            _pending = pending;
            previous?.TrySetResult(false);
            Log.Info(LogTag, "storage permission: request runtime READ/WRITE");
            try
            {
                _runtimeLauncher.Launch(LegacyPermissions);
            }
            catch (Exception ex)
            {
                _pending = null;
                Log.Warn(LogTag, "storage permission: launch failed: " + ex.Message);
                pending.TrySetResult(false);
            }

            await pending.Task.ConfigureAwait(true);
            Log.Info(LogTag, $"storage permission: runtime result granted={IsGranted()}");
            return;
        }

        var packageName = global::Android.App.Application.Context.PackageName;
        var applied = false;
        foreach (var action in new[]
                 {
                     Settings.ActionManageAppAllFilesAccessPermission,
                     Settings.ActionManageAllFilesAccessPermission,
                 })
        {
            try
            {
                var intent = new Intent(action);
                if (action == Settings.ActionManageAppAllFilesAccessPermission)
                    intent.SetData(global::Android.Net.Uri.Parse("package:" + packageName));
                intent.AddFlags(ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
                Log.Info(LogTag, $"storage permission: opened {action}");
                applied = true;
                break;
            }
            catch (Exception ex)
            {
                Log.Warn(LogTag, $"storage permission: {action} failed: {ex.Message}");
            }
        }

        if (!applied)
        {
            try
            {
                var intent = new Intent(Settings.ActionApplicationDetailsSettings);
                intent.SetData(global::Android.Net.Uri.Parse("package:" + packageName));
                intent.AddFlags(ActivityFlags.NewTask);
                global::Android.App.Application.Context.StartActivity(intent);
                Log.Info(LogTag, "storage permission: fell back to app details");
            }
            catch (Exception ex)
            {
                Log.Error(LogTag, "storage permission: app details failed: " + ex);
            }
        }
    }

    /// <summary>运行时权限回调入口（主线程）：以实际权限状态为准，不依赖回调里的 map。</summary>
    public static void CompleteRuntimeRequest()
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(IsGranted());
    }
}

/// <summary><c>RequestMultiplePermissions</c> 的回调桥（对齐 MAUI 的 PermissionResultCallback）。</summary>
internal sealed class StoragePermissionCallback : Java.Lang.Object, IActivityResultCallback
{
    public void OnActivityResult(Java.Lang.Object? result) => ExternalStorageAccess.CompleteRuntimeRequest();
}
