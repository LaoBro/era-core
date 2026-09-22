/**
 * inputRouting——"点击→行为"路由判定，抽成纯函数供 TerminalDisplay 与单测复用。
 *
 * Emuera 输入语义（与 CLI / WinForms 一致）：
 * - EnterKey / AnyKey（任意键 / 回车等待）：屏幕上按钮是**惰性**的——点击任何位置
 *   （含按钮区域）都只"推进"（发送空输入），不提交按钮 value；否则游戏会收到真实输入
 *   并多回显一行输入（曾与 CLI/WinForms 行为不一致）。
 * - 其余等待态（IntValue/StrValue/AnyValue/IntButton/StrButton）：按钮是输入手段，
 *   点击按钮提交其 value；空白区点击不推进（输入由按钮 / 输入栏承担）。
 */

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
 * - EnterKey / AnyKey 等待态：按钮是惰性的，点击只推进，不提交 value。
 */
export function isLiveButton(ctx: {
  state: string;
  inputType: string | null;
  buttonGeneration: number;
  currentTurnGeneration: number;
}): boolean {
  if (ctx.state !== 'WaitInput') return false;
  if (isAnyKeyInput(ctx.inputType)) return false;
  return ctx.buttonGeneration === ctx.currentTurnGeneration;
}

/** 按钮点击提交判定入参——TerminalDisplay.onButtonClick 的守卫上下文。 */
export interface ButtonClickContext {
  connected: boolean;
  /** displayState.state，'WaitInput' 才可提交。 */
  state: string;
  /** displayState.inputType。 */
  inputType: string | null;
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
 * 1. state !== 'WaitInput' 不提交；
 * 2. 按钮 generation 与当前回合不一致（过期按钮）不提交；
 * 3. inputInFlight 不提交。
 */
export function shouldSubmitButtonValue(ctx: ButtonClickContext): boolean {
  if (!ctx.connected) return false;
  if (ctx.state !== 'WaitInput') return false;
  if (isAnyKeyInput(ctx.inputType)) return false;
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
  state: string;
  inputType: string | null;
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
 * - 仅 WaitInput + EnterKey/AnyKey 推进；
 * - connected + 非 inputInFlight。
 */
export function shouldAdvanceOnTerminalClick(ctx: TerminalClickContext): boolean {
  if (ctx.isButton && !isAnyKeyInput(ctx.inputType)) return false;
  if (!ctx.isStickyToBottom) return false;
  if (ctx.state !== 'WaitInput') return false;
  if (!isAnyKeyInput(ctx.inputType)) return false;
  if (!ctx.connected) return false;
  if (ctx.inputInFlight) return false;
  if (ctx.canInput === false) return false;
  return true;
}
