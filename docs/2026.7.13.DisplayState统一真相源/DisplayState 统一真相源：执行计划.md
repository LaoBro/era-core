## DisplayState 统一真相源：执行计划

---

### 核心思路

当前 `EmueraConsole.DisplayLineList` 是原始真相，但 CLI 和 Web 各自独立解读它。升级的目标是：**DisplayState 成为 DisplayLineList 的唯一消费者，CLI 和 Web 都从 DisplayState 的输出获取数据**。

```
Before:  DisplayLineList ──→ TerminalRenderer (CLI 自己算)
         DisplayLineList ──→ AgentJsonlProtocol (ops 队列)
         DisplayLineList ──→ DisplayState (快照)

After:   DisplayLineList ──→ DisplayState (唯一入口)
                                ├─→ DisplaySnapshot (全量)
                                ├─→ DisplayDiff (增量)
                                ├─→ ButtonGeometry (按钮几何)
                                ├─→ CLI Adapter → VT 转义序列
                                └─→ Web Adapter → JSON
```

### Grilling 决议（2026-07-13）

15 个架构决策点已收口。关键决议（其余见各 Phase 内联标注 `【grill Qn】`）：

| 决策 | 决议 | 盖戳 |
|------|------|------|
| `DisplayLine.LineNo` | 加 `[JsonIgnore] internal int LineNo`，CLI delta 算法不变 | Q1 |
| `TryUpdate` 消费时机 | Phase 1-4 peek only，Phase 5 消费式 drain 并删 `TakePendingOps` | Q3 |
| `DisplayLine` record 相等性 | `segments` 类型换 `IReadOnlyList<PrintSegment>`（值相等），不改 DiffSnapshots | Q4 |
| 快照并发 race | `Rebuild` 内 `new List<ConsoleDisplayLine>(original)` 浅拷贝 | Q5 |
| CLI DisplayState 归属 | `AgentCliProtocol` 自构造持有 | Q7 |
| CLEARLINE(0) 信号 | 不做特殊处理，delta 算法覆盖 | Q8 |
| VT sink 抽象 | Phase 3 前置 `IAgentCliVtScreen` + `BufferedVtScreen` | Q9 |
| `--strict` 触发粒度 | 帧级比对（每 `FlushBuffer`） | Q10 |
| ButtonRegionTracker | Phase 3 新增 `UpdateFromSnapshot`，旧方法保留到 Phase 4 | Q13 |
| TestButtonFactory | 轻量构造器，直接 new 引擎类型 | Q15 |

### 复审补充决议（grilling 2026-07-14）

针对本执行计划的可行性复审，收口 6 个实施缺口（详见各 Phase 内联 `【grill 2026-07-14 补】`）：

| # | 缺口 | 决议 | 落点 |
|---|------|------|------|
| R1 | 删 `DrainPendingOpsForCli` 后 CLI 怎么感知全屏事件 | **snapshot 比对推断** CLEARLINE/CLEAR/SET_BG，`FlushBuffer` 脱离 `_pendingOps` | Phase 4-1 / 5-3 |
| R2 | `FullRefresh(snapshot)` 的 `SelectingButton` / `CharWidthConfig` 来源 | **真相源边界 = `DisplayLineList + bgColor`**，二者仍从 `_console` 直读（输入/配置态） | Phase 4-1 |
| R3 | `_lastRenderedLastLine` 类型与 `ReferenceEquals` | 类型 `ConsoleDisplayLine?` → `DisplayLine?`，**删 `ReferenceEquals`** 检查 | Phase 4-1 |
| R4 | CLI `defaultFontName` 注入路径 | **注入 `ConfigData`** 到 `AgentCliProtocol` 构造，与 `Session` 同表达式 | Phase 4-2 |
| R5 | `BuildSnapshot` 全量性能基线 | Phase 0 加 `BuildSnapshotPerformanceTests`，1000 行 < 5ms | Phase 0（0-6） |
| R6 | `UpdateFromSnapshot` 调用入口 | **`ButtonSelectionMode` 加 `_displayState` + `RefreshButtonRegionsFromSnapshot()`** | Phase 3-3 / 4 |

> 事实核查（无需决议）：① `ConsoleDisplayLine` 虽为 mutable class，但所有字段修改（`LineNo`/`IsLineEnd`/`bitmapCacheEnabled`/`ChangeStr`/`SetAlignment`）均在 `displayLineList.Add(line)` **之前**，Add 后事实不可变——Q5 浅拷贝足够。② `emuera_gateway` 不消费 ops，v3→v4 网关侧真零改动（见 5-1）。

---

### Phase 0：现状基线与护栏（1-2 天）

**目标**：确保升级过程中不破坏任何现有行为。

