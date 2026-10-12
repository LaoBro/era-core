using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace MinorShift.Emuera.Server;

/// <summary>
/// System.Text.Json 源生成上下文（3.3 NativeAOT 硬性改造）——位于 Core，双宿主共享。
/// <para>
/// 注册 HTTP 请求 body POCO（<see cref="HttpRouteDispatcher"/> 内的 HttpInput/ControlRequest/
/// LoadGameRequest/ScanGameRequest/ListDirsRequest）与 <see cref="JsonObject"/>：供
/// <see cref="HttpRouteDispatcher"/> 经 <c>JsonSerializer.Deserialize(string, Type, context)</c>
/// 源生成重载反序列化（NativeAOT 安全，消除 IL2026/IL3050），以及 Kestrel 经
/// ConfigureHttpJsonOptions 的 TypeInfoResolverChain 序列化 <c>JsonResult(JsonObject)</c>。
/// </para>
/// <para>
/// 大小写不敏感（PropertyNameCaseInsensitive=true）——Kestrel 原行为；HttpListener 接入本上下文后
/// 由默认（大小写敏感）放宽为大小写不敏感：wire 键全小写，小写请求行为不变，仅额外接受大写键，与
/// Kestrel 对齐（两宿主 wire 契约一致）。Encoder 不在此设置（.NET source-gen options 无 encoder
/// 属性）——relaxed 转义由 HttpJsonOptions 默认（UnsafeRelaxedJsonEscaping）保证。
/// </para>
/// <para>
/// 曾位于 EraCore.Server 程序集（彼时仅 Kestrel 需要，且 Core 不反向引用 Server）；MAUI 经 Core 的
/// HttpListenerHost 同样需要 NativeAOT 安全反序列化，而 MAUI 只引用 Core——故迁回 Core（POCO 本就
/// 在 Core）。Kestrel 经 Core→Server 的 InternalsVisibleTo 访问 <c>Default</c>。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(HttpRouteDispatcher.HttpInput))]
// spec-v3 §5.1：input 帧的可选状态字段（HttpInput 的可达类型，源生成须显式注册否则 SYSLIB1030）。
[JsonSerializable(typeof(MinorShift.Emuera.GameView.InputKeyEvent))]
[JsonSerializable(typeof(MinorShift.Emuera.GameView.InputGeometry))]
[JsonSerializable(typeof(HttpRouteDispatcher.ControlRequest))]
[JsonSerializable(typeof(HttpRouteDispatcher.LoadGameRequest))]
[JsonSerializable(typeof(HttpRouteDispatcher.ScanGameRequest))]
[JsonSerializable(typeof(HttpRouteDispatcher.ListDirsRequest))]
internal partial class ServerJsonContext : JsonSerializerContext
{
}
