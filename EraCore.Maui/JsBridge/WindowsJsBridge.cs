using MinorShift.Emuera.Files;
#if WINDOWS
using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using MinorShift.Emuera;
using MinorShift.Emuera.Assets;
using Windows.Storage.Streams;
using WinRT.Interop;

namespace EraCore.Maui.JsBridge;

/// <summary>
/// Windows 平台 <see cref="IJsBridge"/> 实现——基于 CoreWebView2（issue 06 / spec ID5）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Attach"/>：从 MAUI <see cref="WebView"/> 取 WinUI <see cref="WebView2"/> 平台视图，
/// 等待 <c>EnsureCoreWebView2Async</c> 完成，配置虚拟主机映射（unpackaged 模式下 <c>ms-appx-web:</c>
/// 协议不可用，改用 <c>https://app.local/</c> 映射到 wwwroot 文件夹），订阅 <see cref="CoreWebView2.WebMessageReceived"/>。
/// </para>
/// <para>
/// <see cref="PostTurn"/> / <see cref="PostMessage"/>：调 <see cref="CoreWebView2.ExecuteScriptAsync"/> 执行
/// <c>window.__onTurn(turnJson)</c> / <c>window.__onMessage(msgJson)</c>——
/// JSON 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量）。
/// 异步 fire-and-forget，异常捕获写日志（不阻塞游戏循环线程）。
/// </para>
/// <para>
/// <see cref="PickFolderAsync"/>：调 WinRT <c>Windows.Storage.Pickers.FolderPicker</c>——
/// .NET 10 MAUI 的 <c>Microsoft.Maui.Storage</c> 没有 <c>FolderPicker</c> 类型，故直接用 WinRT API。
/// unpackaged 模式必须调 <c>InitializeWithWindow.Initialize(picker, hwnd)</c> 关联窗口句柄，
/// HWND 从 <c>Application.Current.Windows[0]</c> 的 WinUI 平台视图取。
/// </para>
/// <para>
/// Vue 端（Windows）： <c>window.chrome.webview.postMessage(json)</c> → 触发 <see cref="CoreWebView2.WebMessageReceived"/>。
/// </para>
/// </remarks>
internal sealed class WindowsJsBridge : IJsBridge
{
	/// <summary>虚拟主机名——unpackaged 模式下用 <c>https://app.local/</c> 映射到 wwwroot 文件夹。</summary>
	/// <remarks>
	/// Packaged（MSIX）模式下可用 <c>ms-appx-web:///</c> 直接访问包内文件；
	/// unpackaged 模式下该协议不可用，改用 WebView2 的 SetVirtualHostNameToFolderMapping
	/// 把 <c>app.local</c> 映射到输出目录的 wwwroot/ 文件夹，Vue 用 <c>https://app.local/index.html</c> 加载。
	/// </remarks>
	internal const string VirtualHostName = "app.local";

	private CoreWebView2? _core;
	private bool _attached;
	private GameTree? _gameRoot;

	/// <inheritdoc />
	public event Action<string>? InputReceived;

	/// <inheritdoc />
	public void SetGameRoot(GameTree? gameRoot) => _gameRoot = gameRoot;

