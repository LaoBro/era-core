/**
 * Emuera.Headless ↔ Web 前端协议类型定义（issue 02）。
 *
 * 与 C# 端字段命名严格对称——直接对应序列化 JSON 的字段名：
 * - C# `DisplaySnapshot`（Emuera.Headless/Agent/DisplayState.cs:18）
 * - C# `TurnRecord` / `DisplayDiff` / `LineOp*` / `TurnOp*` / `PrintSegment` / `ButtonRef`
 *   （Emuera.Headless/Agent/TurnRecord.cs）
 *
 * 协议稳定性：ADR-0013/0014 已锁定字段集；C# `JsonSerializerOptions` 用
 * `WhenWritingNull` —— nullable 字段在为 null 时不写入 JSON，故 TS 类型中所有 optional
 * 字段（`?`）对应 C# 端 nullable reference type，反序列化时缺失即为 undefined。
 *
 * 手动维护：spec.md 决策——「协议已稳定，自动生成工具脆」。如 C# 端新增字段，此处同步。
 */

// ---------- 基础原子类型 ----------

/**
 * 按钮提交值。C# `ButtonRef.value` 是 `object`，实际承载 `long`（IsInteger=true）
 * 或 `string`（IsInteger=false）。JSON 数字反序列化到 JS `number`，足够表达 Emuera
 * 整数按钮的输入值（inputId 通常 << 2^53）。
 */
export type ButtonValue = number | string;

/**
 * 文本片段（C# `PrintSegment`）。一段带样式的字符序列。
 * `text` 永远存在；其余字段 nullable，C# WhenWritingNull 时省略。
 *
 * v8（issue 02）：新增 `image?` / `shape?`——图片与矩形色块的类型化承载。
 * v10：新增 `underline?`——保留彩色下划线空格。
 * v11：新增 `strikeout?`——PRINT_SLIDER 实际用 ERB FONTSTYLE 位4（删除线）绘制滑条轨道，
 *      v10 只实现 underline 导致删除线缺失；现按 ERB 位契约（位4=删除线、位8=下划线）对称补齐。
 * 老客户端忽略未知字段自然降级（纯增量）。
 */
export interface PrintSegment {
  text: string;
  /**
   * 文字色 hex（"#RRGGBB"）。null = 默认文字色——C# 只在「与 `ConfigCode.ForeColor`
   * 不同」时写入（ColorChanged 判断），故 null 时渲染端须用游戏默认色
   * （game store `defaultTextColor`，来自 GET /state / MAUI layout 的 `foreColor`）。
   */
  color?: string | null;
  bold?: boolean | null;
  italic?: boolean | null;
  fontname?: string | null;
  /** v10：文本下划线（ERB FONTSTYLE 位8）。 */
  underline?: boolean | null;
  /** v11：文本删除线（ERB FONTSTYLE 位4）。PRINT_SLIDER 用彩色删除线空格绘制滑条横线。 */
  strikeout?: boolean | null;
  /** v8：图片 segment（C# `SegmentImage`）。有值则前端按 image 渲染，忽略 text（text 为 CLI 调试标记）。 */
  image?: SegmentImage | null;
  /** v8：形状 segment（C# `SegmentShape`）。type='rect'，几何已解析 px。 */
  shape?: SegmentShape | null;
}

/**
 * v8：图片 segment 几何（C# `SegmentImage`，issue 02）。
 * - `src`：游戏根相对路径（协议只传路径，前端经 `resolveResource` 拼 URL）
 * - `srcb`：悬停/选中态替换图（相对路径）
 * - `srcm`：映射图（点击热区——取点击点像素色 0xRRGGBB 作为按钮输入值）
 * - `width` / `height` / `ypos`：已解析 px 几何（C# 尺寸探针产出，缺省宽高已按纵横比推算）
 *
 * v9（issue 07）：`crop`——图集 sprite 裁切几何（resources/*.csv 第 3-6 列裁切矩形，
 * 已按显示尺寸缩放）。有值则前端渲染「overflow:hidden 定尺寸容器 + img 负偏移」，
 * `width`/`height` 是容器（裁切后显示）尺寸。
 */
export interface SegmentImage {
  src: string;
  srcb?: string | null;
  srcm?: string | null;
  width: number;
  height: number;
  ypos: number;
  /** v9：裁切几何——img 元素负偏移 + 元素渲染尺寸（整图×缩放）。null/省略 = 无裁切（整图）。 */
  crop?: SegmentCrop | null;
}

