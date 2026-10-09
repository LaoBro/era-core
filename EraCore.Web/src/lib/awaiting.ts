import type { AwaitInfo } from '../types/protocol';

/**
 * v12 `awaiting` 的两个纯函数助手（issue 06）。
 *
 * 为什么要单独一个模块：`awaiting` 有**两个入口**——WS 帧（`parseTurnRecord`）与
 * 全量快照（`snapshotReducer.applySnapshot`，来自 `GET /snapshot`）。两处都必须在
 * v11 兼容期把旧顶层字段合成同一个容器，否则晚加入者和流上帧会拿到两套形状。
 *
 * ⚠️ `awaitingFromLegacyFields` 是**兼容垫片**，不是第二真相源：它只在「协议版本 < 12」
 * （即 wire 上没有 `awaiting`）时被调用。收缩阶段（issue 09）删掉旧顶层字段时，
 * 本函数与调用点一并删除——`awaiting` 从此只来自 wire 的解析结果。
 */

/**
 * 需要值输入的 `InputType` 集合（C# `InputRequest.NeedValue`：IntValue / StrValue）。
 *
 * 旧顶层字段 `needValue` 与 `awaiting.input.type` 是同源数据，故 issue 06 起
 * `DisplayState.needValue` 由后者派生，不再读帧的顶层 `needValue`。
 */
export function isValueInputType(type: string | null | undefined): boolean {
  return type === 'IntValue' || type === 'StrValue';
}

/** `awaitingFromLegacyFields` 的输入——就是 v12 前的旧顶层字段（全部可选，缺失即无）。 */
export interface LegacyAwaitFields {
  /** 旧顶层 `state`（"WaitInput"/"Quit"/"Error"/"Running" 等）。 */
  state?: string | null;
  /** 旧顶层 `inputType`（"Void" 表示不接受输入）。 */
  inputType?: string | null;
  /** 旧顶层 `timeLimit`：ADR-0016 只在 `Timelimit > 0` 时下发（故 0 时长让路点在此不可见）。 */
  timeLimit?: number | null;
  displayTime?: boolean | null;
  timeUpMessage?: string | null;
  /** 旧顶层 `timedOut`：一次性超时旗标。 */
  timedOut?: boolean | null;
}

/**
 * v11 兼容垫片：从旧顶层字段反向合成 `awaiting`（忠实于旧模型的表达能力）。
 *
 * 映射（与 v12 的 `AwaitInfo.Of` 对齐，但受旧 wire 的信息量限制）：
 * - `state ∈ {Quit, Error}` ⇒ 终态键 `ended{terminal}`（`input` / `wait` 保持缺省）
 * - `inputType` 非空且非 `"Void"` ⇒ `input{type}`
 * - `timeLimit > 0` ⇒ `wait{durationMs}`（+ displayTime / timeUpMessage / timedOut 仅在为真时带上）
 *   —— 旧 wire 在 `Timelimit == 0` 时把 `timeLimit` 写成 null，**零时长让路在 v11 里不可表达**，
 *   故这里合成不出 `durationMs:0`。这是垫片的固有损失，不影响 v12 帧。
 *
 * 非终态且两维皆无（例如 `state:"Running"`、空闲）时返回 `{}` ——
 * 与 `EMPTY_DISPLAY_STATE.awaiting` 同形，消费者按「既不等输入也不等定时器」处理。
 */
export function awaitingFromLegacyFields(legacy: LegacyAwaitFields): AwaitInfo {
  if (legacy.state === 'Quit' || legacy.state === 'Error') {
    return { ended: { terminal: legacy.state } };
  }

  const type = legacy.inputType ?? null;
  const wait = typeof legacy.timeLimit === 'number' && legacy.timeLimit > 0
    ? {
        durationMs: legacy.timeLimit,
        // C# WhenWritingNull 语义：仅真/有值时出现（false / 空串 ⇒ 键缺省）。
        ...(legacy.displayTime === true ? { displayTime: true } : {}),
        ...(legacy.timeUpMessage ? { timeUpMessage: legacy.timeUpMessage } : {}),
        ...(legacy.timedOut === true ? { timedOut: true } : {}),
      }
    : null;

  // 只带出现过的键（与 `parseAwaiting` 归一化一致）——空闲/非终态两维皆无时得到 `{}`，
  // 与 `EMPTY_DISPLAY_STATE.awaiting` 同形。
  const info: AwaitInfo = {};
  if (type !== null && type !== 'Void') info.input = { type };
  if (wait !== null) info.wait = wait;
  return info;
}
