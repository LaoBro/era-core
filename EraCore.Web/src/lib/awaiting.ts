/**
 * v12 契约 `awaiting` 的共用类型助手。
 *
 * 收缩阶段（issue 09）后 `awaiting` 只来自 wire——流上帧（`parseTurnRecord`）与全量快照
 * （`snapshotReducer.applySnapshot`）**同形**（同一个 C# 工厂、同一个 req/state），
 * 故原先那个「v11 兼容期把旧顶层字段合成同一容器」的垫片 `awaitingFromLegacyFields`
 * 已随七个旧顶层字段一并删除。
 */

/**
 * 「值输入」判定：`IntValue` / `StrValue`。
 *
 * v12 起 `needValue` 不再上 wire——客户端按 `input.type` 推出，本函数即那条推出规则。
 * 判的是「文本框里的空值是否非法」：`IntValue`/`StrValue` 必须给值（禁止空提交），
 * `AnyValue` 同为文本框但允许空；`IntButton` / `StrButton` / `PrimitiveMouseKey`
 * 的 value 由游戏内按钮承载，根本不渲染文本框（见 `components/InputBar.vue`）。
 */
export function isValueInputType(type: string | null | undefined): boolean {
  return type === 'IntValue' || type === 'StrValue';
}
