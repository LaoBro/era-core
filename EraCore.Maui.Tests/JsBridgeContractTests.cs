#if WINDOWS
using System;
using System.Reflection;
using System.Threading.Tasks;
using EraCore.Maui.JsBridge;
using Microsoft.Maui.Controls;
using MinorShift.Emuera.Files;
using Xunit;

namespace EraCore.Maui.Tests;

/// <summary>
/// IJsBridge 接口契约单测——issue 06 / spec ID5（issue 07 修订：Attach 返回 Task；issue 09 新增 PostMessage + PickFolderAsync）。
/// </summary>
/// <remarks>
/// <para>
/// 可测的接缝（issue 06 要求"mock 平台 WebView 验证 PostTurn 调用 + InputReceived 触发"）：
/// </para>
/// <list type="bullet">
///   <item><see cref="JsBridgeHelper.FormatPostScript"/>——纯函数，验证 C#→JS 的 JS 字面量构造
///       （即"PostTurn 传正确 JS 字面量"的契约本质，平台 ExecuteScriptAsync 调用是 glue）。</item>
///   <item><see cref="JsBridgeHelper.FormatPostMessageScript"/>——issue 09 非 turn 消息的 JS 字面量构造。</item>
///   <item><see cref="JsBridgeFactory.Create"/>——平台分流，验证 Windows 下返回 <see cref="WindowsJsBridge"/>。</item>
///   <item><see cref="IJsBridge"/> 反射——验证六成员（PostTurn/PostMessage/InputReceived/Attach/PickFolderAsync/SetGameRoot）契约形状。</item>
/// </list>
/// <para>
/// 平台原生类型（CoreWebView2 / Android.Webkit.WebView）紧耦合运行时环境，无法在单测中 mock；
/// 真实 PostTurn→ExecuteScriptAsync / WebMessageReceived→InputReceived 的端到端验证由 T07 集成测试覆盖。
/// </para>
/// </remarks>
public class JsBridgeContractTests
{
	/// <summary>
	/// 用例 1：FormatPostScript 把 turnJson 作为 JS 字面量嵌入函数调用——
	/// 即 PostTurn 内部传给 ExecuteScriptAsync 的脚本字符串契约。
	/// </summary>
	/// <remarks>
	/// turnJson 是合法 JSON，JSON ⊂ JS 字面量，直接嵌入函数参数无需 JSON.stringify（双重转义错误）。
	/// </remarks>
	[Theory]
	[InlineData("""{"turn":1,"buttons":[]}""", "window.__onTurn({\"turn\":1,\"buttons\":[]})")]
	[InlineData("""{"name":"英雄","hp":100}""", "window.__onTurn({\"name\":\"英雄\",\"hp\":100})")]
	[InlineData("""[]""", "window.__onTurn([])")]
	[InlineData("""{"nested":{"deep":true}}""", "window.__onTurn({\"nested\":{\"deep\":true}})")]
	public void FormatPostScript_embeds_turnJson_as_js_literal(string turnJson, string expected)
	{
		var script = JsBridgeHelper.FormatPostScript(turnJson);
		Assert.Equal(expected, script);
	}

	/// <summary>
	/// 用例 2：FormatPostScript 拒绝 null turnJson——
	/// PostTurn 调用前 turnJson 必须是合法 JSON 字符串（来自 TurnRecord 序列化）。
	/// </summary>
	[Fact]
	public void FormatPostScript_throws_on_null()
	{
		Assert.Throws<ArgumentNullException>(() => JsBridgeHelper.FormatPostScript(null!));
	}

	/// <summary>
	/// 用例 2b：FormatPostMessageScript 把 messageJson 作为 JS 字面量嵌入函数调用——
	/// issue 09 非 turn 消息通道，与 FormatPostScript 对称。
	/// </summary>
	/// <remarks>
	/// messageJson 是合法 JSON（如 <c>{"type":"folderPicked","path":"D:\\games"}</c>），
	/// JSON ⊂ JS 字面量，直接嵌入函数参数无需 JSON.stringify。
	/// </remarks>
	[Theory]
	[InlineData("""{"type":"folderPicked","path":"D:\\games"}""", "window.__onMessage({\"type\":\"folderPicked\",\"path\":\"D:\\\\games\"})")]
	[InlineData("""{"type":"folderPicked","error":"access denied"}""", "window.__onMessage({\"type\":\"folderPicked\",\"error\":\"access denied\"})")]
	[InlineData("""{}""", "window.__onMessage({})")]
	public void FormatPostMessageScript_embeds_messageJson_as_js_literal(string messageJson, string expected)
	{
		var script = JsBridgeHelper.FormatPostMessageScript(messageJson);
		Assert.Equal(expected, script);
	}

	/// <summary>
	/// 用例 2c：FormatPostMessageScript 拒绝 null messageJson——与 FormatPostScript 对称。
	/// </summary>
	[Fact]
	public void FormatPostMessageScript_throws_on_null()
	{
		Assert.Throws<ArgumentNullException>(() => JsBridgeHelper.FormatPostMessageScript(null!));
	}

	/// <summary>
	/// 用例 3：JsBridgeFactory.Create() 在 Windows 下返回 WindowsJsBridge 实例——
	/// 验证 #if WINDOWS 平台分流正确。
	/// </summary>
	[Fact]
	public void Factory_returns_WindowsJsBridge_on_windows()
	{
		var bridge = JsBridgeFactory.Create();
		Assert.IsType<WindowsJsBridge>(bridge);
	}

	/// <summary>
	/// 用例 4：JsBridgeFactory.Create() 返回的实例实现 IJsBridge——
	/// 验证工厂返回值可赋给接口抽象（调用方 BridgeHost 拿 IJsBridge 不平台分叉）。
	/// </summary>
	[Fact]
	public void Factory_returns_IJsBridge_instance()
	{
		IJsBridge bridge = JsBridgeFactory.Create();
		Assert.NotNull(bridge);
	}