/**
 * v9：图片裁切几何（C# `SegmentCrop`，issue 07）。
 * - `x` / `y`：img 元素负偏移（margin-left / margin-top，≤0，已按显示尺寸缩放）
 * - `imgWidth` / `imgHeight`：img 元素渲染尺寸（整图 × 缩放系数；缩放系数 = 显示尺寸 / 裁切尺寸）
 *
 * 渲染：掩膜（overflow:visible 不变）→ `.term-crop` 容器（width/height + overflow:hidden）→
 * img（负 margin）——前端零布局数学（协议携带已解析几何原则）。
 */
export interface SegmentCrop {
  x: number;
  y: number;
  imgWidth: number;
  imgHeight: number;
}

/**
 * v8：形状 segment（C# `SegmentShape`，issue 02）。
 * type='rect'：彩色填充矩形。1 参 rect（整行色条）与 4 参（绝对定位）统一为该形态。
 * x/width 同时决定矩形绘制位置和该 segment 的流式占位宽度。
 */
export interface SegmentShape {
  type: 'rect';
  x: number;
  y: number;
  width: number;
  height: number;
  color: string;
}

/**
 * v8：背景图状态（C# `BgImageState`，issue 02）。
 * - `src`：游戏根相对路径
 * - `depth`：z 序（WinForms 降序烘焙——depth 大的在上）
 * - `opacity`：0.0-1.0
 */
export interface BgImageState {
  src: string;
  depth: number;
  opacity: number;
}

/**
 * 按钮几何引用（C# `ButtonRef`）。
 *
 * - `value` / `isInteger` / `generation` 永远存在（C# record 必填参数）
 * - `col` / `width` 是可空 int（C# 默认值 `null`）—— 快照中始终填充（BuildPrintOpsForLine
 *   计算得出），增量 PrintOp 中也填充；这里保留 optional 是为了和 C# 类型对称。
 * - `generation`：按钮所属回合的 generation 号，前端用于按钮过期检测（v7）。
 *
 * 几何语义（ADR-0013 决策二）：`col` 是行内起始列（0-based），`width` 是按钮显示宽度
 * （按字符 display-width 计算，含全角字符占 2 列）。
 */
export interface ButtonRef {
  value: ButtonValue;
  isInteger: boolean;
  generation: number;
  col?: number | null;
  width?: number | null;
}

// ---------- DisplaySnapshot：全量快照 ----------

/**
 * 行内条目（C# `DisplayEntry`）。一段 segments + 可选 button。
 * 一个 entry 对应 C# 端一个 `ConsoleButtonString`——可能是按钮也可能是普通文本。
 */
export interface DisplayEntry {
  segments: PrintSegment[];
  button?: ButtonRef | null;
}

/**
 * 单行（C# `DisplayLine`）。
 *
 * `align` 取值：`"left"` / `"center"` / `"right"` / null（C# `AlignToString` 默认 LEFT→"left"）。
 * `isLineEnd` 标记行是否已终止（PRINT 后是否经过 NewLineOp）。
 */
export interface DisplayLine {
  entries: DisplayEntry[];
  align?: 'left' | 'center' | 'right' | null;
  isLineEnd: boolean;
}

/**
 * 全量显示状态（C# `DisplaySnapshot`，ADR-0013 决策二 / ADR-0016 加 timer 字段）。
 *
 * 由 C# `GET /snapshot` 端点产出，或前端从初始空状态经多轮 ops 重建得到。
 * WS 晚加入者先调 GET /snapshot 拿到这个，再订阅 WS 收增量 ops/diff。
 *
 * - `lines`：当前所有显示行
 * - `bgColor`：当前背景色（CSS hex 形如 "#FF0000"，可能为 null）
 * - `bgImages`：v8——当前背景图列表（depth 降序应用；空/省略 = 无背景图）
 * - `state`：游戏状态字符串（C# `ConsoleState.ToString()`，如 "WaitInput"/"Quit"/"Error"）
 * - `inputType`：当前输入请求类型（C# `InputType.ToString()`，如 "IntValue"/"StrValue"/"AnyKey"/"EnterKey"）
 * - `needValue`：是否需要值输入（inputType=IntValue/StrValue 时 true）
 * - `protocolVersion`：协议版本号（与 `TurnRecord.protocolVersion` 一致，v7 当前）
 * - `generation`：快照时的 generation 号（v7），前端用于按钮过期检测
 * - `timeLimit`：ADR-0016——TINPUT 总时长（毫秒），null/省略 = 非 TINPUT 期间
 * - `displayTime`：ADR-0016——是否向玩家显示倒计时（ERB 可设 false）
 * - `timeUpMessage`：ADR-0016——ERB 超时提示文案
 *
 * `timedOut` **不在 DisplaySnapshot**——snapshot 表示"当前状态"而非"如何到达此状态"，
 * 晚加入者只关心"现在还有多久超时"，不关心上一帧是否刚超时。
 */
