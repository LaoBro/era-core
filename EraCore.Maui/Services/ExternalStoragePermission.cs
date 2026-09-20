#if ANDROID
using System.Threading.Tasks;
using Android.Content;
using Android.Content.PM;
using AndroidX.Activity.Result;
using AndroidX.Core.App;
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

    /// <summary>
    /// 是否已至少发起过一次请求——<c>ShouldShowRequestPermissionRationale</c> 在「**从未请求过**」时
    /// 同样返回 <c>false</c>，只看它无法区分「还没问过」与「已被系统阻断」，必须靠这个标记排除前者。
    /// </summary>
    private bool _requestedBefore;

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
    /// 权限是否已被系统**阻断**——用户选过「拒绝，不再询问」（或 ROM 的同类机制）之后，
    /// <c>Launch</c> 不再弹出任何窗口、立即以 denied 返回。
    /// <para>
    /// 判据三合一，缺一不可：**请求过** + 仍未授予 + 系统不再显示说明弹窗。
    /// 第三条单独看无法区分「从未请求过」，故必须配合 <see cref="_requestedBefore"/>。
    /// </para>
    /// <para>
    /// 仅在 API≤29 有意义：API 30+ 走 MANAGE 设置页，用户随时可自行开启，不存在阻断态。
    /// Activity 走 <c>Platform.CurrentActivity</c> 动态取而非构造时持有——避免配置变更时的引用泄漏。
    /// </para>
    /// </summary>
    public bool IsPermanentlyDenied
    {
        get
        {
            if (!_requestedBefore || HasReadAccess())
                return false;
            var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
            if (activity is null)
                return false;
            return !ActivityCompat.ShouldShowRequestPermissionRationale(
                activity, Android.Manifest.Permission.ReadExternalStorage);
        }
    }

    /// <summary>
    /// 发起运行时权限请求并等待用户裁决。必须从宿主 Activity 的主线程调用
    /// （launcher.Launch 要求；由调用方用 <c>IDispatcher.Dispatch</c> 保证）。
    /// <para>
    /// **单飞**：<see cref="_pending"/> 是单槽，新请求会先以 <c>false</c> 了结在途的旧请求——
    /// 否则被覆盖的那个 <c>Task</c> 永不完成，<c>await</c> 它的调用方（见
    /// <c>BridgeHost.HandleRequestPermission</c>）会永久挂起。
    /// </para>
    /// </summary>
    public Task<bool> RequestAccessAsync(string[] permissions)
    {
        _pending?.TrySetResult(false);
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending = tcs;
        _requestedBefore = true;
        _launcher.Launch(permissions);
        return tcs.Task;
    }
}
#endif