	/// <inheritdoc />
	public async Task Attach(WebView webView)
	{
		if (_attached)
			return;
		if (webView?.Handler is not WebViewHandler handler)
			return;
		if (handler.PlatformView is not WebView2 platformView)
			return;

		try
		{
			Console.WriteLine($"WindowsJsBridge.Attach starting, PlatformView ready");
			// EnsureCoreWebView2Async 在 CoreWebView2 已就绪时立即完成；首次调用会等待初始化。
			// 必须 await 完成后再调 SetVirtualHostNameToFolderMapping——否则映射未就绪时
			// 调用方（MainPage）设 Source 触发首次导航，https://app.local/ 请求会失败。
			await platformView.EnsureCoreWebView2Async();
			_core = platformView.CoreWebView2;
			if (_core is null)
			{
				Console.WriteLine("WindowsJsBridge.attach: CoreWebView2 null after EnsureCoreWebView2Async");
				return;
			}

			// 禁用 WebView2 原生捏合缩放（触屏双指）——画面缩放统一由前端控制
			// （EraCore.Web/src/composables/usePinchZoom.ts + 菜单按钮），原生缩放会双重放大。
			_core.Settings.IsPinchZoomEnabled = false;

			// unpackaged 模式下 ms-appx-web: 协议不可用——用 SetVirtualHostNameToFolderMapping
			// 把 https://app.local/ 映射到输出目录的 wwwroot/ 文件夹。
			// VueBuild.targets 的 CopyVueFrontendToWwwroot target 把 Vue 构建产物复制到此目录。
			// folderPath 用 AppContext.BaseDirectory（exe 所在目录）+ wwwroot 拼接。
			var wwwrootFolder = Path.Combine(AppContext.BaseDirectory, "wwwroot");
			Console.WriteLine($"WindowsJsBridge.attach: wwwrootFolder={wwwrootFolder}, exists={Directory.Exists(wwwrootFolder)}");
			if (Directory.Exists(wwwrootFolder))
			{
				_core.SetVirtualHostNameToFolderMapping(
					VirtualHostName,
					wwwrootFolder,
					CoreWebView2HostResourceAccessKind.Allow);
			}

			// issue 05：游戏图片资源通道——拦截 https://game.local/* 手动构造响应。
			// 不能复用上面的虚拟主机映射（静态映射带不了 CORS 头，srcm canvas 读像素会失败），
			// 改为与安卓 PathHandler 对称的拦截：AssetChannel 消毒/白名单/读字节/MIME 四步收敛。
			_core.AddWebResourceRequestedFilter(
				$"https://{GameAssetConstants.VirtualHostName}/*",
				CoreWebView2WebResourceContext.All);
			_core.WebResourceRequested += OnWebResourceRequested;

			_core.WebMessageReceived += OnWebMessageReceived;
			_attached = true;
			Console.WriteLine("WindowsJsBridge.attach completed, WebMessageReceived subscribed");
		}
		catch (Exception ex)
		{
			Console.WriteLine($"WindowsJsBridge.attach failed: {ex}");
		}
	}

