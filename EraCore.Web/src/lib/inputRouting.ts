/**
 * inputRouting——"点击→行为"路由判定，抽成纯函数供 TerminalDisplay 与单测复用。
 *
 * Emuera 输入语义（与 CLI / WinForms 一致）：
 * - EnterKey / AnyKey（任意键 / 回车等待）：屏幕上按钮是**惰性**的——点击任何位置
 *   （含按钮区域）都只"推进"（发送空输入），不提交按钮 value；否则游戏会收到真实输入
 *   并多回显一行输入（曾与 CLI/WinForms 行为不一致）。
 * - 其余等待态（IntValue/StrValue/AnyValue/IntButton/StrButton）：按钮是输入手段，
 *   点击按钮提交其 value；空白区点击不推进（输入由按钮 / 输入栏承担）。
 *
 * ## issue 07：判据从「引擎状态 + 输入种类反向推测」改为直接读契约 `awaiting`
 *
 * 改动前第一条守卫是 `state !== 'WaitInput'`——那是在**猜引擎此刻的模式**（"WaitInput 还包着
 * 定时等待与让路两类"）。`awaiting.input` 非空就直接表达了「服务端此刻在等你发输入」这一件事，
 * 于是三重守卫塌缩为两重：「有 input 维度」 + 「输入种类」 + 「代次匹配」。
 *
 * 语义增益（顺带修掉的一类错判）：`state=WaitInput && inputType=Void`（`AWAIT` / `TWAIT`
 * 定时等待、无参 `AWAIT` 让路）在新判据下**没有** `input` 维度 ⇒ 按钮既不可提交也不撑触控盒；
 * 旧判据下它们与真正的输入等待无法区分。
 */

import type { AwaitInfo } from '../types/protocol';

/** inputType 是否为"任意键/回车"推进等待态（C# `InputType.ToString()` 值）。 */
export function isAnyKeyInput(inputType: string | null | undefined): boolean {
  return inputType === 'EnterKey' || inputType === 'AnyKey';
}

/**
 * 按钮当前是否「有效」（可被玩家点到并真正生效）。
 *
 * 与 `shouldSubmitButtonValue` 的差别：本函数只判按钮自身是否存活，不含 `inputInFlight`
 * 乐观锁（那是一次点击期间的一过性状态）与连接态——排版关心的是"这一行要不要为其撑高"，
 * 与当前是否正在提交无关。
 *
 * 无效的两类来源，都会让关掉它们的排版收益为零：
 * - 历史回合残留：`generation` 落后于当前回合（旧画面里的按钮点了也不生效）；
 * - 服务端不期待输入（`awaiting.input` 缺省：定时等待 / 让路 / 终态）或期待的是
 *   EnterKey / AnyKey（按钮惰性，点击只推进，不提交 value）。
 */
export function isLiveButton(ctx: {
  awaiting: AwaitInfo;
  buttonGeneration: number;
  currentTurnGeneration: number;
}): boolean {
  if (!ctx.awaiting.input) return false;
  if (isAnyKeyInput(ctx.awaiting.input.type)) return false;
  return ctx.buttonGeneration === ctx.currentTurnGeneration;
}

/** 按钮点击提交判定入参——TerminalDisplay.onButtonClick 的守卫上下文。 */
export interface ButtonClickContext {
  connected: boolean;
  /** v12 唯一契约字段：`input` 非空才可提交（原来读 `state === 'WaitInput'`）。 */
  awaiting: AwaitInfo;
  /** 被点击按钮携带的 generation。 */
  buttonGeneration: number;
  /** 当前回合 generation——过期按钮判定。 */
  currentTurnGeneration: number;
  /** 乐观锁——同一回合内防重复点击。 */
  inputInFlight: boolean;
  /** 用户是否可输入。agent 持有时为 false；省略视为 true（兼容旧调用）。 */
  canInput?: boolean;
}

/**
 * 判断点击按钮是否应提交其 value。
 *
 * 守卫（顺序与行为语义对应）：
 * 0. EnterKey/AnyKey 下按钮惰性——不提交，点击落到终端推进路径（避免游戏多回显一行）；
 * 1. `awaiting.input` 缺省（服务端不期待输入）不提交；
 * 2. 按钮 generation 与当前回合不一致（过期按钮）不提交；
 * 3. inputInFlight 不提交。
 */
export function shouldSubmitButtonValue(ctx: ButtonClickContext): boolean {
  if (!ctx.connected) return false;
  if (!ctx.awaiting.input) return false;
  if (isAnyKeyInput(ctx.awaiting.input.type)) return false;
  if (ctx.buttonGeneration !== ctx.currentTurnGeneration) return false;
  if (ctx.inputInFlight) return false;
  if (ctx.canInput === false) return false;
  return true;
}

/** 终端点击推进判定入参——TerminalDisplay.onTerminalClick 的守卫上下文。 */
export interface TerminalClickContext {
  /** 点击目标是否落在按钮区域（`e.target.closest('.term-btn')`）。 */
  isButton: boolean;
  /** 是否贴底——翻看历史（false）时不推进。 */
  isStickyToBottom: boolean;
  /** v12 唯一契约字段：`input.type` 为 EnterKey/AnyKey 时才推进。 */
  awaiting: AwaitInfo;
  connected: boolean;
  inputInFlight: boolean;
  /** 用户是否可输入。agent 持有时为 false；省略视为 true。 */
  canInput?: boolean;
}

/**
 * 判断点击终端（任意位置）是否应推进游戏（发送空输入）。
 *
 * 规则：
 * - 按钮区域：非 EnterKey/AnyKey 态跳过（按钮有独立 @click 处理）；EnterKey/AnyKey 态
 *   按钮惰性、点击同样推进——与 `shouldSubmitButtonValue` 的守卫 0 配套，保证该态下
 *   按钮点击既不提交 value 也不成为死区；
 * - sticky 守卫：isStickyToBottom=false（翻看历史）时不推进；
 * - 仅 `awaiting.input.type` 为 EnterKey/AnyKey 时推进（定时等待 / 让路 / 终态都不推进）；
 * - connected + 非 inputInFlight。
 */
export function shouldAdvanceOnTerminalClick(ctx: TerminalClickContext): boolean {
  const type = ctx.awaiting.input?.type;
  if (ctx.isButton && !isAnyKeyInput(type)) return false;
  if (!ctx.isStickyToBottom) return false;
  if (!isAnyKeyInput(type)) return false;
  if (!ctx.connected) return false;
  if (ctx.inputInFlight) return false;
  if (ctx.canInput === false) return false;
  return true;
}
