import type { DisplayState, DisplaySnapshot, DisplayLine, DisplayEntry } from '../types/protocol';

/**
 * 用全量 `DisplaySnapshot` 替换内部状态（issue 02；issue 06 接 `awaiting`；issue 09 收缩）。
 *
 * 与 C# `TestAdapter.ApplySnapshot` 对称：
 * - lines 深拷贝（每行 entries 也深拷贝，避免外部 mutation 影响内部状态）
 * - bgColor / bgImages 直接覆盖
 * - `awaiting` 覆盖：**必填**契约字段——快照与流上帧同形（同一个 C# 工厂、同一个
 *   req/state），v12 起不再有「旧顶层字段合成」的兼容垫片
 * - `protocolVersion` / `generation` 不进入 DisplayState（wire 元数据，不属于显示状态）
 *
 * 函数式纯度：返回新对象，不修改入参 `state`（与 C# TestAdapter 可变实例不同——
 * Pinia store 需要响应式不可变更新）。`state` 参数保留是为了与 `applyOps(state, ops)`
 * 签名对称；其值被忽略——快照本身就是全量替换。
 *
 * @param _state 之前的状态（被忽略——快照替换全部）
 * @param snapshot 全量快照（通常来自 GET /snapshot）
 * @returns 新的 DisplayState
 */
export function applySnapshot(_state: DisplayState, snapshot: DisplaySnapshot): DisplayState {
  return {
    lines: snapshot.lines.map(copyLine),
    bgColor: snapshot.bgColor ?? null,
    // v8：背景图全量重建（C# 紧凑归一——空列表在 JSON 中省略，缺失即空）
    bgImages: (snapshot.bgImages ?? []).map((bg) => ({ ...bg })),
    awaiting: snapshot.awaiting,
  };
}

/**
 * 深拷贝单行——entries 数组也复制，避免外部 mutation。
 * 与 C# `new AdapterLine(entries.ToList(), line.align, line.isLineEnd)` 对称。
 */
function copyLine(line: DisplayLine): DisplayLine {
  return {
    entries: line.entries.map(copyEntry),
    align: line.align ?? null,
    isLineEnd: line.isLineEnd,
  };
}

/**
 * 深拷贝单 entry。segments 数组浅拷贝（每个 PrintSegment 也复制）——
 * 与 C# `e.segments.ToList()` 略不同：C# PrintSegment 是 record（init-only，不可变），
 * ToList 后元素共享引用安全；TS 对象默认可变，故此处对每个 segment 也做对象展开。
 *
 * 这样 applySnapshot 后修改 newState.lines[i].entries[j].segments[k].text 不会影响
 * 原 snapshot——符合函数式纯度，便于 Pinia store 响应式追踪。
 */
function copyEntry(entry: DisplayEntry): DisplayEntry {
  return {
    segments: entry.segments.map((s) => ({ ...s })),
    button: entry.button ? { ...entry.button } : null,
  };
}
