/**
 * Emuera.Headless ↔ Web 前端协议类型定义（issue 02）。
 *
 * 权威规格见 `docs/guides/turn-protocol.md` —— 本文件是它的 TS 镜像，改动协议时
 * 三处（C# record / 本文件 / 规格文档）必须同步。
 *
 * 与 C# 端字段命名严格对称——直接对应序列化 JSON 的字段名：
 * - C# `DisplaySnapshot`（EraCore.Core/Agent/DisplayState.cs:21）
 * - C# `TurnRecord` / `DisplayDiff` / `LineOp*` / `TurnOp*` / `PrintSegment` / `ButtonRef`
 *   （EraCore.Core/Agent/TurnRecord.cs）
 *
 * 协议稳定性：ADR-0013/0014 已锁定字段集；C# `JsonSerializerOptions` 用
 * `WhenWritingNull` —— nullable 字段在为 null 时不写入 JSON，故 TS 类型中所有 optional
 * 字段（`?`）对应 C# 端 nullable reference type，反序列化时缺失即为 undefined。
 *
 * 当前协议版本：**12**（wire 上只剩 `diff` / `awaiting` / `error` / `errorInfo` /
 * `protocolVersion` / `generation` 六个键——七个旧顶层字段已在收缩阶段全部退场）。
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

// ---------- v12：awaiting（唯一契约字段） ----------
//
// 「服务端此刻在等什么」——两个正交维度 + 一个互斥终态键。C# 权威形态见
// `EraCore.Core/Agent/AwaitInfo.cs`（单一 sealed record + 单一工厂 `AwaitInfo.Of`），
// 规格见 `docs/guides/turn-protocol.md` §2 与 spec-v5 §1。
//
// 为什么不是三个可选字段而是「容器」：`input` 与 `wait` 各自独立判定，于是
//   · TINPUT（既等输入、又有期限）第一次能被表达；
//   · `AWAIT 8000`（纯定时等待）与 `无参 AWAIT`（零时长让路）只是 `durationMs` 不同。
//
// 展开阶段（05–08）这些键曾与旧顶层字段（`state`/`inputType`/`needValue`/`timeLimit`
// 等）并存且同源；收缩阶段（09）删旧字段后 `awaiting` 成为 wire 上的唯一契约。

/** 「是否期待客户端发输入」维度（C# `AwaitInput`）。`inputType != Void` 时出现。 */
export interface AwaitInput {
  /** `InputType` 字符串（如 "IntValue"/"AnyKey"/"EnterKey"），见规格 §3。 */
  type: string;
}

/**
 * 「是否有一段有上界的等待」维度（C# `AwaitWait`）。`Timelimit >= 0` 时出现。
 *
 * `durationMs` 的三态（写死，不得再加第四个取值）：
 * - 缺省 `wait` 键 ⇒ **无限期**（`Timelimit < 0`，没有定时器）
 * - `durationMs === 0` ⇒ **零时长让路点**（无参 `AWAIT` / `AWAIT 0`）：语义是「下一帧立刻到」
 * - `durationMs > 0` ⇒ **定时等待**：带倒计时元数据
 *
 * 四个可选键都是 C# `WhenWritingNull` 语义：**仅 true / 有值时出现**，缺省即 false/无。
 * ⚠️ 「不可打断」= `interruptible` **键不出现**，不是字面量 false——前端据此判断有无
 * 「跳过」affordance（C# 侧 bool→bool? 的隐式提升会把 false 写出来，是已知陷阱）。
 */
export interface AwaitWait {
  durationMs: number;
  /** 可否被客户端输入打断（仅 true 时出现；`AWAIT` 置位、`TWAIT t,1` 不置位）。 */
  interruptible?: boolean | null;
  /** 是否显示倒计时（仅 true 时出现；ERB 可用 `TINPUT t,d,0` 要求不显示）。 */
  displayTime?: boolean | null;
  /** 超时文案（仅 `durationMs > 0` 时可能携带）。 */
  timeUpMessage?: string | null;
  /** 本帧是超时续跑驱动（仅 true 时出现；一次性，挂在 wait 而非顶层）。 */
  timedOut?: boolean | null;
}

/** 终态键（C# `AwaitEnded`）：与 `input` / `wait` **互斥**。`terminal` = "Quit" / "Error"。 */
export interface AwaitEnded {
  terminal: string;
}

/**
 * v12 唯一契约字段（C# `AwaitInfo`）。
 *
 * 硬规则（spec §1.2 规则 1）：`ended` 未出现时，`input` 与 `wait` **至少其一必现**
 * ——两者都空意味着这个等待「既不等输入也不等定时器」，没有任何东西能推进它。
 *
 * 该不变式只约束**流上的帧**（停稳点产出）。`GET /snapshot` 可能在「脚本还在跑」的
 * 瞬态被取到，那时两维都无值（`{}`），由仍保留的 `state` 字段区分。
 */
export interface AwaitInfo {
  input?: AwaitInput | null;
  wait?: AwaitWait | null;
  ended?: AwaitEnded | null;
}

/**
 * 结构化异常诊断（C# `ErrorInfo`）：`type`（完整类型名）+ `message` + 可选嵌套 `inner`。
 *
 * C# 端在脚本运行期异常帧（`errorInfo = ErrorInfo.From(ex)`，最深 4 层）携带，
 * 供 agent 一次性定位崩溃。**纯增量 nullable 字段**（`WhenWritingNull` ⇒ 非异常帧不出现），
 * 不升协议版本。
 *
 * issue 06 处置：此前 TS 端未声明、解析器也未消费（「C# 发得出、TS 接不住」的半接线头），
 * 本轮一并声明并解析。
 */
