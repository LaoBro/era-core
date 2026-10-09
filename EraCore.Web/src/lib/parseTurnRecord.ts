import type { TurnRecord, DisplayDiff, LineOp, DisplayLine, DisplayEntry, ButtonRef, PrintSegment, SegmentImage, SegmentShape, SegmentCrop, BgImageState, AwaitInfo, ErrorInfo } from '../types/protocol';
import { awaitingFromLegacyFields } from './awaiting';

/** v12 起 `awaiting` 成为唯一契约字段（`TurnRecord.CurrentProtocolVersion` 的 TS 镜像）。 */
const PROTOCOL_V12 = 12;

/**
 * 解析 WS 帧 JSON 字符串为 `TurnRecord`（issue 02；issue 06 接 `awaiting`）。
 *
 * C# `KestrelGameServer` 把每个 turn 序列化为单个 Text 帧下发——前端 `connection` store
 * 的 `onmessage` 收到原始字符串后经此函数得到结构化对象。
 *
 * 设计选择：
 * - **必填字段（缺失即抛）**：`generation`（v7 起每帧必带）+ `awaiting`（v12 起唯一契约字段）
 * - `awaiting` 的必填性按**协议版本**判定（见下），不按「`awaiting` 字段在不在」判定
 * - 旧顶层字段 `state` / `needValue` 自 issue 06 起**不再必填**（契约已换成 `awaiting`）
 * - 抛 `ParseTurnRecordError` 而非 `Error`——便于上层 catch 时区分协议错误与其他异常
 * - 未知 `LineOp.type` 抛错——与 C# `TestAdapter.ApplyDiff` 抛 `InvalidOperationException` 对称
 *
 * ## `awaiting` 的版本分支（spec §5.3 第 6 项）
 *
 * 分支只看**协议版本**：`有效版本 >= 12` ⇒ `awaiting` 必填、缺失即抛；否则（v11 兼容期）
 * 允许缺失，由旧顶层字段合成（`awaitingFromLegacyFields`）。**不得**写成「有没有某个字段」
 * 的启发式判断——那正是本轮要消灭的东西。
 *
 * 有效版本 = 帧自身的 `protocolVersion`（仅首帧带）**优先于** `protocolVersion` 形参
 * （调用方传入的 sticky 协议版本）——后续帧省略该字段，只能靠 sticky 才知道是 v12。
 *
 * @param rawJson WS 帧原始 JSON 字符串
 * @param protocolVersion 调用方（game store）已装好的 sticky 协议版本；默认 null（未知 ⇒ 按兼容期处理）
 * @returns 结构化 `TurnRecord` 对象（`awaiting` 恒非 null）
 * @throws {ParseTurnRecordError} JSON 语法错误 / 缺必填字段 / 未知 op 类型
 */
export function parseTurnRecord(rawJson: string, protocolVersion: number | null = null): TurnRecord {
  let obj: unknown;
  try {
    obj = JSON.parse(rawJson);
  } catch (e) {
    throw new ParseTurnRecordError(
      `JSON 语法错误: ${e instanceof Error ? e.message : String(e)}`,
    );
  }

  if (typeof obj !== 'object' || obj === null) {
    throw new ParseTurnRecordError(`期望顶层对象，得到 ${typeof obj}`);
  }

  const root = obj as Record<string, unknown>;

  // 必填字段校验（v7 起每帧必带 generation；缺失即抛，不接受降级）
  const generation = readIntRequired(root.generation, 'generation');

  // optional 字段
  const inputType = readStringOrNull(root.inputType, 'inputType');
  const state = readStringOrNull(root.state, 'state');
  const needValue = readBoolOrNull(root.needValue, 'needValue');
  const error = readStringOrNull(root.error, 'error');
  const protocolVersionOnFrame = readIntOrNull(root.protocolVersion, 'protocolVersion');
  // ADR-0016：timer 字段——timeLimit / displayTime / timeUpMessage nullable + WhenWritingNull
  const timeLimit = readIntOrNull(root.timeLimit, 'timeLimit');
  const displayTime = readBoolOrNull(root.displayTime, 'displayTime');
  const timeUpMessage = readStringOrNull(root.timeUpMessage, 'timeUpMessage');
  // ADR-0016：timedOut 非 nullable bool，默认 false（C# record 默认值）
  // 当 JSON 中省略该字段时（理论不会发生——C# WhenWritingNull 不抑制非 nullable bool），
  // 视为 false，与 C# 默认值对称。
  const timedOut = readBoolWithDefault(root.timedOut, 'timedOut', false);

  const diff = root.diff === undefined || root.diff === null ? null : parseDiff(root.diff);
  // 诊断：结构化异常（C# `ErrorInfo`，非异常帧不出现）。纯增量字段，不升协议版本。
  const errorInfo =
    root.errorInfo === undefined || root.errorInfo === null
      ? null
      : parseErrorInfo(root.errorInfo, 'errorInfo');

  // v12 唯一契约字段。版本判定用「帧自带版本 ?? sticky」——子序列帧省略 protocolVersion。
  const effectiveVersion = protocolVersionOnFrame ?? protocolVersion;
  const awaiting = parseAwaiting(root.awaiting, effectiveVersion != null && effectiveVersion >= PROTOCOL_V12, {
    state,
    inputType,
    timeLimit,
    displayTime,
    timeUpMessage,
    timedOut,
  });

  return {
    awaiting,
    diff,
    error,
    errorInfo,
    protocolVersion: protocolVersionOnFrame,
    generation,
    state,
    inputType,
    needValue,
    timeLimit,
    displayTime,
    timeUpMessage,
    timedOut,
  };
}

