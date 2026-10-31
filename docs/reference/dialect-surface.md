# 方言键权威清单

本文是 era-core 当前**已登记的方言键**（`snake` / `eratw` 模块）的权威清单：逐键的 kind、模块、家族、移植状态与说明由生成物 [`dialect-keys.md`](dialect-keys.md) 提供；本文只保留口径、已知差异与范围外说明。

机器可读真值：

- 键索引（生成物，全部 633 键）：[`dialect-keys.md`](dialect-keys.md)
- 运行期可见面：`tools/dialect-inventory/surface.v24pure.txt` / `surface.snake.txt` / `surface.eratw.txt`
- 上游 fork 增量：`dialects/skia.json` + `tools/dialect-inventory/skia-delta-inventory.txt`
- 门禁：`EraCore.Tests/DialectSurfaceSnapshotTests.cs`、`EraCore.Tests/DialectKeyIndexTests.cs`、`EraCore.Tests/DialectDescriptorMetadataTests.cs`、`EraCore.Tests/DialectForkConsistencyTests.cs`、`EraCore.Tests/DialectProfileTests.cs`

> 本文与生成物/快照不一致时，以**生成物 + 测试**为准，并立即修正本文。

---

## 1. 口径

| 项 | 值 |
|---|---|
| `v24pure` 面 | 567 行（baseline） |
| `snake` / `eratw` 面 | 各 633 行 |
| 方言增量 | **66 键**（17 指令 + 49 函数） |
| `snake` 模块声明键 | 66 |
| `eratw` 模块声明键 | 0（当前为空） |

`surface.*.txt` 每行 `name<TAB>kind`，按行序号排序，UTF-8 / LF。`kind` 只有 `directive` 与 `function` 两种。

`dialect-keys.md` 行口径：方言键 = 键 / kind / 模块 / 家族 / 状态 / 说明；baseline = 键 / kind / baseline / 处理体类型名。两条生成路径共用 `ERACORE_DIALECT_SURFACE_UPDATE=1` 开关。

---

## 2. 指令（17）

17 条指令的逐键元数据见 [`dialect-keys.md`](dialect-keys.md)。

- 声明点：`EraCore.Core/Shared/Runtime/Script/Statements/FunctionIdentifier.Registry.cs`
- 处理体：`EraCore.Core/Shared/Runtime/Script/Statements/Instraction.Child.cs`
- 家族测试：`P0TinputNfInstructionTests`、`P0DynamicCallInstructionTests`、`P0ImageLayerInstructionTests`、`P0HtmlTextInstructionTests`

---

## 3. 函数（49）

49 条函数的逐键元数据见 [`dialect-keys.md`](dialect-keys.md)。分组与实现：

- 数学 / 取整 / 无检查算术（13）：`Creator.Method.Math.cs`
- 位运算（4）：`Creator.Method.Array.cs` / `BitArrayManager.cs`
- 字符串动态求值（2）：`Creator.Method.General.cs`
- 集合 `MAP_*`（6）：`Creator.Method.Map.cs`
- 平台 / 动画定时器（2）：`Creator.Method.System.cs`
- SQL（22，桌面闭环）：`Creator.Method.Sql.cs` + `EraCore.Core/Shared/Runtime/Utils/Sql/SqlManager.cs`

测试：`P0MathBitEvalFunctionTests`、`P0EvalFunctionTests`、`P0MapCollectionFunctionTests`、`P0SqlFunctionTests`（含平台定时器两键）。

---

## 4. 未实现 / 明确排除

### 4.1 SQL Float 变体（3）

| 键 | 原因 |
|---|---|
| `SQL_READER_GET_FLOAT` | 依赖浮点返回/浮点变量支持 |
| `SQL_EXECUTE_SCALAR_FLOAT` | 同上 |
| `SQL_P_EXECUTE_SCALAR_FLOAT` | 同上 |

eraTW 实测未引用这三个键；其余 22 个非 Float 键已覆盖。

### 4.2 Skia 渲染 / 性能层

以下键存在于 `dialects/skia.json` 的 fork delta，但**不属于本移植范围**：

- 渲染实现：`SET_SKIA_QUALITY`、`GET_SKIA_QUALITY`、`SET_TEXT_DRAWING_MODE`、`GET_TEXT_DRAWING_MODE`、`STRICT_FONT_FALLBACK`、`TEXT_BGC_*` 的观感部分；
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
| NF 输入族 | — | 无强制滚动步，与普通等待等价（真语义，Note 记录） | `dialect-keys.md` |
| SQL Float | — | 3 个 Float 变体未实现 | §4.1 |
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
