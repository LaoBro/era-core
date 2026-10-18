# 方言键权威清单

本文是 era-core 当前**已登记的方言键**（`snake` / `eratw` 模块）的权威清单，包含 kind、实现状态、落点与测试。

机器可读真值：

- 运行期可见面：`tools/dialect-inventory/surface.v24pure.txt` / `surface.snake.txt` / `surface.eratw.txt`
- 上游 fork 增量：`dialects/skia.json` + `tools/dialect-inventory/skia-delta-inventory.txt`
- 门禁：`EraCore.Tests/DialectSurfaceSnapshotTests.cs`、`EraCore.Tests/DialectProfileTests.cs`

> 本文与快照不一致时，以**快照 + 测试**为准，并立即修正本文。

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

---

## 2. 指令（17）

| 键 | 家族 | 状态 | 实现 |
|---|---|---|---|
| `TINPUTNF` | NF 输入 | 真语义；`noFocus` 与普通等待等价 | `TINPUT_Instruction` |
| `TINPUTSNF` | NF 输入 | 同上 | `TINPUTS_Instruction` |
| `TONEINPUTNF` | NF 输入 | 同上 | `TINPUT_Instruction` |
| `TONEINPUTSNF` | NF 输入 | 同上 | `TINPUTS_Instruction` |
| `JUMPSTR` | 动态调用 | 真语义（`isJump`） | `CALLS_Instruction` |
| `CALLSTR` | 动态调用 | 真语义 | `CALLS_Instruction` |
| `TRYCALLSTR` | 动态调用 | 真语义（失败不抛） | `CALLS_Instruction` |
| `TRYCJUMPSTR` | 动态调用 | 真语义（TRY-CATCH 配对） | `CALLS_Instruction` |
| `TRYCCALLSTR` | 动态调用 | 真语义（TRY-CATCH 配对） | `CALLS_Instruction` |
| `SETIMAGELAYER` | 图像层 | 参数解析 + ColorMatrix 5×5 校验；控制台 no-op | `SETIMAGELAYER_Instruction` |
| `SETIMAGELAYERL` | 图像层 | 同上（`followScroll` 恒 true） | `SETIMAGELAYER_Instruction` |
| `CLEARIMAGELAYER` | 图像层 | 参数解析；控制台 no-op | `CLEARIMAGELAYER_Instruction` |
| `CLEARIMAGELAYER_ALL` | 图像层 | 控制台 no-op | `CLEARIMAGELAYER_ALL_Instruction` |
| `HTML_PRINTC` | HTML/文本 | 真语义（复用 HTML 管线 + 右对齐单元格） | `HTML_PRINTC_Instruction` |
| `HTML_PRINTLC` | HTML/文本 | 真语义（左对齐单元格） | `HTML_PRINTC_Instruction` |
| `TEXT_BGC_ON` | HTML/文本 | 仅保存 `TextBackgroundColor`；渲染线未消费 | `TEXT_BGC_ON_Instruction` |
| `TEXT_BGC_OFF` | HTML/文本 | 清空 `TextBackgroundColor`；渲染线未消费 | `TEXT_BGC_OFF_Instruction` |

声明点：`EraCore.Core/Shared/Runtime/Script/Statements/FunctionIdentifier.Registry.cs`
处理体：`EraCore.Core/Shared/Runtime/Script/Statements/Instraction.Child.cs`
测试：`P0TinputNfInstructionTests`、`P0DynamicCallInstructionTests`、`P0ImageLayerInstructionTests`、`P0HtmlTextInstructionTests`

---

## 3. 函数（49）

### 3.1 数学 / 取整 / 无检查算术（13）

| 键 | 状态 | 说明 |
|---|---|---|
| `SIN` `COS` `TAN` `ASIN` `ACOS` `ATAN` | 真语义 | 三角函数，返回浮点 |
| `FLOOR` `CEIL` `ROUND` | 真语义 | 取整 |
| `UNCHECKED_ADD` `UNCHECKED_SUB` `UNCHECKED_MUL` `UNCHECKED_NEG` | 真语义 | 无溢出检查算术 |

实现：`Creator.Method.Math.cs`
测试：`P0MathBitEvalFunctionTests`

### 3.2 位运算（4）

| 键 | 状态 | 说明 |
|---|---|---|
| `BITSET` `BITGET` `BITTOGGLE` `BITINDEXOFFIRST` | 真语义 | 基于既有 `BitArrayManager` |

