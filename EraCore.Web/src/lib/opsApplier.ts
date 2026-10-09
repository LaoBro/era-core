import type { DisplayState, DisplayLine, DisplayEntry, TurnOp, DisplayDiff, BgImageState } from '../types/protocol';

/**
 * 应用增量 ops 更新状态（issue 02）。
 *
 * 与 C# `TestAdapter.ApplyOps`（Emuera.Headless.Tests/TestAdapter.cs:97）对称：
 * - `print` → 向"当前行"（最后一行且 isLineEnd=false）追加 entry；无当前行则新建
 * - `newline` → 终止当前行（设 align + isLineEnd=true）；当前行已终止或空则产生空行
 * - `clearline` (n) → 从末尾删除 min(n, length) 行
 * - `clear` → 清空全部行 + 重置 bgColor=null
 * - `set_bg` → 更新 bgColor
 * - `set_bg_image` / `remove_bg_image` / `clear_bg_image`（v8）→ 更新 bgImages
 *   （WinForms 语义：set 追加、remove 移除首个同名、clear 全清）
 *
 * 关键状态机：PrintOp 总是追加到"当前行"——最后一行且 isLineEnd=false。
 * NewLineOp 终止当前行。这与 ConsolePrintManager 的 EmitPrintOps + NewLine 行为对齐。
 *
 * 函数式纯度：返回新对象，不修改入参 `state` 与 `ops`。内部用 `lines.slice()` 做可变
 * 影子数组提升 perf——但任何要修改的行都会先 `{...line}` 浅拷贝。entry 中的 segments
 * 与 button 也深拷贝（与 `applySnapshot` 的 `copyEntry` 一致——TS 对象默认可变，深拷贝
 * 保证 caller 后续 mutate 不影响内部状态）。
 *
 * awaiting 不被 applyOps 修改——它由外层 TurnRecord 携带，不是 ops。
 * 与 C# TestAdapter 行为对称（T_full_roundtrip_preserves_awaiting_from_snapshot）。
 *
 * @throws {Error} 未知 op 类型（与 C# `InvalidOperationException` 对称）
 */
export function applyOps(state: DisplayState, ops: TurnOp[]): DisplayState {
  // 可变影子数组——只在本函数内修改，不影响外部 state.lines
  const lines: DisplayLine[] = state.lines.slice();
  let bgColor: string | null = state.bgColor;
  const bgImages: BgImageState[] = state.bgImages.slice();

  for (const op of ops) {
    switch (op.type) {
      case 'print': {
        ensureCurrentLine(lines);
        const last = lines[lines.length - 1];
        // 深拷贝 segments/button（与 applySnapshot.copyEntry 一致——TS 对象默认可变）
        const newEntry: DisplayEntry = {
          segments: op.segments.map((s) => ({ ...s })),
          button: op.button ? { ...op.button } : null,
        };
        // 浅拷贝当前行 + 追加 entry，避免修改原 state 中的 line 对象
        lines[lines.length - 1] = {
          ...last,
          entries: [...last.entries, newEntry],
        };
        break;
      }
      case 'newline': {
        const align = op.align ?? null;
        if (lines.length === 0 || lines[lines.length - 1].isLineEnd) {
          // 空行后立即 newline，或连续 newline——产生一个空行
          lines.push({ entries: [], align, isLineEnd: true });
        } else {
          // 终止当前行——直接覆盖 align（与 C# Lines[^1].Align = newline.align 对称）
          const last = lines[lines.length - 1];
          lines[lines.length - 1] = { ...last, align, isLineEnd: true };
        }
        break;
      }
      case 'clearline': {
        const n = Math.min(op.n, lines.length);
        if (n > 0) lines.length = lines.length - n; // 截尾
        break;
      }
      case 'clear':
        lines.length = 0;
        bgColor = null;
        break;
      case 'set_bg':
        bgColor = op.color;
        break;
      case 'set_bg_image':
        bgImages.push({ src: op.src, depth: op.depth, opacity: op.opacity });
        break;
      case 'remove_bg_image': {
        // WinForms 语义：同名多份时移除最前一份（与 C# ConsoleStateData 对称）
        const idx = bgImages.findIndex((bg) => bg.src === op.src);
        if (idx >= 0) bgImages.splice(idx, 1);
        break;
      }
      case 'clear_bg_image':
        bgImages.length = 0;
        break;
      default:
        throw new Error(`Unknown op type: ${(op as { type: string }).type}`);
    }
  }

  return {
    lines,
    bgColor,
    bgImages,
    // v12：awaiting 不是 ops 载荷，原样透传（由外层 TurnRecord 携带）。
    awaiting: state.awaiting,
  };
}

/**
 * 归约产出的高级变更信号——lineOps 中会影响滚动位置的结构性变化摘要。
 *
 * - `shift_head`：头部截断 count 行（MaxLog 滚动）——非贴底查看历史时需把
 *   scrollTop 上移 count × rowHeight 保持视觉位置（scrollTop -= count * rowHeight）。
 * - `clear_screen`：全清——视为新画面，强制回到底部。
 *
 * 单值 union：C# `DisplayState.ComputeDiff` 保证 `shift_head` 与 `clear_screen` 互斥
 * （TurnRecord.cs「与 ClearScreenOp 互斥：全清已无头部可截」），故无结构性变化时返回 null。
 *
 * **注意：signal 是 TS 侧独有派生物**——C# `TestAdapter.ApplyDiff`
 * （Emuera.Headless.Tests/TestAdapter.cs:72）是 void（可变实例），不产出信号；
 * 前端从 lineOps 分类摘要得出，用于驱动滚动副作用（归约与滚动后果同处——deep seam）。
 */
