# 方言键权威清单

本文是 era-core 当前**已登记的方言键**（`snake` 模块；`eratw` 为别名）的权威清单：逐键的 kind、模块、家族、移植状态与说明由生成物 [`dialect-keys.md`](dialect-keys.md) 提供；本文只保留口径、已知差异与范围外说明。

机器可读真值：

- 键索引（生成物，全部 668 键）：[`dialect-keys.md`](dialect-keys.md)
- 运行期可见面：`tools/dialect-inventory/surface.v24pure.txt` / `surface.snake.txt`（`eratw` 别名复用 snake 快照）
- 上游 fork 增量：`dialects/skia.json` + `tools/dialect-inventory/skia-delta-inventory.txt`
- 门禁：`EraCore.Tests/DialectSurfaceSnapshotTests.cs`、`EraCore.Tests/DialectKeyIndexTests.cs`、`EraCore.Tests/DialectDescriptorMetadataTests.cs`、`EraCore.Tests/DialectForkConsistencyTests.cs`、`EraCore.Tests/DialectProfileTests.cs`

> 本文与生成物/快照不一致时，以**生成物 + 测试**为准，并立即修正本文。

---

## 1. 口径

| 项 | 值 |
|---|---|
| `v24pure` 面 | 567 行（baseline） |
| `snake` 面 | 668 行 |
| 方言增量 | **101 键**（21 指令 + 80 函数） |
| `snake` 模块声明键 | 101 |
| `eratw` 别名 | 复用 `snake`（无独立模块 / 快照） |

`surface.*.txt` 每行 `name<TAB>kind`，按行序号排序，UTF-8 / LF。`kind` 只有 `directive` 与 `function` 两种。

`dialect-keys.md` 行口径：方言键 = 键 / kind / 模块 / 家族 / 状态 / 说明；baseline = 键 / kind / baseline / 处理体类型名。两条生成路径共用 `ERACORE_DIALECT_SURFACE_UPDATE=1` 开关。

---

## 2. 指令（21）

21 条指令的逐键元数据见 [`dialect-keys.md`](dialect-keys.md)。

- 声明点：`EraCore.Core/Shared/Runtime/Script/Statements/FunctionIdentifier.Registry.cs`
- 处理体：`EraCore.Core/Shared/Runtime/Script/Statements/Instraction.Child.cs`
- 家族测试：`P0TinputNfInstructionTests`、`P0DynamicCallInstructionTests`、`P0ImageLayerInstructionTests`、`P0HtmlTextInstructionTests`、`P0TryJumpStrFlagTests`、`P0RenderSettingsInstructionTests`

---

## 3. 函数（80）

80 条函数的逐键元数据见 [`dialect-keys.md`](dialect-keys.md)。分组与实现：

- 数学 / 取整 / 无检查算术（13）：`Creator.Method.Math.cs`
- 位运算（4）：`Creator.Method.Array.cs` / `BitArrayManager.cs`
- 字符串动态求值（6，含 `STRFORMCHECK`；`EVALF` / `GETVARF` / `GETMETHF` 为票 36 浮点分支）：`Creator.Method.General.cs` / `Creator.Method.System.cs`
- 字符串 ↔ 浮点（2，`TOFLOAT` / `TOSTRF`，票 27c）：`Creator.Method.String.cs`
- 数组搜索（2，`MATCHALL` / `MATCHALLEX`）：`Creator.Method.General.cs`
- 集合 `MAP_*`（6）：`Creator.Method.Map.cs`
- 平台 / 动画定时器（2）：`Creator.Method.System.cs`
- 渲染设置 getter（2，`GET_TEXT_DRAWING_MODE` / `GET_SKIA_QUALITY`，票 35）：`Creator.Method.System.cs`
- 数据表浮点读（1，`DT_CELL_GETF`，票 36）：`Creator.Method.DataTable.cs`
- 模板反查（5，`GETCSVNOBY*` 四键 + `EXIST_IN_CSV`，票 37）：`Creator.Method.General.cs` + `ConstantData`
- 图像层查询（2，`EXISTSIMAGELAYER` / `GETLINEY`，票 38）：`Creator.Method.System.cs` + `EmueraConsole`
- 音频查询 / 控制（5，票 39，确定性 no-op）：`Creator.Method.System.cs`
- 多边形 / 动态精灵（5，票 40，no-op）：`Creator.Method.Graphics.cs`
- SQL（25，桌面闭环）：`Creator.Method.Sql.cs` + `EraCore.Core/Shared/Runtime/Utils/Sql/SqlManager.cs`

签名增量（票 27c，键名不变、面快照不新增）：`SUMARRAY` / `MAXARRAY` / `MINARRAY` / `MATCH` / `FINDELEMENT` / `FINDLASTELEMENT` 增加 `double[]` 分支；求和/最大/最小返回 Float，匹配/查找仍为 Integer。