实现：`Creator.Method.Array.cs` / `BitArrayManager.cs`
测试：`P0MathBitEvalFunctionTests`

### 3.3 字符串动态求值（2）

| 键 | 状态 | 说明 |
|---|---|---|
| `EVAL` | 真语义 | 把字符串当数值表达式求值 |
| `EVALS` | 真语义 | 把字符串当字符串表达式求值 |

实现：`Creator.Method.General.cs`
测试：`P0EvalFunctionTests`（需要完整会话的 `EVAL` / `EVALS`）

### 3.4 集合 `MAP_*`（6）

| 键 | 状态 | 说明 |
|---|---|---|
| `MAP_VALUES` `MAP_MERGE` `MAP_REMOVEIF` `MAP_FINDKEY` `MAP_TOSTRING` `MAP_FROMSTRING` | 真语义 | 复用既有 `DataStringMaps` 数据模型 |

实现：`Creator.Method.Map.cs`
测试：`P0MapCollectionFunctionTests`

### 3.5 平台 / 动画定时器（2）

| 键 | 状态 | 说明 |
|---|---|---|
| `GETPLATFORM` | 真语义 | 0=Windows / 1=Android / 2=iOS / 3=macOS / 4=Linux / 5=Unknown，可常量折叠 |
| `GETANIMETIMER` | 真语义 | 返回当前重绘定时器周期（毫秒），未启用为 0 |

实现：`Creator.Method.System.cs`
测试：`P0SqlFunctionTests`（与平台键同批实跑回填）

### 3.6 SQL（22，桌面闭环）

| 分组 | 键 | 状态 |
|---|---|---|
| 连接 | `SQL_CONNECT` `SQL_CONNECTION_OPEN` `SQL_DISCONNECT` `SQL_ESCAPE` | 真语义 |
| 执行 | `SQL_EXECUTE_NONQUERY` `SQL_EXECUTE_READER` `SQL_EXECUTE_SCALAR_LONG` `SQL_EXECUTE_SCALAR_STRING` | 真语义 |
| 参数化执行 | `SQL_P_EXECUTE_NONQUERY` `SQL_P_EXECUTE_READER` `SQL_P_EXECUTE_SCALAR_LONG` `SQL_P_EXECUTE_SCALAR_STRING` | 真语义 |
| Reader | `SQL_READER_READ` `SQL_READER_GET_LONG` `SQL_READER_GET_STRING` `SQL_READER_ISNULL` `SQL_READER_CLOSE` | 真语义 |
| XML 导入导出 | `SQL_IMPORT_MAP_XML` `SQL_IMPORT_DT_XML` `SQL_IMPORT_XML_CUSTOM` `SQL_EXPORT_MAP_XML` `SQL_EXPORT_DT_XML` | 真语义 |

实现：

- 薄包装：`Creator.Method.Sql.cs`（只做参数取值与 `SqlManagerException → CodeEE`）
- 深模块：`EraCore.Core/Shared/Runtime/Utils/Sql/SqlManager.cs`（唯一 `using Microsoft.Data.Sqlite` 的文件）
- 生命周期：`GlobalStatic.Scope.Dispose` → `SqlManager.CloseAll()`

测试：`P0SqlFunctionTests`

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

| 项 | 差异 | 记录 |
|---|---|---|
| 图像层 | 不渲染，仅参数校验 | 本文 §2 |
| 文本背景色 | 只存状态，渲染线未消费 | 本文 §2 |
| NF 输入 | 无强制滚动步，与普通等待等价 | 本文 §2 |
| SQL Float | 3 键未实现 | 本文 §4.1 |
| `EXISTVAR` 两参 | 向后兼容并入 baseline，`v24pure` 也获得 `mode≠0` | `Creator.Method.General.cs`；面快照无 diff |
| eraTW 循环外 `CONTINUE` | 游戏数据缺陷，三份参考引擎同款告警 | `tests/games.json` 的 `knownLv2` |

---

## 6. 如何更新本文

1. 新增/删除方言键 → 先改 descriptor；
2. 跑快照更新（见 [`dialect-porting.md`](../guides/dialect-porting.md) §6.1）；
3. 用快照差值更新 §2 / §3；
4. 若状态从“真语义”变成“no-op”或反之，同步更新 §5；
5. 运行 `dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~Dialect` 复核。

> 本文不复制每个键的完整参数表；参数形状以 `ArgumentBuilder` / `FunctionMethod` 的 remarks 为准。