	/// <summary>
	/// 用例 5：IJsBridge 接口契约形状——六个成员就位（issue 09 修订 + issue-009）：
	/// PostTurn(string) void 方法、PostMessage(string) void 方法、
	/// InputReceived 事件（Action&lt;string&gt;）、Attach(WebView) 返回 Task 方法、
	/// PickFolderAsync() 返回 Task&lt;string?&gt; 方法、SetGameRoot(GameTree?) void 方法。
	/// </summary>
	/// <remarks>
	/// 反射验证防止意外重命名/签名变更破坏 BridgeHost 调用方。
	/// issue 07 修订：Attach 返回类型从 void 改为 Task（Windows SetVirtualHostNameToFolderMapping
	/// 必须 await EnsureCoreWebView2Async 完成后再调，否则首帧导航失败）。
	/// issue 09 新增：PostMessage（非 turn 消息通道）+ PickFolderAsync（原生文件夹选择器）。
	/// </remarks>
	[Fact]
	public void IJsBridge_interface_has_required_contract_members()
	{
		var type = typeof(IJsBridge);

		// PostTurn(string) 实例方法，返回 void
		var postTurn = type.GetMethod("PostTurn", BindingFlags.Instance | BindingFlags.Public);
		Assert.NotNull(postTurn);
		Assert.Single(postTurn!.GetParameters());
		Assert.Equal(typeof(string), postTurn.GetParameters()[0].ParameterType);
		Assert.Equal(typeof(void), postTurn.ReturnType);

		// PostMessage(string) 实例方法，返回 void（issue 09）
		var postMessage = type.GetMethod("PostMessage", BindingFlags.Instance | BindingFlags.Public);
		Assert.NotNull(postMessage);
		Assert.Single(postMessage!.GetParameters());
		Assert.Equal(typeof(string), postMessage.GetParameters()[0].ParameterType);
		Assert.Equal(typeof(void), postMessage.ReturnType);

		// Attach(WebView) 实例方法，返回 Task（issue 07 修订）
		var attach = type.GetMethod("Attach", BindingFlags.Instance | BindingFlags.Public);
		Assert.NotNull(attach);
		Assert.Single(attach!.GetParameters());
		Assert.Equal(typeof(WebView), attach.GetParameters()[0].ParameterType);
		Assert.Equal(typeof(Task), attach.ReturnType);

		// PickFolderAsync() 实例方法，返回 Task<string?>（issue 09）
		var pickFolder = type.GetMethod("PickFolderAsync", BindingFlags.Instance | BindingFlags.Public);
		Assert.NotNull(pickFolder);
		Assert.Empty(pickFolder!.GetParameters());
		Assert.Equal(typeof(Task<string?>), pickFolder.ReturnType);

		// SetGameRoot(GameTree?) 实例方法，返回 void（issue-009：资源通道按显式根读）
		var setGameRoot = type.GetMethod("SetGameRoot", BindingFlags.Instance | BindingFlags.Public);
		Assert.NotNull(setGameRoot);
		Assert.Single(setGameRoot!.GetParameters());
		Assert.Equal(typeof(GameTree), setGameRoot.GetParameters()[0].ParameterType);
		Assert.Equal(typeof(void), setGameRoot.ReturnType);

		// InputReceived 事件 —— event Action<string>?
		var inputReceived = type.GetEvent("InputReceived", BindingFlags.Instance | BindingFlags.Public);
		Assert.NotNull(inputReceived);
		Assert.Equal(typeof(Action<string>), inputReceived!.EventHandlerType);
	}

	/// <summary>
	/// 用例 6：WindowsJsBridge 实现 IJsBridge——
	/// 验证平台实现类正确实现接口（编译期已保证，运行时再次断言）。
	/// </summary>
	[Fact]
	public void WindowsJsBridge_implements_IJsBridge()
	{
		var bridge = new WindowsJsBridge();
		Assert.IsAssignableFrom<IJsBridge>(bridge);
	}

	/// <summary>
	/// 用例 7：WindowsJsBridge.InputReceived 事件订阅/取消订阅安全——
	/// 验证事件语义：订阅/取消订阅不抛异常。
	/// </summary>
	/// <remarks>
	/// 不依赖 CoreWebView2——真实 WebMessageReceived→InputReceived 触发链路由 T07 集成测试覆盖。
	/// 不通过反射访问编译器生成的后备字段（实现细节，脆弱）。
	/// </remarks>
	[Fact]
	public void WindowsJsBridge_InputReceived_event_supports_subscribe_unsubscribe()
	{
		var bridge = new WindowsJsBridge();
		Action<string> handler = _ => { };

		bridge.InputReceived += handler;
		bridge.InputReceived -= handler;

		// 订阅/取消订阅完成，不抛异常即通过。
		Assert.True(true);
	}

	/// <summary>
	/// 用例 8：WindowsJsBridge.VirtualHostName 常量——issue 07 unpackaged 模式虚拟主机名。
	/// </summary>
	/// <remarks>
	/// 反射验证常量存在且值正确——MainPage.ResolveWebViewUrl 和 Vue isMauiEnvironment 均依赖此值。
	/// </remarks>
	[Fact]
	public void WindowsJsBridge_has_VirtualHostName_constant()
	{
		var field = typeof(WindowsJsBridge).GetField(
			"VirtualHostName",
			BindingFlags.NonPublic | BindingFlags.Static);
		Assert.NotNull(field);
		Assert.Equal("app.local", field!.GetValue(null));
	}
}
#endif