export interface DisplaySnapshot {
  lines: DisplayLine[];
  bgColor?: string | null;
  /** v8：背景图列表。depth 降序应用；省略/空 = 无背景图（C# 紧凑归一：空列表不进 JSON）。 */
  bgImages?: BgImageState[] | null;
  state: string;
  inputType?: string | null;
  needValue: boolean;
  protocolVersion: number;
  generation: number;
  timeLimit?: number | null;
  displayTime?: boolean | null;
  timeUpMessage?: string | null;
}

// ---------- DisplayDiff：行级差异（增量） ----------

/**
 * 行级差异操作（C# `LineOp`，plan C v5 + shift_head 扩展）。
 *
 * Emuera 显示模型是追加式：新内容追加到末尾，CLEARLINE 从末尾删除，CLEAR 全清；
 * MaxLog 截断时头部行被删除。故 diff 需要四类操作：
 * - append / clear_line_diff / clear_screen：尾部操作（plan C v5）
 * - shift_head：头部截断（MaxLog 滚动）——由 C# 端 `ConsolePrintManager.RemoveAt(0)`
 *   主动 enqueue `ShiftHeadTurnOp` 捕获，避免 `StructuralDiff` 把头部删除误判为
 *   `ClearScreenOp + AppendLinesOp` 全量重印。
 *
 * 鉴别字段 `type` 与 C# 子类 `LineOp.type` 一一对应。
 *
 * 应用顺序（applyDiff 内 switch）：shift_head → clear_line_diff → clear_screen → append。
 * shift_head 与 clear_screen 互斥（同回合不会既头部截断又全清），
 * 但 shift_head 可与 clear_line_diff + append 共存。
 */
export type LineOp =
  | { type: 'append'; newLines: DisplayLine[] }
  | { type: 'clear_line_diff'; clearCount: number }
  | { type: 'clear_screen' }
  | { type: 'shift_head'; count: number };

/**
 * 两个连续 DisplaySnapshot 之间的差异（C# `DisplayDiff`）。
 *
 * - `lineOps`：行级操作序列（按顺序应用）
 * - `bgColor`：背景色变化（null 表示未变；非空表示设置为新值）
 * - `bgImages`：v8——背景图整体替换（非 null 即有变更：`[]`=清空，非空列表=新状态；
 *   null=未变）——与 `bgColor` 同模式
 *
 * state/inputType/needValue/protocolVersion 由外层 `TurnRecord` 携带，不在此重复。
 */
export interface DisplayDiff {
  lineOps: LineOp[];
  bgColor?: string | null;
  bgImages?: BgImageState[] | null;
}

// ---------- TurnOp：增量 ops（v5 之前 / 内部流） ----------

/**
 * 增量操作（C# `TurnOp`，5 种变体）。
 *
 * 在 v5 协议中 TurnRecord 顶层只携带 `diff`（`LineOp[]`），不携带 `ops[]`。
 * 但 `TurnOp` 仍是 EmueraConsole 内部 `_pendingOps` 的模型，也是 `TestAdapter.ApplyOps`
 * 的输入——前端 TestAdapter 对称测试需要直接构造 TurnOp[]。
 *
 * 鉴别字段 `type` 与 C# 子类 `TurnOp.type` 一一对应：
 * - `print`：向当前行追加 entry（segments + button，含几何 col/width）
 * - `newline`：终止当前行（带 align），下一行开始
 * - `clearline`：从行列表末尾删除 n 行（n=0 时无操作）
 * - `clear`：清空全部行 + 重置 bgColor
 * - `set_bg`：更新 bgColor
 * - `set_bg_image` / `remove_bg_image` / `clear_bg_image`：v8——背景图增删清
 *   （C# 内部脏信号；对外增量面由 `DisplayDiff.bgImages` 整体替换承载）
 */
export type TurnOp =
  | { type: 'print'; segments: PrintSegment[]; button?: ButtonRef | null }
  | { type: 'newline'; align?: 'left' | 'center' | 'right' | null }
  | { type: 'clearline'; n: number }
  | { type: 'clear' }
  | { type: 'set_bg'; color: string }
  | { type: 'set_bg_image'; src: string; depth: number; opacity: number }
  | { type: 'remove_bg_image'; src: string }
  | { type: 'clear_bg_image' };

// ---------- TurnRecord：单回合 WS 帧 ----------

