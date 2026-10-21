using MinorShift.Emuera.Files;
#if ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Android.Webkit;
using AndroidX.WebKit;
using Java.Interop;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Handlers;
using MinorShift.Emuera;
using MinorShift.Emuera.Assets;
using AWebView = Android.Webkit.WebView;

namespace EraCore.Maui.JsBridge;

/// <summary>
/// Android 平台 <see cref="IJsBridge"/> 实现——基于 Android.Webkit.WebView（issue 06 / spec ID5）。
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Attach"/>：从 MAUI <see cref="Microsoft.Maui.Controls.WebView"/> 取 <see cref="AWebView"/> 平台视图，
/// 调 <see cref="AWebView.AddJavascriptInterface(Java.Lang.Object, string?)"/> 注册 <see cref="Bridge"/> 实例，
/// JS 端通过 <c>window.appBridge.postMessage(json)</c> 触发 <see cref="Bridge.PostMessage"/> → <see cref="InputReceived"/>。
/// </para>
/// <para>
/// <see cref="PostTurn"/>：调 <c>AWebView.EvaluateJavascript(string, IValueCallback)</c>
/// 执行 <c>window.__onTurn(turnJson)</c>——turnJson 作为 JS 字面量直接嵌入（JSON ⊂ JS 字面量）。
/// </para>
/// <para>
/// Vue 端（Android）： <c>window.appBridge.postMessage(json)</c> → 触发 <see cref="Bridge.PostMessage"/>。
/// </para>
/// <para>
/// 注意： <see cref="Bridge"/> 必须继承 <see cref="Java.Lang.Object"/> 才能暴露给 JS（<c>AddJavascriptInterface</c> 要求）。
/// 方法注解 <c>[JavascriptInterface]</c> 是 Android 4.2+ 的安全要求（仅注解的方法对 JS 可见）。
/// </para>
/// <para>
/// game-library spec ID11：原 <c>PickFolderAsync</c> 空桩已删除——Android 的「更改目录」走
/// 系统选择器（<c>pickSafDirectory</c>，ADR-0019），且自 issues/15 方案 B 起这是唯一目录入口
/// （页内真实路径浏览器已下线）。接口默认实现
/// （<c>Task.FromResult&lt;string?&gt;(null)</c>）兜底，Android 路径不会到达此方法。
/// </para>
/// </remarks>
internal sealed class AndroidJsBridge : IJsBridge
{
	private AWebView? _androidWebView;
	private Bridge? _bridge;
	private WebViewAssetLoader? _assetLoader;
	private bool _attached;

	/// <inheritdoc />
	public event Action<string>? InputReceived;

    /// <summary>ADR-0019 诊断：静态日志缓冲区，Vue 通过 scanGames/gamesScanned 回复可见。</summary>
    internal static string? LastDiagnostic;

    /// <summary>InputReceived 无订阅者时的兜底回调。</summary>
    internal static Action<string>? FallbackInputHandler;