	/// <inheritdoc />
	public async void PostTurn(string turnJson)
	{
		if (_core is null)
			return;
		var script = JsBridgeHelper.FormatPostScript(turnJson);
		try
		{
			// ExecuteScriptAsync 在 UI 线程执行；调用方（BridgeHost）已 Dispatcher.Dispatch 切到 UI 线程。
			// 返回值是 JS 表达式的 JSON 序列化（__onTurn 返 undefined → "null"），本场景不消费。
			await _core.ExecuteScriptAsync(script);
		}
		catch (Exception ex)
		{
			// 不向游戏循环抛——PostTurn 失败不该 crash 整个游戏。Vue 端会因未收到 turn 而超时。
			System.Diagnostics.Debug.WriteLine($"WindowsJsBridge.PostTurn failed: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async void PostMessage(string messageJson)
	{
		if (_core is null)
			return;
		var script = JsBridgeHelper.FormatPostMessageScript(messageJson);
		try
		{
			// 与 PostTurn 同样——ExecuteScriptAsync 在 UI 线程执行，调用方已 Dispatcher.Dispatch。
			// __onMessage 在 Vue 端由 registerMessageHandler 注册，未注册时返 undefined，无副作用。
			await _core.ExecuteScriptAsync(script);
		}
		catch (Exception ex)
		{
			// 不向调用方抛——PostMessage 失败仅写日志，让游戏循环继续运行。
			Console.WriteLine($"WindowsJsBridge.PostMessage failed: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public async Task<string?> PickFolderAsync()
	{
		try
		{
			// .NET 10 MAUI (10.0.20) 的 Microsoft.Maui.Storage 命名空间没有 FolderPicker 类型
			// （FilePicker 存在但 FolderPicker 不存在）——直接用 WinRT Windows.Storage.Pickers.FolderPicker。
			//
			// unpackaged 模式下必须调 InitializeWithWindow.Initialize(picker, hwnd) 关联窗口句柄，
			// 否则 PickSingleFolderAsync 抛 "Invalid window handle" 异常。
			// HWND 从 MAUI Application.Current.Windows[0] 的 WinUI 平台视图取。

			var mauiWindow = Application.Current?.Windows?.FirstOrDefault();
			if (mauiWindow?.Handler?.PlatformView is not Microsoft.UI.Xaml.Window winUiWindow)
			{
				Console.WriteLine("WindowsJsBridge.PickFolderAsync: no MAUI Window or WinUI PlatformView");
				return null;
			}
			var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(winUiWindow);

			var picker = new Windows.Storage.Pickers.FolderPicker();
			// FileTypeFilter 必须非空——FolderPicker 要求至少一个扩展名，用 "*" 匹配所有。
			picker.FileTypeFilter.Add("*");

			// unpackaged 模式必须调此方法关联窗口，否则 WinRT picker 无法显示。
			WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

			var folder = await picker.PickSingleFolderAsync();
			if (folder is null)
			{
				// 用户取消
				return null;
			}
			Console.WriteLine($"WindowsJsBridge.PickFolderAsync: picked {folder.Path}");
			return folder.Path;
		}
		catch (Exception ex)
		{
			Console.WriteLine($"WindowsJsBridge.PickFolderAsync failed: {ex}");
			return null;
		}
	}

	// ── issue 05：游戏图片资源通道（game.local 拦截） ─────────────────────────

	/// <summary>
	/// <c>https://game.local/{path}</c> 请求拦截（<see cref="GameAssetConstants.VirtualHostName"/>）。
	/// 消毒/白名单/读字节/MIME 全部收敛在 <see cref="AssetChannel"/>（spec Q5 单点实现）；
	/// 此处只做平台胶水：URL → 相对路径 → AssetChannel → <see cref="CoreWebView2WebResourceResponse"/>。
	/// 失败一律 404（不泄露资源是否存在），与 Kestrel /assets 同语义。
	/// </summary>
	private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
	{
		try
		{
			var uri = e.Request?.Uri;
			if (string.IsNullOrEmpty(uri) || _core == null)
				return;
			var path = GetRelativePath(uri);
			if (path == null)
			{
				e.Response = CreateAssetResponse(null, null);
				return;
			}

			if (_gameRoot is not { } gameRoot
				|| !AssetChannel.TryGetImage(gameRoot.DirAccessor, gameRoot.Root, path, out var bytes, out var mime))
			{
				e.Response = CreateAssetResponse(null, null);
				return;
			}
			e.Response = CreateAssetResponse(bytes, mime);
		}
		catch (Exception ex)
		{
			Console.WriteLine($"WindowsJsBridge.OnWebResourceRequested failed: {ex.Message}");
			e.Response = CreateAssetResponse(null, null);
		}
	}

	/// <summary>从 game.local URL 提取相对路径（如 <c>https://game.local/img/x.png</c> → <c>img/x.png</c>）。</summary>
	/// <remarks>
	/// 必须 <see cref="Uri.UnescapeDataString"/> 解码：浏览器对非 ASCII 路径自动百分号编码
	/// （日文 sprite 名 <c>ダミー</c> → <c>%E3%83%80%E3%83%9F%E3%83%BC</c>），
	/// <see cref="Uri.AbsolutePath"/> 保持编码形态——不解码则按字面名找文件恒 404（破图）。
	/// 解码后的路径照常经 AssetChannel 消毒（%2e%2e 等穿越编码解码为 .. 后仍被拒）。
	/// </remarks>
	private static string? GetRelativePath(string url)
	{
		if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
			return null;
		var path = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
		return path.Length == 0 ? null : path;
	}

	/// <summary>构造资源响应——字节 → InMemoryRandomAccessStream（CoreWebView2 要求 WinRT 流）。</summary>
	private CoreWebView2WebResourceResponse CreateAssetResponse(byte[]? bytes, string? mime)
	{
		if (_core == null) throw new InvalidOperationException("CoreWebView2 not attached");
		var status = bytes != null ? 200 : 404;
		var reason = bytes != null ? "OK" : "Not Found";
		IRandomAccessStream? content = null;
		if (bytes != null)
		{
			// WinRT InMemoryRandomAccessStream + DataWriter：CoreWebView2 响应体必须是 WinRT 流，
			// 不能用 MemoryStream。
			var ras = new InMemoryRandomAccessStream();
			var writer = new DataWriter(ras);
			writer.WriteBytes(bytes);
			writer.StoreAsync().GetAwaiter().GetResult();
			writer.DetachStream();
			content = ras;
		}
		// 响应头字符串：Content-Type + 缓存 + CORS（srcm canvas 读像素必需，与 Kestrel/安卓同策略）
		var headers = status == 200
			? $"Content-Type: {mime}\r\nCache-Control: {AssetChannel.CacheControlHeader}\r\nAccess-Control-Allow-Origin: {AssetChannel.CorsAllowOriginHeader}\r\n"
			: string.Empty;
		return _core.Environment.CreateWebResourceResponse(content, status, reason, headers);
	}

	private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		try
		{
			var message = e.TryGetWebMessageAsString();
			Console.WriteLine($"WindowsJsBridge.OnWebMessageReceived: message={message}");
			if (!string.IsNullOrEmpty(message))
			{
				InputReceived?.Invoke(message);
			}
		}
		catch (Exception ex)
		{
			Console.WriteLine($"WindowsJsBridge.OnWebMessageReceived failed: {ex.Message}");
		}
	}
}
#endif
