using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Util;
using AndroidX.Activity.Result;
using AndroidX.Activity.Result.Contract;

namespace EraCore.Native.Android;

/// <summary>
/// 原生 SAF 目录选择：ACTION_OPEN_DOCUMENT_TREE 拿 tree URI，并申请可持久化读权限。
///
/// <para>Avalonia 的 <c>IStorageProvider.OpenFolderPickerAsync</c> 在部分设备上会在
/// 「允许访问」系统确认框阶段一直不返回（ActivityResult 丢失/挂起），导致导入按钮永远禁用。
/// 这里直接走 Android 的 ActivityResult 通道，并对启动/回传/取消都打日志。</para>
/// </summary>
internal static class SafFolderPicker
{
    private const string LogTag = "EraCoreNative";

    private static TaskCompletionSource<global::Android.Net.Uri?>? _pending;

    /// <summary>选择器结果的兜底等待上限：超过视为系统没有回传，避免按钮永久禁用。</summary>
    private static readonly TimeSpan ResultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>拉起系统目录选择器；取消、失败或超时返回 null。</summary>
    public static async Task<global::Android.Net.Uri?> PickAsync()
    {
        var activity = MainActivity.Instance;
        if (activity is null)
        {
            Log.Warn(LogTag, "saf picker: no MainActivity");
            return null;
        }

        if (_pending is not null)
        {
            Log.Warn(LogTag, "saf picker: previous request still pending, ignore new request");
            return null;
        }

        var pending = new TaskCompletionSource<global::Android.Net.Uri?>();
        _pending = pending;
        try
        {
            Log.Info(LogTag, "saf picker: launch ACTION_OPEN_DOCUMENT_TREE");
            activity.LaunchSafFolderPicker();
        }
        catch (Exception ex)
        {
            _pending = null;
            Log.Error(LogTag, "saf picker: launch failed: " + ex);
            return null;
        }

        var winner = await Task.WhenAny(pending.Task, Task.Delay(ResultTimeout)).ConfigureAwait(true);
        if (winner != pending.Task && _pending == pending)
        {
            _pending = null;
            Log.Warn(LogTag, "saf picker: no result after 10 min, give up");
            return null;
        }

        return await pending.Task.ConfigureAwait(true);
    }

    /// <summary>ActivityResult 回调入口（主线程）。</summary>
    public static void Complete(global::Android.Net.Uri? uri)
    {
        var pending = _pending;
        _pending = null;
        if (pending is null)
        {
            Log.Warn(LogTag, "saf picker: result without pending request");
            return;
        }

        Log.Info(LogTag, "saf picker: completed uri=" + (uri?.ToString() ?? "<null>"));
        pending.TrySetResult(uri);
    }
}

/// <summary>
/// 在原生的 OpenDocumentTree 基础上补充读 + 可持久化权限 flag。
/// AndroidX 默认 contract 不带 persistable flag，<c>TakePersistableUriPermission</c> 可能失败。
/// </summary>
internal sealed class SafOpenTreeContract : ActivityResultContract
{
    public override Intent CreateIntent(Context context, Java.Lang.Object? input)
    {
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission
                        | ActivityFlags.GrantWriteUriPermission
                        | ActivityFlags.GrantPersistableUriPermission);
        Log.Info("EraCoreNative", "saf picker: intent flags=" + intent.Flags);
        return intent;
    }

    public override Java.Lang.Object? ParseResult(int resultCode, Intent? intent)
    {
        var uri = resultCode == (int)Result.Ok ? intent?.Data : null;
        Log.Info("EraCoreNative", $"saf picker: parseResult code={resultCode} uri={uri}");
        return uri;
    }
}

/// <summary>ActivityResult 回调：持久化权限后交给 <see cref="SafFolderPicker"/>。</summary>
internal sealed class SafFolderCallback : Java.Lang.Object, IActivityResultCallback
{
    private const string LogTag = "EraCoreNative";

    private readonly MainActivity _activity;

    public SafFolderCallback(MainActivity activity)
    {
        _activity = activity;
    }

    public void OnActivityResult(Java.Lang.Object? result)
    {
        var uri = result as global::Android.Net.Uri;
        if (uri is not null)
        {
            try
            {
                _activity.ContentResolver?.TakePersistableUriPermission(
                    uri, ActivityFlags.GrantReadUriPermission);
                Log.Info(LogTag, "saf picker: persistable read permission granted");
            }
            catch (Exception ex)
            {
                // 部分 provider 不支持持久化，但本次进程内仍可读取；继续导入。
                Log.Warn(LogTag, "saf picker: takePersistableUriPermission failed: " + ex.Message);
            }
        }

        SafFolderPicker.Complete(uri);
    }
}
