# CLI ↔ Web 差异表（Phase 0-3）

> 轻量文档：重述 ADR-0013 的 4 缺口，标注各缺口由哪个 Phase 消化。不重做调研。
>
> 详见执行计划：[DisplayState 统一真相源：执行计划.md](./DisplayState%20统一真相源：执行计划.md)

## 背景

CLI（`TerminalRenderer` + `ButtonRegionTracker`）与 Web（`AgentJsonlProtocol.BuildTurn`）两条渲染路径的数据源不对等。ADR-0013 识别了 4 个缺口；ADR-0014 推翻决策四（CLI 不迁移），要求 DisplayState 成为 CLI 与 Web 的统一真相源。本表标注各缺口的消化落点。

## 差异表

| # | 缺口 | 现状描述 | 消化落点 | 状态 |
|---|------|----------|----------|------|
| 1 | **ops 是 displayLineList 的有损投影** | CLI 富模型有按钮几何（PointX/Width/row），Web op 模型（`TakePendingOps()` 返回的 `List<TurnOp>`）只有 print/newline/clearline/clear/set_bg，无几何 | **Phase 2**（`DisplayDiff` 增量模型作为 `ops[]` 的并行新格式）+ **Phase 5**（删除 `ops[]` 旧格式，`diff` 成为唯一增量格式） | Phase 2/5 待实施 |
| 2 | **ButtonRef 无几何** | Web 前端须自己重写 `ButtonRegionTracker`（含 CJK 双宽字符 `TerminalDisplayWidth` 逻辑）才能做按钮 hit-test | **ADR-0013 决策三已落地**（`ButtonRef` 加 `col`/`width` nullable 字段，`ConsolePrintManager.EmitPrintOps` 填充）；**Phase 3-2** 补几何完备性断言（仅凭 `lines[i].entries[j].button` 的 `col`/`width` 与数组位置即可唯一定位每个按钮）；**Phase 3-3** `ButtonRegionTracker.UpdateFromSnapshot` value-based 路径 | ADR-0013 已落地；Phase 3 验证 |
| 3 | **无全屏快照** | ADR-0003 accepted "晚加入者不回放历史"，WS 晚加入者（断线重连、多标签观察、调试工具中途连接）无初始状态无法渲染 | **ADR-0013 决策一已落地**（`GET /snapshot` 端点 + `DisplaySnapshot` state-based 格式） | ✅ 已落地 |
| 4 | **PrintImg 退化** | `PrintImg`/`PrintShape` 在 ops 中退化为 `node.ToString()`，Headless 未实现 sprite 加载（`AppContents.GetSprite` 返回 null） | **不在本计划范围**（ADR-0013 决策五排除；候选 5 可能删除 Image/ 子树）。未来 sprite 加载落地后，新候选扩展 `DisplaySnapshot` 加 `resources` 字段 | 不在范围 |

## 各 Phase 与缺口的关系

- **Phase 0**（本阶段）：建护栏、不改代码。golden 测试锁定快照形状，性能基线锁定 `BuildSnapshot` 量化指标。
- **Phase 1**：DisplayState 实例化 + 变更检测（`TryUpdate` peek `_pendingOps`）。为 Phase 2 diff 提供连续快照对。
- **Phase 2**：`DisplayDiff` 增量模型——消化缺口 1（ops 有损投影 → diff 行级操作序列）。`TurnRecord` 双模式输出（`ops` + `diff` 并存）。
- **Phase 3**：按钮几何统一 + VT sink 抽象——验证缺口 2 几何完备性（`UpdateFromSnapshot` value-based 路径），为 Phase 4 CLI 迁移铺路。
- **Phase 4**：CLI Adapter 迁移——`TerminalRenderer` 改为以 `DisplayState.Current.lines` 为数据源，消化缺口 1/2 在 CLI 侧的剩余差异。
- **Phase 5**：清理与协议升级——删除 `ops[]` 旧格式（缺口 1 彻底消化），协议版本升至 v4。