/**
 * WS 单回合帧（C# `TurnRecord`，ADR-0016 v6 加 timer 字段）。
 *
 * 每个 WS Text 帧是一个 `TurnRecord` JSON。前端 `parseTurnRecord` 把原始 JSON 字符串
 * 解析为该类型。v5 后 `diff` 是唯一增量格式（`ops` 已废弃移除）。
 *
 * - `state`：本回合后的游戏状态（"WaitInput"/"Quit"/"Error" 等）
 * - `inputType`：本回合后的输入请求类型（null 表示无需输入）
 * - `needValue`：是否需要值输入
 * - `diff`：行级差异（null 表示本回合显示未变——例如纯状态切换）
 * - `error`：错误信息（state=Error 时填充）
 * - `protocolVersion`：协议版本（v7 当前；首次帧必带，后续帧可省略）
 * - `generation`：本回合 generation 号（v7），前端用于按钮过期检测；v7 每帧必带
 * - `timeLimit`：ADR-0016——TINPUT 总时长（毫秒），null/省略 = 非 TINPUT 期间
 * - `displayTime`：ADR-0016——是否向玩家显示倒计时（ERB 可设 false）
 * - `timeUpMessage`：ADR-0016——ERB 超时提示文案
 * - `timedOut`：ADR-0016——本帧是否由 TINPUT 超时触发（非 nullable bool，默认 false）
 *
 * 注意：v5 协议中 `ops[]` 字段已废弃，本类型不包含该字段。C# `TestAdapter.ApplyOps`
 * 直接消费 `TurnOp[]`——TS 端做对称测试时也直接构造 `TurnOp[]`，不经 TurnRecord。
 */
export interface TurnRecord {
  state: string;
  inputType?: string | null;
  needValue: boolean;
  diff?: DisplayDiff | null;
  error?: string | null;
  protocolVersion?: number | null;
  timeLimit?: number | null;
  displayTime?: boolean | null;
  timeUpMessage?: string | null;
  /** ADR-0016：非 nullable bool，C# 每帧都发（默认 false） */
  timedOut: boolean;
  /** v7：本回合 generation 号，前端按钮过期检测。v7 每帧必带。 */
  generation: number;
}

// ---------- DisplayState：前端内部可变状态 ----------
//
// 与 C# `TestAdapter`（Emuera.Headless.Tests/TestAdapter.cs:23）字段对称：
// `lines` / `bgColor` / `state` / `inputType` / `needValue`。
//
// 与 `DisplaySnapshot` 的差别：
// - 不含 `protocolVersion`（协议版本是 wire 元数据，不属于显示状态）
// - 字段全部非 optional（应用 snapshot/ops 后必有确定值）
//
// 由 `applySnapshot` 从 `DisplaySnapshot` 重建，由 `applyOps` 经增量 ops 更新。
// 函数式纯度：所有 reducer 返回新对象，不修改入参（与 C# TestAdapter 可变实例不同——
// 前端 Pinia store 需要响应式不可变更新）。
export interface DisplayState {
  lines: DisplayLine[];
  bgColor: string | null;
  /** v8：当前背景图（空数组 = 无背景图）。applySnapshot 全量重建 / applyDiff 整体替换。 */
  bgImages: BgImageState[];
  state: string;
  inputType: string | null;
  needValue: boolean;
}

/**
 * 空状态（初始值）。`applySnapshot` 之前 / `clear` op 之后的状态。
 * state='' 与 C# TestAdapter `State = ""` 初始值对称。
 */
// ---------- 控制权交接（issue 04 / M3） ----------

/** Controller 身份。C# `ControllerInfo.Kind`：`agent` | `user`。空闲时 HTTP 返回 null。 */
export type ControllerKind = 'agent' | 'user';

export interface ControllerInfo {
  kind: ControllerKind;
  leaseExpiresAt?: string | null;
}

/** `GET /control` / acquire 响应中的控制快照。 */
export interface ControlStatus {
  controller: ControllerInfo | null;
  state: string;
}

/**
 * `GET /control/wait` 推送的控制事件。
 * C# `ControlEvent.Type`：acquired / released / stolen / lease_expired / game_ended / session_replaced。
 */
export type ControlEventType =
  | 'acquired'
  | 'released'
  | 'stolen'
  | 'lease_expired'
  | 'game_ended'
  | 'session_replaced';

export interface ControlEvent {
  type: ControlEventType;
  reason?: string | null;
  at?: string;
  controller: ControllerInfo | null;
  state: string;
}

export const EMPTY_DISPLAY_STATE: DisplayState = {
  lines: [],
  bgColor: null,
  bgImages: [],
  state: '',
  inputType: null,
  needValue: false,
};