export interface ErrorInfo {
  type: string;
  message: string;
  inner?: ErrorInfo | null;
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
 * - `awaiting`：**v12 唯一契约字段（必填）**——与流上帧**同形**（同一个 C# 工厂、
 *   同一个 req/state），故晚加入者拿到的第一张画面不是另一套契约形状。
 *   TINPUT 计时元数据随 `awaiting.wait` 一并下发（顶层不再重复）。
 *   ⚠️ 快照可能落在「脚本还在跑」的瞬态（`GET /snapshot` 随时可调），那时两个维度都
 *   无值、序列化成 `{}`——这不是契约违规：`awaiting` 的不变式只约束**停稳点上产出的流帧**。
 *   客户端把 `{}` 当作「没有可推进的等待」处理，下一次 WS 帧会给出真实停稳点。
 * - `protocolVersion`：协议版本号（与 `TurnRecord.protocolVersion` 一致，当前 **12**）
 * - `generation`：快照时的 generation 号（v7），前端用于按钮过期检测
 *
 * `timedOut` **不在 DisplaySnapshot**——snapshot 表示"当前状态"而非"如何到达此状态"，
 * 晚加入者只关心"现在还有多久超时"，不关心上一帧是否刚超时。
 */
export interface DisplaySnapshot {
  lines: DisplayLine[];
  bgColor?: string | null;
  /** v8：背景图列表。depth 降序应用；省略/空 = 无背景图（C# 紧凑归一：空列表不进 JSON）。 */
  bgImages?: BgImageState[] | null;
  /** v12：唯一契约字段（必填；与流上帧同形）。见上方说明。 */
  awaiting: AwaitInfo;
  protocolVersion: number;
  generation: number;
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
 * WS 单回合帧（C# `TurnRecord`，ADR-0016 v6 加 timer 字段；v12 收缩到 `awaiting`）。
 *
 * 每个 WS Text 帧是一个 `TurnRecord` JSON。前端 `parseTurnRecord` 把原始 JSON 字符串
 * 解析为该类型。v5 后 `diff` 是唯一增量格式（`ops` 已废弃移除）。
 * 权威规格见 `docs/guides/turn-protocol.md`。
 *
 * - `awaiting`：**v12 唯一契约字段（必填，缺失即抛）**——「服务端此刻在等什么」
 *   （`input` / `wait` 两个正交维度 + 互斥的 `ended` 终态键）。
 * - `diff`：行级差异（null 表示本回合显示未变——例如纯状态切换）
 * - `error`：语义收窄为一句话：**非空 ⇒ 客户端上一次输入未被接受，画面与等待状态均未推进**
 * - `errorInfo`：结构化异常诊断（脚本运行期异常帧携带；非异常帧不出现）
 * - `protocolVersion`：协议版本（当前 **12**；仅首帧必带，后续帧省略）
 * - `generation`：本回合 generation 号（v7），前端用于按钮过期检测；v7 起每帧必带
 *
 * 🔒 **v12 起 wire 上只有这六个键**。旧顶层字段（`state` / `inputType` / `needValue` /
 * `timeLimit` / `displayTime` / `timeUpMessage` / `timedOut`）已全部退场：
 * 输入状态由 `awaiting.input` 表达、`needValue` 由客户端按 `input.type` 推出
 * （见 `lib/awaiting.ts` 的 `isValueInputType`）、TINPUT 计时元数据挂在 `awaiting.wait`。
 *
 * 注意：v5 协议中 `ops[]` 字段已废弃，本类型不包含该字段。C# `TestAdapter.ApplyOps`
 * 直接消费 `TurnOp[]`——TS 端做对称测试时也直接构造 `TurnOp[]`，不经 TurnRecord。
 */
export interface TurnRecord {
  /** v12：唯一契约字段（必填，缺失即抛）。 */
  awaiting: AwaitInfo;
  diff?: DisplayDiff | null;
  error?: string | null;
  /** 结构化异常诊断（C# `ErrorInfo`）：type/message/可选嵌套 inner。非异常帧为 null。 */
  errorInfo?: ErrorInfo | null;
  protocolVersion?: number | null;
  /** v7：本回合 generation 号，前端按钮过期检测。v7 每帧必带（缺失即抛）。 */
  generation: number;
}

/**
 * 前端内部可变显示状态（`DisplaySnapshot` 去掉协议元数据后的形状）。
 *
 * 与 C# `TestAdapter` 字段对称：`lines` / `bgColor` / `bgImages` / `awaiting`。
 * 不含 `protocolVersion` / `generation`——协议版本与回合号是 wire 元数据，不属于显示状态。
 *
 * 由 `applySnapshot` 从 `DisplaySnapshot` 重建，由 `applyDiff` 经增量更新。
 * 函数式纯度：所有 reducer 返回新对象，不修改入参。
 */
export interface DisplayState {
  lines: DisplayLine[];
  bgColor: string | null;
  /** v8：当前背景图（空数组 = 无背景图）。applySnapshot 全量重建 / applyDiff 整体替换。 */
  bgImages: BgImageState[];
  /**
   * v12：服务端此刻在等什么（唯一契约字段）。**恒非 null** —— 帧 / 快照直接取自 wire；
   * resetter 清为 `{}`（空闲态，也是快照瞬态形态）。
   */
  awaiting: AwaitInfo;
}

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
  // 空闲：三键皆无（既不等输入、也不等定时器、也不是终态）。
  awaiting: {},
};
