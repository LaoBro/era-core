using System.Threading.Tasks;
using Microsoft.Maui.Controls;

namespace EraCore.Maui.JsBridge;

/// <summary>
/// 游戏资源虚拟主机（issue 05，spec Q3 路线 B）——Windows 与安卓共用同一域名，
/// 前端 <c>resolveResource</c> 的 MAUI 分支统一返回 <c>https://game.local/{path}</c>，
/// 平台实现各自拦截：
/// <list type="bullet">
///   <item>Windows：<c>WindowsJsBridge</c> 用 <c>WebResourceRequested</c> 拦截手动构造响应</item>
///   <item>Android：<c>AndroidJsBridge</c> 用 <c>WebViewAssetLoader</c> PathHandler</item>
/// </list>
/// 两平台共用 <see cref="MinorShift.Emuera.Assets.AssetChannel"/>（消毒/白名单/读字节/MIME 单点）。
/// </summary>
internal static class GameAssetConstants
{
	internal const string VirtualHostName = "game.local";
}

/// <summary>
/// C# ↔ JS 桥接抽象层——issue 06 / spec ID5。
/// 屏蔽 Windows CoreWebView2 与 Android Webkit WebView 的平台差异，让 <c>BridgeHost</c> 不平台分叉。
/// </summary>
/// <remarks>
/// 五个成员：
/// <list type="bullet">
///   <item><see cref="PostTurn"/>：C# → JS，调 <c>window.__onTurn(turnJson)</c>。turnJson 是合法 JSON 字符串，
///       作为 JS 字面量传函数参数（无需再 JSON.stringify）。</item>
///   <item><see cref="PostMessage"/>：C# → JS，调 <c>window.__onMessage(msgJson)</c>——
///       非 turn 消息（如 <c>folderPicked</c> 事件）经此通道，与 turn 分流避免污染 applyTurn 协议消费链路。
///       issue 09 文件选择器引入。</item>
///   <item><see cref="InputReceived"/>：JS → C#，Vue 端 <c>postMessage(json)</c> 触发，C# 侧 <c>MauiBridgeIO.EnqueueInput</c>。</item>
///   <item><see cref="Attach"/>：挂载到 MAUI <see cref="WebView"/>，订阅平台原生消息事件。
///       在 <c>WebView.HandlerChanged</c> 事件内调（拿平台原生视图的最早可靠时机）。
///       返回 <see cref="Task"/> 让调用方 await 平台初始化完成后再设 URL——
///       Windows <c>SetVirtualHostNameToFolderMapping</c> 必须在导航开始前完成（issue 07）。</item>
///   <item><see cref="PickFolderAsync"/>：弹出原生文件夹选择器，返回所选路径（用户取消返 null）。
///       issue 09 文件选择器——Vue 端 <c>pickGameFolder()</c> → C# <c>BridgeHost.HandlePickFolder</c> → 此方法。
///       game-library spec ID11 后仅 Windows 重写此方法；Android 改走 <c>listDirectories</c> + Vue 弹窗，
///       接口提供默认实现返 <c>null</c> 兜底。</item>
/// </list>
/// iOS / MacCatalyst 不在 Phase 1 范围（spec Out of Scope）。
/// </remarks>
internal interface IJsBridge
{
	/// <summary>
	/// C# → JS：向 WebView 投递一个 turn JSON。
	/// 内部调 <c>EvaluateJavaScriptAsync($"window.__onTurn({turnJson})")</c>。
	/// </summary>
	/// <param name="turnJson">合法 JSON 字符串（来自 <c>TurnRecord</c> 序列化）。作为 JS 字面量传入函数参数。</param>
	void PostTurn(string turnJson);