export type DisplaySignal =
  | { type: 'shift_head'; count: number }
  | { type: 'clear_screen' };

/**
 * 应用 `DisplayDiff`（v5 协议增量 + shift_head 扩展）更新状态（issue 02）。
 *
 * 与 C# `TestAdapter.ApplyDiff`（Emuera.Headless.Tests/TestAdapter.cs:72）对称（状态部分）：
 * - `append` → 追加 newLines（diff 已按行结构化，逐条转为内部 DisplayLine）
 * - `clear_line_diff` (clearCount) → 从末尾删除 min(clearCount, length) 行
 * - `clear_screen` → 清空全部行
 * - `shift_head` (count) → 从头部删除 min(count, length) 行（MaxLog 滚动场景）
 * - diff.bgColor 非空 → 更新 bgColor
 *
 * 与 `applyOps` 的差别：applyOps 消费引擎内部 `TurnOp[]`（print/newline/...），
 * applyDiff 消费对外 `LineOp[]`（append/clear_line_diff/clear_screen/shift_head）。
 * **WS 帧的 `TurnRecord.diff.lineOps` 即 `LineOp[]`——前端实际消费 WS 流靠此函数**，
 * applyOps 主要为与 C# TestAdapter 的 ApplyOps 测试对称（内部 op 流）。
 *
 * 应用顺序：C# `DisplayState.ComputeDiff` 按 shift_head → clear_line_diff → append
 * 顺序产出 lineOps（clear_screen 与 shift_head 互斥）。本函数按数组顺序应用即可。
 *
 * 函数式纯度：与 applyOps 一致——返回新对象，深拷贝 segments/button。
 *
 * awaiting 不被 applyDiff 修改——它由外层 TurnRecord 携带。
 *
 * @returns `{ state, signal }`——signal 为 null 表示本帧无结构性变化（不影响滚动位置）；
 *   有 clear_screen 时返回 `{type:'clear_screen'}`，否则有 shift_head 时返回
 *   `{type:'shift_head', count}`（确定性优先级，防御协议不可能的双 op 同帧）。
 * @throws {Error} 未知 LineOp 类型
 */
export function applyDiff(
  state: DisplayState,
  diff: DisplayDiff,
): { state: DisplayState; signal: DisplaySignal | null } {
  const lines: DisplayLine[] = state.lines.slice();
  let bgColor: string | null = state.bgColor;

  // 先扫描结构性变化——在应用 op 前产出信号（不能事后 watch(lines.length) 检测全清：
  // applyDiff 单 tick 内顺序应用 clear_screen + append，响应式只观察到最终 length，
  // 突变到 0 的中间态不可见）。确定性优先级：有 clear_screen 即优先（协议保证二者互斥）。
  let signal: DisplaySignal | null = null;
  let hasClearScreen = false;
  let shiftHeadCount = 0;
  for (const op of diff.lineOps) {
    if (op.type === 'clear_screen') hasClearScreen = true;
    else if (op.type === 'shift_head') shiftHeadCount += op.count;
  }
  if (hasClearScreen) signal = { type: 'clear_screen' };
  else if (shiftHeadCount > 0) signal = { type: 'shift_head', count: shiftHeadCount };

  for (const op of diff.lineOps) {
    switch (op.type) {
      case 'append':
        // 深拷贝每行 + 每个 entry（与 applySnapshot.copyLine/copyEntry 一致）
        for (const line of op.newLines) {
          lines.push({
            entries: line.entries.map((e) => ({
              segments: e.segments.map((s) => ({ ...s })),
              button: e.button ? { ...e.button } : null,
            })),
            align: line.align ?? null,
            isLineEnd: line.isLineEnd,
          });
        }
        break;
      case 'clear_line_diff': {
        const n = Math.min(op.clearCount, lines.length);
        if (n > 0) lines.length = lines.length - n;
        break;
      }
      case 'clear_screen':
        lines.length = 0;
        break;
      case 'shift_head': {
        // 头部删除——与 C# TestAdapter.ApplyDiff 的 `Lines.RemoveRange(0, count)` 对称。
        // count 超量时钳制到 lines.length（防御性，C# 端 ShiftHeadLineOp.count 已钳制）。
        const removeCount = Math.min(op.count, lines.length);
        if (removeCount > 0) lines.splice(0, removeCount);
        break;
      }
      default:
        throw new Error(`Unknown LineOp type: ${(op as { type: string }).type}`);
    }
  }

  // diff.bgColor 非空时更新（null 表示未变，保留原 bgColor）
  if (diff.bgColor != null) {
    bgColor = diff.bgColor;
  }

  // v8：diff.bgImages 非 null 即有变更（[]=清空，非空列表=新状态）——整体替换
  let bgImages = state.bgImages;
  if (diff.bgImages != null) {
    bgImages = diff.bgImages.map((bg) => ({ ...bg }));
  }

  return {
    state: {
      lines,
      bgColor,
      bgImages,
      // v12：awaiting 由外层 TurnRecord 携带，归约层原样透传。
      awaiting: state.awaiting,
    },
    signal,
  };
}

/**
 * 确保存在一个"当前行"——最后一行未终止（isLineEnd=false）；若不存在则追加新行。
 * 与 C# `TestAdapter.EnsureCurrentLine`（Emuera.Headless.Tests/TestAdapter.cs:140）对称。
 *
 * 直接 push 到 lines 数组（调用方负责保证 lines 是影子拷贝）。
 */
function ensureCurrentLine(lines: DisplayLine[]): void {
  if (lines.length === 0 || lines[lines.length - 1].isLineEnd) {
    lines.push({ entries: [], align: null, isLineEnd: false });
  }
}