    /// <inheritdoc />
    public Task Attach(Microsoft.Maui.Controls.WebView webView)
	{
		Android.Util.Log.Info("EmueraMaui", $"AndroidJsBridge.Attach called, _attached={_attached}, Handler={(webView?.Handler != null ? webView.Handler.GetType().Name : "null")}");
		if (_attached)
			return Task.CompletedTask;
		if (webView?.Handler is not WebViewHandler handler)
		{
			Android.Util.Log.Warn("EmueraMaui", $"AndroidJsBridge.Attach: WebViewHandler null, cannot attach");
			return Task.CompletedTask;
		}
		if (handler.PlatformView is not AWebView platformView)
		{
			Android.Util.Log.Warn("EmueraMaui", $"AndroidJsBridge.Attach: PlatformView is {handler.PlatformView?.GetType().Name ?? "null"}, not Android.Webkit.WebView");
			return Task.CompletedTask;
		}

		_androidWebView = platformView;
		_bridge = new Bridge(this);
		platformView.AddJavascriptInterface(_bridge, "appBridge");

		// ADR-0019：WebViewClient 拦截 bridge:// URL——可靠 JS→C# 通道，不依赖 appBridge
		// issue 05：同一 client 内接 WebViewAssetLoader（ShouldInterceptRequest 委托）——
		// 两个 PathHandler（first-match-wins，注册顺序：窄前缀先）：
		//   1) /wwwroot/ → WwwrootPathHandler（读 android_asset/wwwroot/ 前端静态文件 index.html/assets/*）
		//   2) /         → GameAssetPathHandler（SAF 读游戏图片字节 + CORS/缓存头）
		//
		// 关键设计：Android 页面也用 https://game.local/wwwroot/index.html 加载（ResolveWebViewUrl），
		// 而非 file://——file:// 页面里的 https:// 子资源请求不会进入 shouldInterceptRequest
		//（AndroidX WebViewAssetLoader 官方设计前提：页面与资源同 https 域），
		// 实测图片直接走真实网络 → ERR_NAME_NOT_RESOLVED → 图片全空。
		// 整页迁到 https 虚拟域后与 Windows 模式（app.local 页面 + game.local 资源）对称。
		// 注意：Xamarin.AndroidX.WebKit 1.9.0 的 .NET 绑定只暴露 AssetsPathHandler(Context)
		// 单参构造（读 android_asset/ 根），无 (Context, string assetsPath) 重载——
		// 故用自定义 WwwrootPathHandler 读 android_asset/wwwroot/，MIME 映射自持。
		var appContext = Android.App.Application.Context;
		_assetLoader = new WebViewAssetLoader.Builder()
			.SetDomain(GameAssetConstants.VirtualHostName)
			.AddPathHandler("/wwwroot/", new WwwrootPathHandler(appContext))
			.AddPathHandler("/", new GameAssetPathHandler())
			.Build();
		platformView.SetWebViewClient(new BridgeClient(this, _assetLoader));

		_attached = true;
		Android.Util.Log.Info("EmueraMaui", "AndroidJsBridge.Attach completed: appBridge registered");
		// Android WebView 无需异步初始化（AddJavascriptInterface 同步生效），直接返回 CompletedTask。
		return Task.CompletedTask;
	}