测试：`P0MathBitEvalFunctionTests`、`P0EvalFunctionTests`、`P0MapCollectionFunctionTests`、`P0SqlFunctionTests`（含平台定时器两键）、`P0FloatReflectionFunctionTests`（票 36 四键）、`P0CsvReverseLookupFunctionTests`（票 37 五键）、`P0ImageLayerQueryFunctionTests`（票 38 两键）、`P0AudioControlFunctionTests`（票 39 五键）、`P0GraphicsPolygonFunctionTests`（票 40 五键）、`P0PilotAutomationTests`（含 `STRFORMCHECK` / `MATCHALL` / `MATCHALLEX`）。

---

## 4. 未实现 / 明确排除

### 4.1 Skia 渲染 / 性能层

以下键存在于 `dialects/skia.json` 的 fork delta，但**不属于本移植范围**：

- 渲染观感：`TEXT_BGC_*` 的实际绘制、图像质量 / 字体 hinting / edging 的视觉消费（键已登记，见 §5）；
- 机制/性能：`BITMAP_CACHE_ENABLE`、lazyload、`SparseArray`、`SELECTCASE` 跳转表、Preload、SoundTouch；
- eraTW 未引用的其它 Skia-only 键。

完整 fork 键面见 `dialects/skia.json` 与 `tools/dialect-inventory/skia-delta-inventory.txt`；是否需要补入按“游戏实测引用 + 解析必需”裁决，不做整包追平。

---

## 5. 已知差异（语义层）

> 与 descriptor 的 `Status` / `Note` 互补：`Status != Semantic` 的键必须在本区**逐键点名**（`DialectDescriptorMetadataTests` 锁定）；Semantic 但带差异的键只在生成物的 `Note` 记录，本区按族/项概述、不点名具体键。

| 项 | 键 | 差异 | 记录 |
|---|---|---|---|
| 图像层 | `SETIMAGELAYER` `SETIMAGELAYERL` `CLEARIMAGELAYER` `CLEARIMAGELAYER_ALL` | 不渲染，仅参数解析校验（`AcceptedNoOp`） | `dialect-keys.md` |
| 文本背景色 | `TEXT_BGC_ON` `TEXT_BGC_OFF` | 只存状态，渲染线未消费（`StateOnly`） | `dialect-keys.md` |
| 画质 / 字体渲染 | `SET_SKIA_QUALITY` | 三项质量设置只存状态，渲染线未消费（`StateOnly`）；`FontFactory.ClearFont()` 已执行 | `dialect-keys.md` |
| 严格字体回退 | `STRICT_FONT_FALLBACK` | 只存开关，era-core 字形渲染线未消费（`StateOnly`） | `dialect-keys.md` |
| 音频查询 / 控制 | `ISPLAYINGSOUND` `ISPLAYINGBGM` `GETSOUNDORBGMINFO` `SOUNDCONTROL` `BGMCONTROL` | era-core 音频为空实现：按 Skia 返回码做确定性 no-op，不产生声音、不改播放状态（`AcceptedNoOp`） | `dialect-keys.md` |
| 多边形 / 动态精灵 | `G_POLYGON_POINT_ADD` `G_POLYGON_POINT_CLEAR` `G_POLYGON_DRAW` `G_POLYGON_FILL` `SPRITECREATEFROMFILE` | `GraphicsImage` 为 no-op 桩：只做参数校验与确定性返回（已创建 1 / 否则 0；精灵恒 0），不绘制、不建精灵（`AcceptedNoOp`） | `dialect-keys.md` |
| NF 输入族 | — | 无强制滚动步，与普通等待等价（真语义，Note 记录） | `dialect-keys.md` |
| `EXISTVAR` 两参 | — | 向后兼容并入 baseline，`v24pure` 也获得 `mode≠0`；面快照无 diff | `Creator.Method.General.cs` |
| eraTW 循环外 `CONTINUE` | — | 游戏数据缺陷，三份参考引擎同款告警 | `tests/games.json` 的 `knownLv2` |

---

## 6. 如何更新本文

1. 新增/删除方言键 → 只改 descriptor（`Family` / `Status` / `Note` 一并填写）；
2. 设 `ERACORE_DIALECT_SURFACE_UPDATE=1`，运行 `DialectSurfaceSnapshotTests` 与 `DialectKeyIndexTests`，一次重写面快照与键索引；
3. 评审两个生成物的 diff（键增删、家族/状态/说明变化）；
4. 若 `Status` 或已知差异变化，同步修订 §4 / §5 散文；
5. 运行 `dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~Dialect` 复核。

> 本文不复制每个键的完整参数表；参数形状以 `ArgumentBuilder` / `FunctionMethod` 的 remarks 为准。