/**
 * 解析 `awaiting`（v12 唯一契约字段）。
 *
 * - 字段缺失：v12 ⇒ 抛错（契约字段不得降级）；v11 兼容期 ⇒ 由旧顶层字段合成
 * - 字段存在：严格按「两个正交维度 + 互斥终态键」校验形状
 *
 * 返回的容器**只带出现过的键**（缺失维度不写成 `null`），故空闲/瞬态快照得到 `{}`
 * ——与 `EMPTY_DISPLAY_STATE.awaiting` 同形，消费者按「既不等输入也不等定时器」处理。
 */
function parseAwaiting(
  raw: unknown,
  required: boolean,
  legacy: Parameters<typeof awaitingFromLegacyFields>[0],
): AwaitInfo {
  if (raw === undefined || raw === null) {
    if (required) {
      throw new ParseTurnRecordError(
        '字段 awaiting 缺失或为 null（v12 起为必填契约字段，缺失即抛）',
      );
    }
    return awaitingFromLegacyFields(legacy);
  }
  if (typeof raw !== 'object' || Array.isArray(raw)) {
    throw new ParseTurnRecordError(`awaiting 期望对象，得到 ${Array.isArray(raw) ? 'array' : typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;

  const info: AwaitInfo = {};
  if (obj.input !== undefined && obj.input !== null) {
    info.input = parseAwaitInput(obj.input, 'awaiting.input');
  }
  if (obj.wait !== undefined && obj.wait !== null) {
    info.wait = parseAwaitWait(obj.wait, 'awaiting.wait');
  }
  if (obj.ended !== undefined && obj.ended !== null) {
    info.ended = parseAwaitEnded(obj.ended, 'awaiting.ended');
  }
  return info;
}

/** 解析 `awaiting.input` ——「是否期待客户端发输入」维度。`type` 必填 string。 */
function parseAwaitInput(raw: unknown, path: string): NonNullable<AwaitInfo['input']> {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  return { type: readStringRequired(obj.type, `${path}.type`) };
}

/**
 * 解析 `awaiting.wait` ——「是否有一段有上界的等待」维度。`durationMs` 必填 int
 * （三态：缺省键 = 无限期 / `0` = 零时长让路 / `>0` = 定时等待）；
 * 其余四个键可选，语义是 C# `WhenWritingNull`（仅 true / 有值时出现）。
 */
function parseAwaitWait(raw: unknown, path: string): NonNullable<AwaitInfo['wait']> {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  return {
    durationMs: readIntRequired(obj.durationMs, `${path}.durationMs`),
    interruptible: readBoolOrNull(obj.interruptible, `${path}.interruptible`),
    displayTime: readBoolOrNull(obj.displayTime, `${path}.displayTime`),
    timeUpMessage: readStringOrNull(obj.timeUpMessage, `${path}.timeUpMessage`),
    timedOut: readBoolOrNull(obj.timedOut, `${path}.timedOut`),
  };
}

/** 解析 `awaiting.ended` —— 互斥终态键。`terminal` 必填 string（"Quit" / "Error"）。 */
function parseAwaitEnded(raw: unknown, path: string): NonNullable<AwaitInfo['ended']> {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  return { terminal: readStringRequired(obj.terminal, `${path}.terminal`) };
}

/**
 * 解析 `errorInfo`（C# `ErrorInfo`）：`type`（完整类型名）+ `message` 必填，
 * `inner` 可选且递归同形（C# 最深 4 层，这里不再设限——信源已封顶）。
 */
function parseErrorInfo(raw: unknown, path: string): ErrorInfo {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  const inner = obj.inner === undefined || obj.inner === null
    ? null
    : parseErrorInfo(obj.inner, `${path}.inner`);
  const info: ErrorInfo = {
    type: readStringRequired(obj.type, `${path}.type`),
    message: readStringRequired(obj.message, `${path}.message`),
  };
  if (inner) info.inner = inner;
  return info;
}

/**
 * 解析 `DisplayDiff`。`lineOps` 必填且必须为数组；`bgColor` / `bgImages` optional。
 */
function parseDiff(raw: unknown): DisplayDiff {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`diff 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (!Array.isArray(obj.lineOps)) {
    throw new ParseTurnRecordError(`diff.lineOps 缺失或非数组（得到 ${typeof obj.lineOps}）`);
  }
  return {
    lineOps: obj.lineOps.map((op, i) => parseLineOp(op, i)),
    bgColor: readStringOrNull(obj.bgColor, 'diff.bgColor'),
    // v8：背景图增量（C# 紧凑归一——空列表省略，缺失即无变更）。null/undefined 均视为未提供。
    bgImages: obj.bgImages === undefined || obj.bgImages === null
      ? undefined
      : parseBgImages(obj.bgImages),
  };
}

/**
 * v8：解析 `bgImages` 数组。元素必须为对象且含 src/depth/opacity。
 */
function parseBgImages(raw: unknown): BgImageState[] {
  if (!Array.isArray(raw)) {
    throw new ParseTurnRecordError(`diff.bgImages 期望数组，得到 ${typeof raw}`);
  }
  return raw.map((item, i) => parseBgImageState(item, `diff.bgImages[${i}]`));
}

/**
 * v8：解析单个背景图状态——src 必填 string；depth 必填 int；opacity 必填 number。
 */
function parseBgImageState(raw: unknown, path: string): BgImageState {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.src !== 'string') {
    throw new ParseTurnRecordError(`${path}.src 缺失或非 string`);
  }
  return {
    src: obj.src,
    depth: readIntRequired(obj.depth, `${path}.depth`),
    opacity: readNumberRequired(obj.opacity, `${path}.opacity`),
  };
}

/**
 * 解析单个 `LineOp`。鉴别字段 `type` 必填；其余字段按 type 校验。
 * 与 C# `LineOpConverter` 反序列化不支持的差别——前端必须能反序列化（消费方）。
 */
function parseLineOp(raw: unknown, index: number): LineOp {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`lineOps[${index}] 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  const type = obj.type;
  switch (type) {
    case 'append':
      if (!Array.isArray(obj.newLines)) {
        throw new ParseTurnRecordError(`lineOps[${index}].newLines 缺失或非数组`);
      }
      return { type: 'append', newLines: obj.newLines.map((l, i) => parseDisplayLine(l, `lineOps[${index}].newLines[${i}]`)) };
    case 'clear_line_diff':
      return { type: 'clear_line_diff', clearCount: readIntRequired(obj.clearCount, `lineOps[${index}].clearCount`) };
    case 'clear_screen':
      return { type: 'clear_screen' };
    case 'shift_head':
      return { type: 'shift_head', count: readIntRequired(obj.count, `lineOps[${index}].count`) };
    default:
      throw new ParseTurnRecordError(`lineOps[${index}] 未知 type="${String(type)}"`);
  }
}

/**
 * 解析 `DisplayLine`。`entries` 必填数组；`align` 可空；`isLineEnd` 必填 bool。
 */
function parseDisplayLine(raw: unknown, path: string): DisplayLine {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (!Array.isArray(obj.entries)) {
    throw new ParseTurnRecordError(`${path}.entries 缺失或非数组`);
  }
  if (typeof obj.isLineEnd !== 'boolean') {
    throw new ParseTurnRecordError(`${path}.isLineEnd 缺失或非 boolean`);
  }
  const align = readAlignOrNull(obj.align, `${path}.align`);
  return {
    entries: obj.entries.map((e, i) => parseDisplayEntry(e, `${path}.entries[${i}]`)),
    align,
    isLineEnd: obj.isLineEnd,
  };
}

/**
 * 解析 `DisplayEntry`。`segments` 必填数组；`button` 可空。
 */
function parseDisplayEntry(raw: unknown, path: string): DisplayEntry {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (!Array.isArray(obj.segments)) {
    throw new ParseTurnRecordError(`${path}.segments 缺失或非数组`);
  }
  return {
    segments: obj.segments.map((s, i) => parsePrintSegment(s, `${path}.segments[${i}]`)),
    button: obj.button === undefined || obj.button === null ? null : parseButtonRef(obj.button, `${path}.button`),
  };
}

/**
 * 解析 `PrintSegment`。`text` 必填；`color`/`bold`/`italic`/`fontname` 可空；
 * `image`/`shape` 可选（v8，issue 02）；`underline` 可选（v10）、`strikeout` 可选（v11）。
 *
 * 2026-08-06 修复：此前只按 v7 五字段解析，image/shape 在 JSON→对象转换时被丢弃——
 * SegmentRenderer 的 `v-if="seg.image"` 恒 false，图片/背景「降级为文本」
 * （渲染 C# 侧保留的 AltText markup 字符串）。此处补全两个字段的解析与形状校验。
 */
function parsePrintSegment(raw: unknown, path: string): PrintSegment {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.text !== 'string') {
    throw new ParseTurnRecordError(`${path}.text 缺失或非 string`);
  }
  return {
    text: obj.text,
    color: readStringOrNull(obj.color, `${path}.color`),
    bold: readBoolOrNull(obj.bold, `${path}.bold`),
    italic: readBoolOrNull(obj.italic, `${path}.italic`),
    fontname: readStringOrNull(obj.fontname, `${path}.fontname`),
    underline: readBoolOrNull(obj.underline, `${path}.underline`),
    strikeout: readBoolOrNull(obj.strikeout, `${path}.strikeout`),
    image: obj.image === undefined || obj.image === null
      ? undefined
      : parseSegmentImage(obj.image, `${path}.image`),
    shape: obj.shape === undefined || obj.shape === null
      ? undefined
      : parseSegmentShape(obj.shape, `${path}.shape`),
  };
}

/**
 * v8：解析图片 segment——src 必填 string；srcb/srcm 可空 string；width/height/ypos 必填 int。
 * v9（issue 07）：crop 可选——{x,y,imgWidth,imgHeight} 全必填 int。
 */
function parseSegmentImage(raw: unknown, path: string): SegmentImage {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.src !== 'string') {
    throw new ParseTurnRecordError(`${path}.src 缺失或非 string`);
  }
  return {
    src: obj.src,
    srcb: readStringOrNull(obj.srcb, `${path}.srcb`),
    srcm: readStringOrNull(obj.srcm, `${path}.srcm`),
    width: readIntRequired(obj.width, `${path}.width`),
    height: readIntRequired(obj.height, `${path}.height`),
    ypos: readIntRequired(obj.ypos, `${path}.ypos`),
    crop: obj.crop === undefined || obj.crop === null
      ? undefined
      : parseSegmentCrop(obj.crop, `${path}.crop`),
  };
}

/**
 * v9：解析裁切几何——x/y/imgWidth/imgHeight 全必填 int（C# SegmentCrop 非 nullable）。
 */
function parseSegmentCrop(raw: unknown, path: string): SegmentCrop {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  return {
    x: readIntRequired(obj.x, `${path}.x`),
    y: readIntRequired(obj.y, `${path}.y`),
    imgWidth: readIntRequired(obj.imgWidth, `${path}.imgWidth`),
    imgHeight: readIntRequired(obj.imgHeight, `${path}.imgHeight`),
  };
}

/**
 * v8：解析形状 segment——type 必填且必须为 'rect'（C# 当前仅 rect 一种）；
 * x/y/width/height 必填 int；color 必填 string。
 */
function parseSegmentShape(raw: unknown, path: string): SegmentShape {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (obj.type !== 'rect') {
    throw new ParseTurnRecordError(`${path}.type 期望 "rect"，得到 ${JSON.stringify(obj.type)}`);
  }
  return {
    type: 'rect',
    x: readIntRequired(obj.x, `${path}.x`),
    y: readIntRequired(obj.y, `${path}.y`),
    width: readIntRequired(obj.width, `${path}.width`),
    height: readIntRequired(obj.height, `${path}.height`),
    color: readStringRequired(obj.color, `${path}.color`),
  };
}

/**
 * 解析 `ButtonRef`。`value` 必填（number|string）；`isInteger`/`generation` 必填；
 * `col`/`width` 可空 int。
 */
function parseButtonRef(raw: unknown, path: string): ButtonRef {
  if (typeof raw !== 'object' || raw === null) {
    throw new ParseTurnRecordError(`${path} 期望对象，得到 ${typeof raw}`);
  }
  const obj = raw as Record<string, unknown>;
  if (typeof obj.value !== 'number' && typeof obj.value !== 'string') {
    throw new ParseTurnRecordError(`${path}.value 缺失或非 number/string`);
  }
  if (typeof obj.isInteger !== 'boolean') {
    throw new ParseTurnRecordError(`${path}.isInteger 缺失或非 boolean`);
  }
  return {
    value: obj.value,
    isInteger: obj.isInteger,
    generation: readIntRequired(obj.generation, `${path}.generation`),
    col: readIntOrNull(obj.col, `${path}.col`),
    width: readIntOrNull(obj.width, `${path}.width`),
  };
}

// ---------- 字段读取辅助 ----------

function readStringOrNull(v: unknown, path: string): string | null {
  if (v === undefined || v === null) return null;
  if (typeof v !== 'string') {
    throw new ParseTurnRecordError(`${path} 期望 string|null，得到 ${typeof v}`);
  }
  return v;
}

function readStringRequired(v: unknown, path: string): string {
  if (typeof v !== 'string') {
    throw new ParseTurnRecordError(`${path} 缺失或非 string（得到 ${typeof v}）`);
  }
  return v;
}

function readNumberRequired(v: unknown, path: string): number {
  if (typeof v !== 'number' || !Number.isFinite(v)) {
    throw new ParseTurnRecordError(`${path} 缺失或非 number（得到 ${typeof v}）`);
  }
  return v;
}

function readIntOrNull(v: unknown, path: string): number | null {
  if (v === undefined || v === null) return null;
  if (typeof v !== 'number' || !Number.isInteger(v)) {
    throw new ParseTurnRecordError(`${path} 期望 int|null，得到 ${typeof v}`);
  }
  return v;
}

function readIntRequired(v: unknown, path: string): number {
  if (typeof v !== 'number' || !Number.isInteger(v)) {
    throw new ParseTurnRecordError(`${path} 缺失或非 int（得到 ${typeof v}）`);
  }
  return v;
}

function readBoolOrNull(v: unknown, path: string): boolean | null {
  if (v === undefined || v === null) return null;
  if (typeof v !== 'boolean') {
    throw new ParseTurnRecordError(`${path} 期望 boolean|null，得到 ${typeof v}`);
  }
  return v;
}

/**
 * ADR-0016：读非 nullable bool 字段，省略时返回 default。
 *
 * 用于 `timedOut`——C# record 默认 false，`JsonSerializerOptions.WhenWritingNull`
 * 不抑制非 nullable bool，故正常情况下 server 每帧都发该字段。
 * 防御性允许省略（与 v5 兼容性 / 测试构造方便），省略时视为 false。
 */
function readBoolWithDefault(v: unknown, path: string, defaultValue: boolean): boolean {
  if (v === undefined || v === null) return defaultValue;
  if (typeof v !== 'boolean') {
    throw new ParseTurnRecordError(`${path} 期望 boolean，得到 ${typeof v}`);
  }
  return v;
}

function readAlignOrNull(v: unknown, path: string): 'left' | 'center' | 'right' | null {
  if (v === undefined || v === null) return null;
  if (v === 'left' || v === 'center' || v === 'right') return v;
  throw new ParseTurnRecordError(`${path} 期望 "left"|"center"|"right"|null，得到 ${JSON.stringify(v)}`);
}

/**
 * 解析错误。上层 catch 时用 `instanceof ParseTurnRecordError` 区分协议错误与其他异常。
 */
export class ParseTurnRecordError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'ParseTurnRecordError';
  }
}