	/// <inheritdoc />
	public void PostTurn(string turnJson)
	{
		if (_androidWebView is null)
			return;
		var script = JsBridgeHelper.FormatPostScript(turnJson);
		try
		{
			// EvaluateJavaScript 在 UI 线程异步执行；调用方（BridgeHost）已 Dispatcher.Dispatch 切到 UI 线程。
			// 第二参数 IValueCallback 接收 JS 返回值，本场景不消费，传 null。
			_androidWebView.EvaluateJavascript(script, null);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"AndroidJsBridge.PostTurn failed: {ex.Message}");
		}
	}

	/// <inheritdoc />
	public void PostMessage(string messageJson)
	{
		if (_androidWebView is null)
			return;
		var script = JsBridgeHelper.FormatPostMessageScript(messageJson);
		try
		{
			// 与 PostTurn 同样——EvaluateJavaScript 在 UI 线程异步执行。
			// __onMessage 在 Vue 端由 registerMessageHandler 注册，未注册时返 undefined，无副作用。
			_androidWebView.EvaluateJavascript(script, null);
		}
		catch (Exception ex)
		{
			System.Diagnostics.Debug.WriteLine($"AndroidJsBridge.PostMessage failed: {ex.Message}");
		}
	}

	/// <summary>
	/// JS → C# 桥接对象——通过 <c>AddJavascriptInterface</c> 注册为 <c>window.appBridge</c>。
	/// Vue 端调 <c>window.appBridge.postMessage(json)</c> 触发 <see cref="PostMessage"/>，
	/// 进而触发 <see cref="AndroidJsBridge.InputReceived"/>。
	/// </summary>
	/// <remarks>
	/// 必须继承 <see cref="Java.Lang.Object"/>（<c>AddJavascriptInterface</c> 要求 Java 对象）。
	/// <c>[JavascriptInterface]</c> 注解是 Android 4.2+ 安全要求——未注解的方法对 JS 不可见。
	/// </remarks>
	private sealed class Bridge : Java.Lang.Object
	{
		private readonly AndroidJsBridge _owner;

		public Bridge(AndroidJsBridge owner)
		{
			_owner = owner;
		}

		[JavascriptInterface]
		public void PostMessage(string message)
		{
			if (!string.IsNullOrEmpty(message))
			{
				var hasSub = _owner.InputReceived != null;
				if (hasSub)
					_owner.InputReceived!.Invoke(message);
				else
					FallbackInputHandler?.Invoke(message);
			}
		}
	}

	/// <summary>ADR-0019：bridge:// URL → Action 回调——可靠 JS→C# 通道。</summary>
	internal static event Action<string>? BridgeUrlReceived;

	private sealed class BridgeClient : WebViewClient
	{
		private readonly AndroidJsBridge _bridge;
		private readonly WebViewAssetLoader _assetLoader;

		public BridgeClient(AndroidJsBridge bridge, WebViewAssetLoader assetLoader)
		{
			_bridge = bridge;
			_assetLoader = assetLoader;
		}

		/// <summary>
		/// issue 05：拦截 <c>https://game.local/*</c> 图片请求——委托 WebViewAssetLoader
		/// 到 <see cref="GameAssetPathHandler"/>（SAF 读字节 + CORS/缓存头）。
		/// 非 assetLoader 域返回 null 走默认加载（不影响 bridge:// 拦截）。
		/// </summary>
		public override WebResourceResponse? ShouldInterceptRequest(AWebView? view, IWebResourceRequest? request)
		{
			// .NET 绑定：WebViewAssetLoader.ShouldInterceptRequest(Android.Net.Uri)
			var response = request?.Url != null ? _assetLoader.ShouldInterceptRequest(request.Url) : null;
			if (response != null)
			{
				Android.Util.Log.Info("EmueraMaui", $"AssetLoader intercepted: {request?.Url}");
				return response;
			}
			return base.ShouldInterceptRequest(view, request);
		}

		public override bool ShouldOverrideUrlLoading(AWebView? view, IWebResourceRequest? request)
		{
			if (request?.Url?.Scheme == "bridge")
			{
				var host = request.Url.Host;
				if (host == "post")
				{
					var msg = request.Url.GetQueryParameter("msg");
					if (msg != null)
					{
						var json = Uri.UnescapeDataString(msg);
						Android.Util.Log.Info("EmueraMaui", $"BridgeClient post: {json[..Math.Min(json.Length, 80)]}");
						BridgeUrlReceived?.Invoke(json);
						return true;
					}
				}
				else
				{
					Android.Util.Log.Info("EmueraMaui", $"BridgeClient action: {host}");
					BridgeUrlReceived?.Invoke(host ?? string.Empty);
					return true;
				}
			}
			// ShouldOverrideUrlLoading(WebView?, IWebResourceRequest?) 两参重载仅 API 24+ 受支持；
			// minSdk 21 下框架不会调用此重载（走单参 deprecated 重载），guard 仅为满足 CA1416。
			if (OperatingSystem.IsAndroidVersionAtLeast(24))
				return base.ShouldOverrideUrlLoading(view, request);
			return false;
		}
	}

	/// <summary>
	/// <c>https://game.local/wwwroot/*</c> 请求处理——读 <c>android_asset/wwwroot/</c> 下的
	/// Vue 前端静态文件（index.html / assets/*.js / *.css / 字体等）。
	/// <para>
	/// AndroidX <c>WebViewAssetLoader.AssetsPathHandler</c> 的 .NET 绑定只暴露单参构造
	/// （固定读 <c>android_asset/</c> 根），无法指定子目录，故自定义实现。
	/// MIME 用 <see cref="MimeTypeMap"/> 从扩展名取（Android 系统表，含 js/css/html/woff2），
	/// 兜底 <c>application/octet-stream</c>。JS/CSS 是 Vite 构建的 ES Module 产物，
	/// MIME 必须正确否则 WebView 报 “non-JavaScript MIME type” 白屏。
	/// <c>Handle</c> 声明为 <c>new</c> 消除 CS0108（有意隐藏 <see cref="Java.Lang.Object.Handle"/>）。
	/// </para>
	/// </summary>
	internal sealed class WwwrootPathHandler : Java.Lang.Object, WebViewAssetLoader.IPathHandler
	{
		private readonly Android.Content.Context _context;

		public WwwrootPathHandler(Android.Content.Context context) => _context = context;

		public new WebResourceResponse? Handle(string? path)
		{
			try
			{
				// WebViewAssetLoader 前缀匹配：/wwwroot/ 之后的部分（如 index.html / assets/x.js）。
				// 防目录穿越——assets 目录下不存在 ..，但防御性拦截。
				if (string.IsNullOrEmpty(path)
					|| path.Contains("..", StringComparison.Ordinal)
					|| path.StartsWith("/", StringComparison.Ordinal))
					return null;

				var rel = $"wwwroot/{path}";
				Android.Util.Log.Info("EmueraMaui", $"WwwrootPathHandler: open {rel}");
				using var stream = _context.Assets!.Open(rel);
				using var ms = new MemoryStream();
				stream.CopyTo(ms);
				var mime = GetMimeType(rel);
				// 无 CORS 头需求——页面与资源同域（均 game.local），同源请求。静态文件缓存 1 天。
				var headers = new Dictionary<string, string>
				{
					["Cache-Control"] = AssetChannel.CacheControlHeader,
				};
				return new WebResourceResponse(mime, null, 200, "OK", headers, new MemoryStream(ms.ToArray()));
			}
			catch (Java.IO.IOException)
			{
				Android.Util.Log.Warn("EmueraMaui", $"WwwrootPathHandler: missing {path}");
				return null;
			}
			catch (Exception ex)
			{
				Android.Util.Log.Error("EmueraMaui", $"WwwrootPathHandler: exception for {path}: {ex}");
				return null;
			}
		}

		/// <summary>扩展名 → MIME（Android MimeTypeMap 主，失败回退常见映射）。</summary>
		private static string GetMimeType(string path)
		{
			var ext = System.IO.Path.GetExtension(path);
			if (string.IsNullOrEmpty(ext))
				return "application/octet-stream";
			var mime = MimeTypeMap.Singleton?.GetMimeTypeFromExtension(ext.TrimStart('.').ToLowerInvariant());
			if (!string.IsNullOrEmpty(mime))
				return mime!;
			return ext.ToLowerInvariant() switch
			{
				".js" or ".mjs" => "text/javascript",
				".css" => "text/css",
				".html" or ".htm" => "text/html",
				".json" or ".map" => "application/json",
				".svg" => "image/svg+xml",
				".woff" => "font/woff",
				".woff2" => "font/woff2",
				_ => "application/octet-stream",
			};
		}
	}

	/// <summary>
	/// <c>https://game.local/*</c> 图片请求处理——游戏资源通道（issue 05 / spec Q3 路线 B / Q5 安全）。
	/// 消毒/白名单/读字节/MIME 全部收敛在 <see cref="AssetChannel"/>（与 Windows/Kestrel 共用单点）；
	/// 此处只做平台胶水：相对路径 → AssetChannel → <see cref="WebResourceResponse"/>（带缓存/CORS 头）。
	/// 失败返回 null（WebView 按默认处理，不泄露资源存在性）。
	/// </summary>
	/// <remarks>
	/// 必须继承 <see cref="Java.Lang.Object"/>（WebViewAssetLoader.PathHandler 是 Java 接口，
	/// IJavaObject 是绑定实现的前提，与 <see cref="Bridge"/> 同模式）。
	/// <c>Handle</c> 声明为 <c>new</c> 消除 CS0108（有意隐藏 <see cref="Java.Lang.Object.Handle"/>）。
	/// </remarks>
	internal sealed class GameAssetPathHandler : Java.Lang.Object, WebViewAssetLoader.IPathHandler
	{
		public new WebResourceResponse? Handle(string? path)
		{
			// 异常可见性：之前无 try-catch，AssetChannel 内部未捕获异常会被 WebView 静默吞，
			// 日志里无任何痕迹，排查盲区。AndroidJsBridge.Attach 的 ensure 期望：能成功读就 serve，
			// 失败/异常统一走 WebView 默认（→ 真实网络 → ERR_NAME_NOT_RESOLVED），同时打 error logcat。
			try
			{
				if (string.IsNullOrEmpty(path))
					return null;
				var paths = GamePaths.Current;
				if (paths?.DirAccessor == null
					|| !AssetChannel.TryGetImage(paths.DirAccessor, paths.ExeDir, path, out var bytes, out var mime))
				{
					Android.Util.Log.Warn("EmueraMaui", $"GameAssetPathHandler: reject {path}");
					return null;
				}
				// 显式缓存头——否则无浏览器级缓存，状态屏每回合重印画像每次走 SAF IPC（性能）。
				// 头策略与 Kestrel/Windows 共用 AssetChannel 常量（改缓存/CORS 只动 Core）
				var headers = new Dictionary<string, string>
				{
					["Cache-Control"] = AssetChannel.CacheControlHeader,
					// srcm canvas 读像素需要 CORS 允许；图片 GET 无凭据，* 安全
					["Access-Control-Allow-Origin"] = AssetChannel.CorsAllowOriginHeader,
				};
				Android.Util.Log.Info("EmueraMaui", $"GameAssetPathHandler: serve {path} ({bytes.Length}B {mime})");
				return new WebResourceResponse(mime, null, 200, "OK", headers, new MemoryStream(bytes));
			}
			catch (Exception ex)
			{
				Android.Util.Log.Error("EmueraMaui", $"GameAssetPathHandler: exception for {path}: {ex}");
				return null;
			}
		}
	}
}
#endif
