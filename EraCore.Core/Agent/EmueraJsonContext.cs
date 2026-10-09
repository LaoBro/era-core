using System.Text.Json.Serialization;
using MinorShift.Emuera.Runtime.Config.JSON;

namespace MinorShift.Emuera.GameView;

/// <summary>
/// System.Text.Json 源生成上下文（3.3 NativeAOT 硬性改造）。
/// AOT 下反射序列化被禁用（JsonSerializerIsReflectionDisabled），所有 JSON 路径必须经此上下文。
/// <para>
/// wire 契约保持不变的三个机制：
/// 1. <see cref="TurnJsonOptions"/> / <see cref="DisplayState.JsonOpts"/> 加 <c>TypeInfoResolver = EmueraJsonContext.Default</c>，
///    直接序列化 TurnRecord/DisplaySnapshot 走源生成 TypeInfo，输出与反射版逐字节一致（同 WhenWritingNull、无 naming policy）；
/// 2. <see cref="TurnOpConverter"/>/<see cref="LineOpConverter"/> 的装箱 Write 递归 <c>Serialize((object)value, options)</c>
///    经 options.TypeInfoResolver 按运行时具体类型解析——此处显式注册全部多态叶子类型，保证 GetTypeInfo 命中；
/// 3. 无 options 的调用点（JSONConfig / JsonlCommand 反序列化）改显式 context 调用。
/// </para>
/// </summary>
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(TurnRecord))]
[JsonSerializable(typeof(DisplayDiff))]
[JsonSerializable(typeof(DisplaySnapshot))]
// v12：awaiting 容器及其三个可空子对象（spec §5.2 第 6 项 / §5.4）。
// 均为**非多态**普通 record ⇒ 不需要新增 Converter，但必须都能被 resolver 解析
// ——否则 MAUI 等直接走 context 的路径会在运行时炸（不是「改一行注册」的量级）。
// v12：awaiting 容器及其三个可空子对象（spec §5.2 第 6 项 / §5.4）。
// 均为**非多态**普通 record ⇒ 不需要新增 Converter，但必须都能被 resolver 解析
// ——否则 MAUI 等直接走 context 的路径会在运行时炸（不是「改一行注册」的量级）。
// 注：源生成器也会顺带为 TurnRecord 属性类型生成元数据（实测：删掉下面四行，
// context-only 路径仍能序列化完整对象）。保留显式注册是**把意图写死**——本票的
// 形状闸门是 FatalTurnShapeTests（致命帧必须走 Core 的单一构造点）。
[JsonSerializable(typeof(AwaitInfo))]
[JsonSerializable(typeof(AwaitInput))]
[JsonSerializable(typeof(AwaitWait))]
[JsonSerializable(typeof(AwaitEnded))]
[JsonSerializable(typeof(DisplayLine))]
[JsonSerializable(typeof(DisplayEntry))]
[JsonSerializable(typeof(PrintSegment))]
[JsonSerializable(typeof(SegmentImage))]
[JsonSerializable(typeof(SegmentCrop))]
[JsonSerializable(typeof(SegmentShape))]
[JsonSerializable(typeof(BgImageState))]
[JsonSerializable(typeof(ButtonRef))]
[JsonSerializable(typeof(JsonlCommand))]
[JsonSerializable(typeof(JSONConfigData))]
// LineOp 多态叶子——LineOpConverter.Write 按运行时类型解析 TypeInfo 所需（抽象基类 converter 已挂 options.Converters）
[JsonSerializable(typeof(AppendLinesOp))]
[JsonSerializable(typeof(ClearLineDiffOp))]
[JsonSerializable(typeof(ClearScreenOp))]
[JsonSerializable(typeof(ShiftHeadLineOp))]
// TurnOp 多态叶子——TurnOpConverter.Write 同上
[JsonSerializable(typeof(PrintOp))]
[JsonSerializable(typeof(NewLineOp))]
[JsonSerializable(typeof(ClearLineOp))]
[JsonSerializable(typeof(ClearOp))]
[JsonSerializable(typeof(SetBgOp))]
[JsonSerializable(typeof(SetBgImageOp))]
[JsonSerializable(typeof(RemoveBgImageOp))]
[JsonSerializable(typeof(ClearBgImageOp))]
[JsonSerializable(typeof(ShiftHeadTurnOp))]
internal partial class EmueraJsonContext : JsonSerializerContext
{
}