> **范围修正（grilling 2026-07-13）**：已确认推翻 ADR-0013 决策四（CLI 不迁移），DisplayState 将成为 CLI 与 Web 的统一真相源（见 ADR-0014）。Phase 0 只建护栏、不改代码；CLI 的实际迁移在 Phase 4。护栏遵循「简单高效」原则——Web 用合成 C# golden，CLI 不录字节 golden，全部固定 **MS Gothic** 字体以保证 `width` 确定。

| 任务 | 说明 |
|------|------|
| 0-1 Web 快照 golden（C# 单测） | 新增 `DisplayStateSnapshotGoldenTests`：用 `TestButtonFactory` 轻量构造器（直接 new `ConsoleDisplayLine`/`ConsoleButtonString`，见 Q15）合成 5 个场景（空屏 / 单行 / 多行按钮 / CLEARLINE 态 / SET_BG 态）的 `ConsoleDisplayLine` 列表，喂 `BuildSnapshot` 序列化，与提交的 `.json` 资源（`Emuera.Headless.Tests/TestData/Phase0/`）比对。不走服务器，毫秒级、无 ConPTY 依赖。`ConfigData.FontName` 固定 MS Gothic。 |
| 0-1b 端点接线薄护栏 | 在 `test_server_single_session.py` 加 1 个最小断言：`GET /snapshot` 对已知简单场景返回的 JSON 形状（state / lines / button.col+width）与 C# golden 一致，确保端点 wiring 未断。 |
| 0-2 快照确定性/等价性 | `DisplayStateTests` 补两条：相同输入 → `BuildSnapshot` 两次产出 byte-identical JSON（幂等）；不同输入 → 不同 JSON（区分度）。原「diff 负例」前移——diff 属 Phase 1，此处以快照等价性替代（等价于「快照不等价 ⟺ JSON 不同」，是 Phase 2 `ComputeDiff`「不同快照不应产生空 diff」的前置）。 |
| 0-3 CLI↔Web 差异表 | 轻量文档：重述 ADR-0013 的 4 缺口（ops 有损投影 / ButtonRef 无几何 / 无全屏快照 / PrintImg 退化），标注各缺口由哪个 Phase 消化（几何→Phase 3；全屏快照→ADR-0013 已落地；diff→Phase 2）。不重做调研。 |
| 0-4 TINPUT 超时 | **不进** DisplayState golden——超时是 `/turn` 概念非 `/snapshot`。交由现有 `test_tinput_timeout.py` e2e 覆盖。 |
| 0-5 CLI 护栏 | 不新建 CLI 字节 golden（DA1/resize/字体噪声易碎）。现有 `test_cli_*.py`（按钮点击/滚动/CLEARLINE/SET_BG）全绿即当前行为基线；Phase 4 的 4-3 双路径对比加 `--strict` 开关，新旧路径不一致时 `Assert.Fail`（CI 开、开发关），作为 CLI 回归守卫——即 ADR-0013 理由一（回归风险）的缓解手段。 |
| 0-6 BuildSnapshot 性能基线 **【grill 2026-07-14 补】** | 新增 `BuildSnapshotPerformanceTests`：合成 100 / 1000 / 5000 行 `ConsoleDisplayLine` 列表，调 `BuildSnapshot` 测全量重建耗时。阈值：**1000 行 < 5ms**（CLI 60fps 帧预算 16ms，留 ~10ms 给渲染 + VT I/O）。理由：`TryUpdate` 每次 `PendingOpCount > 0` 就 `Rebuild()` → `BuildSnapshot` 全量遍历 `displayLineList`，对每行调 `BuildPrintOpsForLine`（含 `TerminalDisplayWidth` CJK 双宽计算）；CLI `FlushBuffer` 帧级调 `TryUpdate`，大屏累积后可能每帧全量 rebuild。超阈 → Phase 1 `TryUpdate` 须加增量优化（按 `ConsoleDisplayLine` 引用 memoize `BuildPrintOpsForLine` 结果）。设为 Phase 0 护栏以在 Phase 1 实施前锁定量化基线，避免「做完 Phase 1 才发现 CLI 卡顿」的回溯成本。 |

---

### Phase 1：DisplayState 实例化与变更检测（3-5 天）

**目标**：DisplayState 从"无状态工具类"升级为"有状态的显示模型"，具备变更检测能力。

> **grilling 修正（2026-07-13）**：
> - 变更检测**不读 `LineNo`**——`LineNo` 在 `CLEARLINE` 后会回退，非单调（`TerminalRenderer.cs:65-68` 已为此重置 delta tracking 强制 `FullRefresh`）。改用引擎既有的 `_state._pendingOps` 作为权威变更信号（peek，不 drain）。
> - `BuildTurn` 必须在 `TakePendingOps()` **之前**调 `TryUpdate()`，否则 pendingOps 已空，检测永远 false。
> - 单个 `DisplayState` 实例由 `Session` 持有并注入 `AgentJsonlProtocol`；`GET /snapshot` 走该实例，需 `_gate` 锁保护跨线程读写。

