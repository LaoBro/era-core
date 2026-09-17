#if ANDROID
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using AndroidX.Activity.Result;
using AndroidX.Core.Content;

namespace EraCore.Maui.Services;

/// <summary>
/// 安卓8（API≤28）legacy 存储的<b>运行时权限</b>请求服务——与 <c>SafGameDirAccessor</c>
/// 同构的 launcher 挂载模式：<c>MainActivity</c> 在
/// <c>OnCreate</c> 注册 <c>ActivityResultContracts.RequestMultiplePermissions</c>
/// 并把结果回调到 <see cref="OnPermissionResult"/>。
/// <para>
/// 为何存在：API≤28 无 <c>MANAGE_EXTERNAL_STORAGE</c> 概念，直接访问 <c>/storage/emulated/0</c>
/// 必须以运行时方式授予 <c>READ_EXTERNAL_STORAGE</c>（写入另需 <c>WRITE_EXTERNAL_STORAGE</c>）。
/// 缺权时 <c>Directory.GetDirectories</c> 静默返回空（不抛），正是"看到盘符、点进去无子目录"的根因。
/// 本服务只在 API≤28 触发；API 30+ 走既有 <c>BridgeHost.HasAllFilesAccess</c> 的 MANAGE 路径。
/// </para>
/// </summary>
internal sealed class ExternalStoragePermission
{
    /// <summary>legacy 全量存储访问所需的运行时权限集合。仅 API≤28 时请求。</summary>
    internal static readonly string[] LegacyPermissions =
    [
        Android.Manifest.Permission.ReadExternalStorage,
        Android.Manifest.Permission.WriteExternalStorage,
    ];

    internal static ExternalStoragePermission? Instance { get; private set; }

    private readonly Context _context;
    private readonly ActivityResultLauncher _launcher;
    private TaskCompletionSource<bool>? _pending;

    public ExternalStoragePermission(Context context, ActivityResultLauncher launcher)
    {
        _context = context;
        _launcher = launcher;
        Instance = this;
    }

    /// <summary>MainActivity 权限结果回调入口——<c>true</c> 当且仅当 READ/WRITE 都授予。</summary>
    public void OnPermissionResult(bool granted)
    {
        var pending = _pending;
        _pending = null;
        pending?.TrySetResult(granted);
    }

    /// <summary>是否已授予 <c>READ_EXTERNAL_STORAGE</c>（legacy 只读再列举的门槛）。</summary>
    public bool HasReadAccess()
        => ContextCompat.CheckSelfPermission(_context, Android.Manifest.Permission.ReadExternalStorage)
           == Permission.Granted;

    /// <summary>
    /// 发起运行时权限请求并等待用户裁决。必须从宿主 Activity 的主线程调用
    /// （launcher.Launch 要求；由调用方用 <c>IDispatcher.DispatchAsync</c> 保证）。
    /// 同一时刻只允许一个等待者；重复调用会覆盖前一个挂起请求。
    /// </summary>
    public Task<bool> RequestAccessAsync(string[] permissions)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        _launcher.Launch(permissions);
        return tcs.Task;
    }
}
#endif