	/// <summary>
	/// C# → JS：向 WebView 投递一个非 turn 消息（如 <c>{"type":"folderPicked","path":...}</c>）。
	/// </summary>
	/// <remarks>
	/// issue 09 文件选择器引入——turn 与非 turn 消息分流：
	/// <list type="bullet">
	///   <item>turn（每帧渲染）：经 <see cref="PostTurn"/> → Vue <c>applyTurn</c> 协议消费链路</item>
	///   <item>非 turn 事件（如 folderPicked）：经 <see cref="PostMessage"/> → Vue <c>registerMessageHandler</c> 分发</item>
	/// </list>
	/// 内部调 <c>EvaluateJavaScriptAsync($"window.__onMessage({msgJson})")</c>，
	/// 与 <see cref="PostTurn"/> 同样把 JSON 作为 JS 字面量直接嵌入。
	/// </remarks>
	/// <param name="messageJson">合法 JSON 字符串。作为 JS 字面量传入函数参数。</param>
	void PostMessage(string messageJson);

	/// <summary>
	/// JS → C#：Vue 端 <c>postMessage(json)</c> 触发。订阅者（<c>BridgeHost</c>）将消息入 <c>MauiBridgeIO</c>。
	/// </summary>
	event Action<string>? InputReceived;

	/// <summary>
	/// 挂载到 MAUI <see cref="WebView"/>——订阅平台原生消息事件（Windows <c>WebMessageReceived</c> /
	/// Android <c>AddJavascriptInterface</c>）。在 <c>WebView.HandlerChanged</c> 事件内调。
	/// </summary>
	/// <param name="webView">MAUI 跨平台 <see cref="WebView"/>，实现内部取平台原生视图。</param>
	/// <returns>Task 在平台原生视图初始化完成、桥接事件订阅就绪后完成。
	/// 调用方应 await 此 Task 后再设 <c>WebView.Source</c>——
	/// Windows 平台 <c>SetVirtualHostNameToFolderMapping</c> 必须在导航开始前完成。</returns>
	Task Attach(WebView webView);

	/// <summary>
	/// 弹出原生文件夹选择器——issue 09 文件选择器（Windows 专用）。
	/// </summary>
	/// <remarks>
	/// <para>
	/// 平台实现：
	/// <list type="bullet">
	///   <item>Windows：<c>WindowsJsBridge.PickFolderAsync</c> 重写此方法——
	///     WinRT <c>Windows.Storage.Pickers.FolderPicker</c>。
	///     .NET 10 MAUI (10.0.20) 的 <c>Microsoft.Maui.Storage</c> 没有 <c>FolderPicker</c> 类型
	///     （<c>FilePicker</c> 存在但 <c>FolderPicker</c> 不存在），故直接用 WinRT API。
	///     unpackaged 模式必须调 <c>InitializeWithWindow.Initialize(picker, hwnd)</c> 关联窗口句柄。</item>
	///   <item>Android：不走此方法——Android 的「更改目录」走 SAF 原生选择器
	///     （<c>pickSafDirectory</c> → <see cref="BridgeHost.HandlePickSafDirectory"/>，ADR-0019）；
	///     issues/15 S2 起的页内真实路径浏览器是<b>另一条</b>并列入口，走
	///     <see cref="BridgeHost.HandleListDirectories"/> + <c>getStorageRoots</c>
	///     （Vue 端 <c>ChangeDirDialog.vue</c> 的浏览区块）。Android 不会投递 <c>pickFolder</c>，
	///     故此方法不会被调用；默认实现返回 <c>null</c> 防御性兜底（万一被调用也不崩）。</item>
	/// </list>
	/// </para>
	/// <para>
	/// 必须在 UI 线程调用——WinRT picker 依赖窗口句柄，<see cref="BridgeHost.HandlePickFolder"/>
	/// 在 <see cref="BridgeHost.OnInputFromJs"/> 内（UI 线程）调此方法。
	/// </para>
	/// <para>
	/// game-library spec ID11：原 <c>AndroidJsBridge.PickFolderAsync</c> 空桩（<c>return null</c>）
	/// 已删除——default interface method 兜底，避免 Android 实现类被强制 override 一个不用的方法。
	/// </para>
	/// </remarks>
	/// <returns>用户选中的目录绝对路径；用户取消或失败返回 <c>null</c>。Android 路径不应到达此方法。</returns>
	Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
}