当前 [DisplayState](../../EraCore.Core/Agent/DisplayState.cs#L51-L70) 每次调用 `GetSnapshot()` 都重新遍历整个 `DisplayLineList`，不持有任何状态。这是升级的起点。

#### 1-0 前置：EmueraConsole 暴露 PendingOpCount

`DisplayState` 现只能经 `internal` 属性访问 console（`EmueraConsole.cs:58-67`），`_state._pendingOps` 不可直达。新增只读属性（Emuera.Headless 自有代码，非 Shared）：

```csharp
internal int PendingOpCount => _state._pendingOps.Count;
```

#### 1-1 DisplayState 持有当前 Snapshot + 线程安全变更检测

```csharp
internal sealed class DisplayState
{
    private readonly EmueraConsole _console;
    private readonly string _defaultFontName;
    private readonly object _gate = new();
    private DisplaySnapshot? _current;

    // 权威变更信号：引擎每次显示变更都向 _pendingOps Add
    // （ClearOp/ClearLineOp/SetBgOp/PrintOp/NewLineOp，见 ConsolePrintManager.cs:65/174/549/570/647）
    // 单调无关（不受 CLEARLINE LineNo 回退影响），且捕捉原地末行编辑（会 Add(PrintOp)）。
    internal DisplaySnapshot Current
    {
        get { lock (_gate) { TryUpdate(); return _current!; } }
    }

    /// peek _pendingOps（不 drain；drain 仍由 BuildTurn 的 TakePendingOps 负责）
    internal bool TryUpdate()
    {
        lock (_gate)
        {
            if (_current != null && _console.PendingOpCount == 0)
                return false; // 无变化
            _current = Rebuild();
            return true;
        }
    }

    private DisplaySnapshot Rebuild()
    {
        // 浅拷贝 displayLineList 防止 HTTP 线程并发遍历时游戏线程写入（Q5）
        var linesCopy = new List<ConsoleDisplayLine>(_console.DisplayLineList);
        return BuildSnapshot(linesCopy, _console.bgColor,
            _console.State, _console.CurrentRequest, _defaultFontName);
    }
}
```

#### 1-2 归属与接入

- `Session.GameLoopAsync` 内构造**单个** `DisplayState`（注入 `console` + 从 `ConfigData` 取的 `defaultFontName`），传入 `AgentJsonlProtocol` 构造。
- `AgentJsonlProtocol.BuildTurn` 在 `console.TakePendingOps()` **之前**调 `_displayState.TryUpdate()`，确保与 turn 同步且能 peek 到 pendingOps。
- `Session.GetDisplaySnapshot()` 改为返回 `_displayState.GetSnapshotJson()`（内部 `Current` → `TryUpdate`），不再每次 new 实例。

#### 1-3 验证点

- 所有现有 Python 端到端测试通过
- `GET /snapshot` 返回结果与 Phase 0 golden fixture 一致
- DisplayState 单测（`DisplayStateTests` / 新增 `DisplayStateChangeDetectionTests`）：
  - `TryUpdate()` 在 pendingOps 非空时返回 true 并重建；清空后返回 false 且 `_current` 不变
  - **CLEARLINE 回归用例**：行数 / `LineNo` 回退后再 print 使 count + `LineNo` 回到旧值，必须判定为「变化」（直接验证 LineNo 方案会漏检的反例）
  - 并发：游戏线程写、`GET /snapshot` HTTP 线程读 `Current` 在 `_gate` 锁下无竞争；`Rebuild` 内浅拷贝 `displayLineList`（Q5）

---

### Phase 2：DisplayDiff 增量模型（5-7 天）

**目标**：定义两个连续 DisplaySnapshot 之间的 diff 数据模型，作为 `TurnRecord.ops[]` 的并行新格式（Phase 5 再废弃 ops）。

> **grilling 修正（2026-07-13）**：
> - diff 由**快照比对**得出（非翻译 `_pendingOps`）；no-op 回合用 `ReferenceEquals(prev, curr)` 短路返回 null（Phase 1 的 `TryUpdate` 仅在 `_pendingOps` 非空时 rebuild，故引用不变 = 显示未变；state/inputType 变化由 TurnRecord 顶层字段携带）。
> - **只用 3 种 LineOp**（删去 `UpdateLineOp`），与「关键设计决策」自述一致；末行原地编辑表达为 `Truncate + Append`。
> - `DisplayDiff` 只含 `lineOps + bgColor`；`state/inputType/needValue/protocolVersion` 由外层 TurnRecord 提供，不在 diff 内重复。

#### 2-1 定义 DisplayDiff 记录

```csharp
/// 两个连续 DisplaySnapshot 之间的差异（仅行级 + 背景色；其余状态由外层 TurnRecord 携带）
internal record DisplayDiff(
    List<LineOp> lineOps,   // 行级操作序列（仅 append / truncate / replace_all 三种）
    string? bgColor         // null=未变，非 null=新背景色
);

internal abstract record LineOp(string type);

/// 追加新行（curr 比 prev 多出的尾部）
/// 注：DisplayLine.segments 字段类型为 IReadOnlyList<PrintSegment> 而非 List<PrintSegment>，
/// 确保 C# record 编译器生成值相等性而非引用相等性（Q4）。
internal record AppendLinesOp(List<DisplayLine> newLines) : LineOp("append");

/// 截断尾部，保留前 keepCount 行（CLEARLINE 场景）
internal record TruncateLinesOp(int keepCount) : LineOp("truncate");

/// 全量替换（CLEAR / 全重置场景，diff 不经济时 fallback）
internal record ReplaceAllOp(List<DisplayLine> allLines) : LineOp("replace_all");
```

**关键设计决策**：为何不用 `insert_line` / `delete_line` 任意位置操作、也不加 `update_line`？因为 Emuera 的显示模型是**追加式**的——新内容追加到末尾，`CLEARLINE` 从末尾删除，`CLEAR` 全部清除，**头部行永不变**。所以 diff 只需三种尾部操作；公共前缀 `k` 之后的差异一律用 `Truncate(keep k) + Append` 表达（含末行原地编辑：PRINT 不带换行落到最后一支，= `Truncate(k) + Append(1)`）。

#### 2-2 DisplayState.ComputeDiff

```csharp
internal sealed class DisplayState
{
    private DisplaySnapshot? _previous;

    internal DisplayDiff? ComputeDiff()
    {
        var current = Current;            // 经 TryUpdate 保证最新（Phase 1）
        var prev = _previous;
        _previous = current;

        if (prev == null) return null;                    // 首次，无 diff
        if (ReferenceEquals(prev, current)) return null;  // 本回合显示未变（no-op）；state 变化走 TurnRecord 顶层

        var lineOps = DiffSnapshots(prev, current);
        string? bg = prev.bgColor == current.bgColor ? null : current.bgColor;
        return new DisplayDiff(lineOps, bg);
    }

    private static List<LineOp> DiffSnapshots(DisplaySnapshot prev, DisplaySnapshot curr)
    {
        var p = prev.lines; var c = curr.lines;
        int k = CommonPrefix(p, c);                  // 逐行值比较（DisplayLine 为 record，值相等）
        if (k == p.Count) return [ new AppendLinesOp(c.GetRange(k, c.Count - k)) ];   // 纯追加
        if (k == c.Count) return [ new TruncateLinesOp(k) ];                          // 纯截尾（CLEARLINE）
        if (k == 0 && p.Count > 0) return [ new ReplaceAllOp(c) ];                    // 头部都变 = CLEAR/全重置
        return [ new TruncateLinesOp(k), new AppendLinesOp(c.GetRange(k, c.Count - k)) ]; // 尾部替换
    }

    private static int CommonPrefix(List<DisplayLine> a, List<DisplayLine> b)
    {
        int n = Math.Min(a.Count, b.Count);
        int k = 0;
        while (k < n && a[k].Equals(b[k])) k++;
        return k;
    }
}
```

> 注意：`_previous` 的读写须与 Phase 1 的 `_current` 同处 `_gate` 锁内。仅游戏线程 `BuildTurn` 调用 `ComputeDiff`，无新增并发，但锁须覆盖二者。

#### 2-3 TurnRecord 扩展：双模式输出

不立即删除 `ops[]`，而是让 TurnRecord 同时携带两种格式：

```csharp
internal record TurnRecord(
    string state,
    string? inputType,
    bool needValue,
    List<TurnOp> ops,           // 旧格式，渐进废弃
    DisplayDiff? diff,          // 新格式（仅 lineOps + bgColor）
    string? error = null,
    int? protocolVersion = null
);
```

Web adapter 优先读 `diff`，CLI adapter 继续读 `ops`（Phase 4 再迁移）。两套格式并存，回归风险可控。

#### 2-4 验证点

- 新增 `DisplayDiffTests`：覆盖纯追加、纯截尾（CLEARLINE）、CLEAR/全重置（ReplaceAll）、末行原地编辑（Truncate+Append）、背景色变更（bgColor 非 null）、no-op 回合（ReferenceEquals → null）
- TurnRecord JSON 中 `diff` 字段可被前端消费；`ops` 字段保持不变，现有客户端零影响
- 确认 `diff` 不含 `state/inputType/needValue`（由 TurnRecord 顶层提供）

---

### Phase 3：按钮几何统一 + VT sink 抽象（3-4 天）

**目标**：确认 Web 端按钮定位信息已完备（ADR-0013 已并入 `col`/`width`），并记录 CLI 消费 DisplayState 时的提交路径结论，为 Phase 4 铺路。

> **grilling 修正（2026-07-13）**：
> - **不扩展 `ButtonRef`**：`lineIndex` 由 `DisplaySnapshot.lines[i].entries[j]` 的数组位置隐含；`row` 是 CLI 渲染期概念（依赖 `Scroll Offset + viewportHeight`），二者都不进快照（与 ADR-0013 决策三一致，且不污染共用的 `ButtonRef`——它同时被快照和 ops 使用）。
> - **CLI 命中区走 value-based**：`DispatchMouseClick` 本就只从 `ConsoleButtonString` 取 value 后 `DispatchInput(value)`；旧 Generation 按钮的拦截由**服务端 `ConsoleInputHandler`** 按 `button.Generation == lastButtonGeneration` 校验（`ConsoleInputHandler.cs:210-267`），不依赖客户端持有对象引用。故 CLI 改为消费快照的 `ButtonRef.value` 提交，**无正确性回归**，`generation` 不入快照。

#### 3-0 前置：VT sink 抽象

`TerminalRenderer` 的 VT 输出当前直写 `AgentCliVtScreen`（真实终端），Phase 4 双路径比对须能程序化捕获输出。新增 `IAgentCliVtScreen` 接口 + `BufferedVtScreen` 测试实现（内存 byte 缓冲），`AgentCliVtScreen` 实现接口作为生产实现。Phase 4 双路径比对依赖此抽象（Q9）。

#### 3-1 ButtonRef 维持不变

`ButtonRef`（`TurnRecord.cs:49`）保持 `(value, isInteger, col?, width?)`。Web 端定位按钮 = `lines[i].entries[j]` 的数组位置（隐含 lineIndex）+ `col`/`width`（行内几何）。不需要显式 `lineIndex`/`row` 字段，也不为 CLI 内部概念污染 Web 契约。

#### 3-2 Web 几何完备性断言（替代原「BuildSnapshot 注入 lineIndex」）

`DisplayStateTests` 补断言：给定多行多按钮快照，仅凭 `lines[i].entries[j].button` 的 `col`/`width` 与数组位置即可唯一定位每个按钮（含 CJK 双宽累加到下一行起点的列）。证明 ADR-0013 几何已满足 Web 需求，Phase 3 无新字段要加。

#### 3-3 CLI 命中区消费 DisplayState（Phase 4 实施，此处加新路径 + 测试）【grill Q13】

Phase 3 在 `ButtonRegionTracker` 上新增 `UpdateFromSnapshot(DisplaySnapshot, int scrollOffset, int viewportHeight)` 方法（value-based），但保留旧 `RecordLineRegions` 引用式路径不动。Phase 3 验证几何完备性断言时兼验证 `UpdateFromSnapshot` 的正确性；Phase 4 切换调用点后删旧方法。

CLI 的 `ButtonRegionTracker` 当前持 `ConsoleButtonString` 引用 + `Generation` 过滤（`ButtonRegionTracker.cs:27`）。Phase 4 改为从 `DisplayState` 快照取 `col`/`width` 建 `Region`，命中后按 `ButtonRef.value` 提交：

```csharp
// Phase 3 新增方法（Q13：新旧路径共存，Phase 4 切换后删旧路径）
public void UpdateFromSnapshot(DisplaySnapshot snapshot, int scrollOffset, int viewportHeight)
{
    _regions.Clear();
    int startLine = Math.Max(0, snapshot.lines.Count - viewportHeight - scrollOffset);
    for (int i = 0; i < viewportHeight && (startLine + i) < snapshot.lines.Count; i++)
    {
        var line = snapshot.lines[startLine + i];
        foreach (var entry in line.entries)
        {
            if (entry.button is { } btn && btn.col is { } c && btn.width is { } w)
            {
                _regions.Add(new Region(
                    Row: i,
                    Left: c,
                    Right: c + w - 1,
                    Value: btn.value,        // value-based，点击时 DispatchInput(value)
                    IsInteger: btn.isInteger
                ));
            }
        }
    }
}
```

`Region` 改为持 `Value`/`IsInteger` 替代 `ConsoleButtonString` 引用；`Generation` 过滤移除（服务端校验兜底）。视口 `Row: i` 仍本地算。

> **调用入口编排【grill 2026-07-14 补】**：`UpdateFromSnapshot` 是**单次调用**，与旧路径 `ButtonSelectionMode.RefreshButtonRegions` 内**按行循环**调 `VtInputHandler.RecordLineRegions` 语义不同，切换时须明确谁负责调。定案：**`ButtonSelectionMode` 加 `_displayState` 字段（构造注入）+ 新增 `RefreshButtonRegionsFromSnapshot()` 方法**，内部调 `_tracker.UpdateFromSnapshot(_displayState.Current, _scroll.ScrollOffset, viewportHeight)`（不经 `VtInputHandler` 转发）。理由：`ButtonSelectionMode` 是命中区编排者，语义一致，且它本就在 Phase 4 改造范围内（持 `_console` 引用读 `DisplayLineList`，Phase 4 改读 snapshot）；`VtInputHandler` 是输入解析器，不应承担显示数据转发职责（单一职责）。旧路径调用链（`AgentCliProtocol` 7 处 `RefreshButtonRegions`，line 88/102/193/252/258/296 → `ButtonSelectionMode.RefreshButtonRegions:150` → `VtInputHandler.RecordLineRegions:36` → `ButtonRegionTracker.RecordLineRegions`）在 Phase 4 统一切换：7 处调用点改为 `RefreshButtonRegionsFromSnapshot`，删旧 `RefreshButtonRegions` + `RecordLineRegions`。

#### 3-4 验证点

- `DisplayStateTests` 几何完备性断言通过（数组位置 + col/width 可唯一定位）
- 记录结论：CLI value-based 提交、`generation` 服务端校验，Phase 4 直接照做
- Web 端 `ButtonRef` 不含 `lineIndex`/`row`/`generation`（契约保持 ADR-0013 形态）

---

### Phase 4：CLI Adapter 迁移（5-7 天）

**目标**：CLI 的 `TerminalRenderer` 改为以 `DisplayState` 为唯一数据源（不再直接读 `DisplayLineList`），保留已调优的增量 delta 渲染算法。

> **grilling 修正（2026-07-13）**：
> - **DisplayState 是回合级、CLI 渲染是帧级**——不能只在 `AgentJsonlProtocol.BuildTurn` 里刷新。CLI 模式跑 `AgentCliProtocol`（其 `GetInitialTurnAsync/StepAsync` 返回 null，`BuildTurn` 不被调用），故 CLI 需**自有 `DisplayState` 实例**，并在 `FlushBuffer` 轮询中调 `TryUpdate()` 实时刷新。
> - **不改写为「消费 DisplayDiff」**：`DisplayDiff` 是 Web 回合级增量（每 turn `ComputeDiff` 一次），单位与帧级渲染不匹配；CLI 保留现有 `_lastRenderedLineNo` delta 算法，只把数据源从 `_console.DisplayLineList` 换成 `DisplayState.Current.lines`。回归风险最低，契合 ADR-0014 双路径缓解。

#### 4-1 TerminalRenderer 以 DisplayState 快照为数据源【grill Q1】

`TerminalRenderer` 现有增量逻辑（`_lastRenderedLineNo` 比较、`FullRefresh`/`FlushBuffer` 的尾部 delta）**保持不变**，仅把读取的数据源从 `_console.DisplayLineList` 换成 `DisplayState.Current.lines`。

`DisplayLine` 新增 `[JsonIgnore] internal int LineNo` 字段（Q1），`BuildSnapshot` 从 `ConsoleDisplayLine.LineNo` 传入。该字段仅 CLI 渲染使用，不进入 JSON 序列化。

```csharp
// 改造前
internal void FullRefresh()
{
    var lines = _console.DisplayLineList;
    // ... 按 _lastRenderedLineNo 做 delta 渲染
}

// 改造后：delta 算法不变，数据源换成快照
internal void FullRefresh(DisplaySnapshot snapshot)
{
    var lines = snapshot.lines;   // DisplayLine 含 align / isLineEnd / entries[].segments / button.col+width
    // ... 原有 delta 渲染逻辑原样套用
}
```

`FlushBuffer` 同理从 `snapshot.lines` 计算 tail delta。`DisplayLine` 已覆盖渲染所需全部信息（对齐、行末、段样式、按钮几何），格式化逻辑无需改写。

> **真相源边界【grill 2026-07-14 补】**：`FullRefresh(snapshot)` / `WriteDisplayLine` 内 `TerminalLineFormatter.FormatLineForTerminal`（`TerminalRenderer.cs:183-185`）仍需 `_console.SelectingButton`（选中按钮高亮，输入态）与 `_console.CharWidthConfig`（CJK 双宽判定，配置态）——二者**不是 `DisplayLineList` 内容**，不进 `DisplaySnapshot`。据此明确「统一真相源」范围 = **`DisplayLineList` + `bgColor`**；`SelectingButton` / `CharWidthConfig` 仍由 `TerminalRenderer` 经其持有的 `_console` 引用（line 16）直读，零改造。语义澄清：DisplayState 是 `DisplayLineList` 的唯一消费者，**不是整个 `_console` 的唯一消费者**。

> **末行类型变更与 `ReferenceEquals`【grill 2026-07-14 补】**：数据源换成 `snapshot.lines` 后，末行类型从 `ConsoleDisplayLine` 变为 `DisplayLine`（record）。须同步：`_lastRenderedLastLine` 字段类型 `ConsoleDisplayLine?` → `DisplayLine?`（`TerminalRenderer.cs:21`）、`WriteNewLinesSince` 参数类型 `ConsoleDisplayLine?` → `DisplayLine?`、`lines[i].LineNo` 来源改自 `DisplayLine.LineNo`（Q1 新增字段）。**删除 `ReferenceEquals(_lastRenderedLastLine, lastLine)` 检查**（`TerminalRenderer.cs:138`）：`BuildSnapshot` 每次 `new DisplayLine(...)`，record 引用每次必不同，`ReferenceEquals` 恒 false，检查冗余；简化为「`LineNo` 不变即擦末行重写」。副作用：末行内容未变时也重写，但成本极低（一行 `EraseTerminalRows(1) + WriteDisplayLine`），可接受。不改 `Equals`（deep equals 一行开销可能超过重写本身）。

> **FlushBuffer 全屏事件信号源（依赖 Phase 5-3）【grill 2026-07-14 补】**：现状 `FlushBuffer`（`TerminalRenderer.cs:41-72`）第一行调 `_console.DrainPendingOpsForCli` 感知 ClearOp/ClearLineOp/SetBgOp 并据此重置 delta tracking / 写 VT 转义——`DrainPendingOpsForCli` 是**事件信号通道**而非数据源。Phase 5-3 删该 API 后（且 `TryUpdate` 消费式清空 `_pendingOps`，drain 拿到空列表），须改为**从 snapshot 比对推断全屏事件**：CLEARLINE = `snapshot.lines.Count < prevCount` 或末行 `LineNo` 回退（`_lastRenderedLineNo > snapshot.lines[^1].LineNo`）→ 重置 `_lastRenderedLineNo = -1` 走 `FullRefresh`（现有 `currentLineNo < _lastRenderedLineNo` 分支已能处理）；CLEAR = `snapshot.lines.Count == 0` → `ClearScreen` + 重置 tracking；SET_BG = `snapshot.bgColor != _currentBgHex` → `WriteBgEscape` + 更新 `_currentBgHex`。改造后 `FlushBuffer` 完全脱离 `_pendingOps`，Phase 5 删 `DrainPendingOpsForCli` 零回归。此改造须在 Phase 4-1 与 Phase 5-3 同步落地。

#### 4-2 CLI 自有 DisplayState + 实时刷新【grill Q7】

CLI 模式无 `Session`，由 `AgentCliProtocol` 自构造**一个** `DisplayState`（构造函数中拿 `console` + 从 `ConfigData` 取 `defaultFontName`），在 `RunVtLoop` 的 `FlushBuffer()` 入口调 `TryUpdate()`，使 `Current` 随游戏打印实时推进：

```csharp
// AgentCliProtocol.RunVtLoop 内
_displayState.TryUpdate();          // 先刷新快照
_renderer.FlushBuffer(_displayState.Current);
```

> 与 server 路径区分：server 的 `DisplayState` 由 `Session` 持有、`BuildTurn` 内 `TryUpdate`（回合级，供 Web `diff`）；CLI 的 `DisplayState` 由 `AgentCliProtocol` 持有、`FlushBuffer` 内 `TryUpdate`（帧级，供本地渲染）。二者刷新节奏不同，互不影响。

> **`defaultFontName` 注入路径【grill 2026-07-14 补】**：`AgentCliProtocol` 当前构造函数（`AgentCliProtocol.cs:64`）只收 `console, ui, terminalSetup, terminalInput`，**无 `ConfigData`**。定案：**把 `ConfigData` 注入 `AgentCliProtocol` 构造函数**，用 `_configData.GetConfigValue<string>(ConfigCode.FontName) ?? ""` 取 `defaultFontName`——与 `Session.cs:37-38` 路径**同一取值表达式**。调用方 `HeadlessRunner.SelectProtocol` 跟着加参（`configData` 在 `HeadlessRunner.cs:64` 的 `using (var scope = GlobalStatic.OpenScope(configData))` 作用域内可见）。理由：与 server 路径一致，避免 `Config.FontName` ambient / scope 隐式依赖引入隐蔽 bug；`ConfigData` 是值对象注入成本极低；未来 CLI 若加 MaxLog 行数上限保护（Phase 2 风险表的 1000 行 diff 基线），注入的 `ConfigData` 直接可用。

#### 4-3 渐进迁移策略（双路径比对）【grill Q9/Q10】

不要一次性切换。每条 `FlushBuffer` 帧上做比对（Q10 帧级粒度）：

1. `TerminalRenderer` 同时持有旧路径（读 `_console.DisplayLineList`）与新路径（读 `DisplayState.Current.lines`）的渲染结果；
2. 新旧路径各写各自的 `BufferedVtScreen`（Q9 的 VT sink 抽象），写入前比对两个 sink 的字节序列；
3. 不一致时 `AgentLog` warning（CI `--strict` 时 `Assert.Fail`）；
4. 确认一致后删除旧路径。

> VT sink 抽象（`IAgentCliVtScreen` + `BufferedVtScreen`）已在 Phase 3-0 完成，此处直接使用。

#### 4-4 验证点

- CLI 交互测试全通过（按钮点击、滚动、CLEARLINE、SET_BG）——复用现有 `test_cli_*.py` 作基线（Phase 0 护栏）
- 双路径比对无 warning（或 `--strict` 下无 fail）= 新路径 VT 输出与旧路径 byte 一致
- `TerminalRenderer` 不再直接引用 `_console.DisplayLineList`（经 `DisplayState.Current.lines`）

---

### Phase 5：清理与协议升级（2-3 天）

**目标**：删除旧路径，升级协议版本。

> **grilling 修正（2026-07-13）**：`_state._pendingOps` 在 Phase 1 后已是 DisplayState 的变更检测 substrate（`PendingOpCount` peek），**不得删除**。`TakePendingOps`/`DrainPendingOpsForCli` 在 ops 字段移除后成死代码可删，但清空职责改由 `TryUpdate()` 消费式承担，避免队列无限增长。

| 任务 | 说明 |
|------|------|
| 5-1 删除 `TurnRecord.ops[]` 旧格式 | Web adapter 完全切换到 `diff` 后，`TurnRecord` 不再含 `ops`；`BuildTurn` 不再 drain ops。须同步把 `emuera_gateway`（Python MCP）与 Python 端到端测试改为消费 `diff`（破坏性升级，见 5-2）。**【grill 2026-07-14 补】** 核查结论：`emuera_gateway` 不消费 ops（grep 无匹配），**网关侧真零改动**（与 Q14 一致）；但 `tests/` 多文件深度消费 ops（`test_jsonl.py` / `test_snapshot.py` / `test_ws.py` / `test_server_single_session.py` / `test_fatal_turn.py`）。这些测试的 v3 断言升级范围须明示：`protocolVersion == 3` → `== 4`、`ops` 字段存在断言 → `diff` 字段存在断言、`ops_text` 对照 → `diff` 应用后状态对照。 |
| 5-2 协议版本升至 v4 | `TurnRecord.CurrentProtocolVersion` 从 3 升至 4，标记 diff 模式；`DisplaySnapshot.protocolVersion` 同步为 4。v3 客户端依赖 `ops` 将失配，须随之升级。`emuera_gateway`（Python MCP）透传 raw JSON 给客户端，网关侧零改动（Q14） |
| 5-3 清理 pendingOps drain API | 删 `TakePendingOps()` 与 `DrainPendingOpsForCli`（均成死代码）；**保留** `_state._pendingOps` 列表 + `ConsolePrintManager` 全部 `Add` 站点 + `PendingOpCount` 属性。改为 `TryUpdate()` 在检测到变更 rebuild 后**清空 `_pendingOps`**（消费式，避免无限增长）。**【grill 2026-07-14 补】前置依赖**：删 `DrainPendingOpsForCli` 前，CLI `FlushBuffer` 必须已改为「从 snapshot 比对推断 CLEARLINE/CLEAR/SET_BG」（见 Phase 4-1 补段），否则全屏事件信号丢失、渲染走错分支。二者须同步落地——`TryUpdate` 消费式清空 `_pendingOps` 后，任何残留的 `DrainPendingOpsForCli` 调用都只会拿到空列表。 |
| 5-4 TerminalRenderer 不再引用 `_console.DisplayLineList` | 确认 DisplayState 是唯一入口（Phase 4 双路径比对通过后删旧路径） |
| 5-5 更新文档 | 在 ADR-0014 记录最终决策；更新显示快照协议 / JSONL 协议规范，注明 v4 + diff 为唯一增量格式、ops 已废弃 |

---

### 风险与回退策略

| 风险 | 影响 | 缓解 |
|------|------|------|
| DisplayDiff 计算性能 | 大显示量下 diff 算法可能比 ops 队列慢 | 基准测试：1000 行 snapshot 的 diff 需 <1ms；超限时 fallback ReplaceAll |
| CLI VT 渲染回归 | TerminalRenderer 是最复杂的模块 | Phase 4 的双路径对比策略；Python CLI 测试覆盖 |
| 协议兼容性 | 旧客户端不识别 diff 字段 | Phase 2 的双模式输出；v4 版本号门控；`emuera_gateway` 透传 raw JSON，网关侧零适配（Q14） |
| ButtonRegionTracker 引用语义变更 | 从对象引用改为 value-based | Phase 3/4 value-based；generation 由服务端 `ConsoleInputHandler` 校验兜底（ConsoleInputHandler.cs:210-267），无正确性回归 |

---

### 总时间线

```
Phase 0  ██                                             1-2 天
Phase 1  █████                                          3-5 天
Phase 2  █████████                                      5-7 天
Phase 3  ██████                                         3-4 天
Phase 4  █████████████                                  5-7 天
Phase 5  ████                                           2-3 天
                                                        --------
                                                        ~20-28 天
```

Phase 1-2 是核心价值——做完这两步，Web 前端就能拿到 diff 数据了。Phase 3-4 是偿还 CLI 的技术债。Phase 5 是收尾。