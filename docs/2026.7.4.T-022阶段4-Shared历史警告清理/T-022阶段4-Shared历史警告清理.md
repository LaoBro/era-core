# T-022 阶段 4：Shared/ 历史代码非 nullable 警告清理（精简工作备忘）

**创建**：2026-07-04
**范围**：`Emuera.Headless/Shared/**` 子树
**前置**：T-022 阶段 3 已完成所有 CS86xx（nullable 引用类型）抑制规则撤销
**目标**：逐步撤销 `.editorconfig` 中 `[Emuera.Headless/Shared/**]` 段剩余的 CS/CA/SYSLIB 抑制规则，使历史代码尽量通过 `TreatWarningsAsErrors=true` 构建。无法机械清理的规则需明确保留理由。

## 1. 背景与约束

- **构建环境**：`<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>` + `EnableNETAnalyators=true` + `AnalysisMode=Minimum`。
- **策略**：历史代码（`Shared/`）原则不重构逻辑，只做"让编译器静默"的最小改动，保持运行时行为不变。与阶段 3 同构。
- **保留项**：`CA1416`（平台兼容性）**真实触发、非死配置**，保留抑制。真实触发点：`Shared` 内 `ExpressionMediator.cs`/`Creator.Method.cs` 的 `Strings.StrConv`（共 5 处，非 `#if !HEADLESS` 隔离）、`Emuera.Headless` 自有代码 `WindowsTerminalSetup.cs` 的 `Console.BufferWidth/Window*` 等（4 处）。详见 `docs\2026.7.4.T-022阶段4-Shared历史警告清理\CA1416_Shared_分析.md`（2026-07-08 复核）。

## 2. 现状数据（2026-07-04 实际构建所得）

临时将 `[Emuera.Headless/Shared/**]` 段所有抑制改为 `warning` 后构建（`-p:TreatWarningsAsErrors=false`），统计结果：

- **总计 292 条警告 / 22 个 ID / 38 个 (ID, 文件) 对**
- 22 个抑制 ID 中有 **6 个自然消亡**（构建无警告），可直接删除规则行
- 警告呈强文件-ID 聚集：头部 5 文件占总警告数的 65%

### 2.1 按 ID 聚合

| ID | 警告数 | 文件数 | 类别 | 备注 |
| :--- | :---: | :---: | :--- | :--- |
| CA1854 | 84 | 6 | 性能 | `ContainsKey + indexer` → `TryGetValue` |
| CA1822 | 66 | 3 | 性能 | 成员可标记 static（GraphicsImage.cs 占 52） |
| CA1834 | 44 | 7 | 性能 | `StringBuilder.Append("x")` → `Append('x')` |
| CA1069 | 26 | 1 | 可靠性 | 枚举值不应重复（VariableCode.cs） |
| CA1507 | 12 | 1 | 性能 | 字符串字面量 → `nameof(...)` |
| CA1862 | 8 | 2 | 性能 | `string.Equals` 加 `StringComparison` |
| CA2211 | 6 | 1 | 可靠性 | 非私有静态字段（EncodingHandler.cs） |
| CA1514 | 6 | 2 | 性能 | 去掉 `Substring` 冗余长度参数 |
| CA1853 | 6 | 1 | 性能 | 字典 lookup 模式优化（VariableData.cs）；**已于 2026-07-08 清理（实际 3 处，日志重复计为 6；ContainsKey+Remove → 直接 Remove，撤销抑制）** |
| CA1829 | 6 | 1 | 性能 | `.Count()` → `.Length`/`.Count` |
| CA2208 | 4 | 1 | 可靠性 | `ArgumentException` 参数名 |
| CA1846 | 4 | 2 | 性能 | `Substring` → `AsSpan` |
| CS0164 | 2 | 1 | 编译器 | 未引用标签（LogicalLineParser.cs:591） |
| CS0162 | 2 | 1 | 编译器 | 不可达代码（LogicalLineParser.cs:591，与 CS0164 同行） |
| CA1858 | 2 | 1 | 性能 | `IndexOf == 0` → `.StartsWith` |
| CA1845 | 2 | 1 | 性能 | `Substring` → `AsSpan` |
| CA1864 | 2 | 1 | 性能 | `ContainsKey + Add` → `TryAdd` |
| CA1875 | 2 | 1 | 性能 | `Regex.Matches().Count` → `Regex.Count` |
| CA1830 | 2 | 1 | 性能 | StringBuilder 强类型重载 |
| CA2249 | 2 | 1 | 性能 | `IndexOf >= 0` → `.Contains` |
| CA2263 | 2 | 1 | 用法 | 优先泛型方法重载 |
| SYSLIB0014 | 2 | 1 | 过时 API | WebClient → HttpClient（Instraction.Child.cs） |

### 2.2 自然消亡 ID（0 警告，可直接删除规则）

| ID | 注释 |
| :--- | :--- |
| CS0472 | 表达式恒为某值 |
| CS0649 | 字段从未赋值（Shared 子树） |
| CA1806 | 未使用方法返回值 |
| CA1816 | Dispose 未调用 GC.SuppressFinalize |
| CA1835 | `ReadAsync(Memory<byte>)` |
| CA1859 | 变量类型收窄 |
| CA2016 | CancellationToken 传递 |

### 2.3 头部 5 文件按 ID 分布

| 文件 | 总数 | 主 ID（条数） |
| :--- | :---: | :--- |
| GraphicsImage.cs | 52 | CA1822 (52) |
| Creator.Method.cs | 50 | CA1854 (34) + CA1862 (6) + CA1834 (2) + 其他 |
| HtmlManager.cs | 38 | CA1834 (30) + CA1514 (2) + CA1845/CA1846/CA1862 |
| VariableData.cs | 30 | CA1854 (24) + CA1853 (6)；**CA1853 已于 2026-07-08 清理（3 处）** |
| VariableCode.cs | 26 | CA1069 (26) |

## 3. 清理路线图（按批次）

每批沿用阶段 3 SOP：**暴露 → 定位 → 修复 → 验收 → 删除规则 → 再次构建确认**。

### 第 1 批：零源码改动（直接删规则，6 个 ID）

直接从 `.editorconfig` 删除以下行，重建验证 0 警告：

```
dotnet_diagnostic.CS0472.severity = none
dotnet_diagnostic.CS0649.severity = none
dotnet_diagnostic.CA1806.severity = none
dotnet_diagnostic.CA1816.severity = none
dotnet_diagnostic.CA1835.severity = none
dotnet_diagnostic.CA1859.severity = none
dotnet_diagnostic.CA2016.severity = none
```

### 第 2 批：低难度机械批量（~38 条，11 个 ID）

模式化修复，风险低，可考虑 SubAgent 并行：

| ID | 数 | 修复模式 |
| :--- | :---: | :--- |
| CA1834 | 44 | `sb.Append("x")` → `sb.Append('x')`（单字符字符串转 char） |
| CA1829 | 6 | `.Count()` on array/List → `.Length`/`.Count` |
| CA2249 | 2 | `IndexOf(...) >= 0` → `.Contains(...)` |
| CA1858 | 2 | `IndexOf(...) == 0` → `.StartsWith(...)` |
| CA1864 | 2 | `ContainsKey + Add` → `TryAdd` |
| CA1514 | 6 | 去掉 `Substring` 冗余长度参数 |
| CA1845 | 2 | `Substring` → `AsSpan` |
| CA1846 | 4 | `Substring` → `AsSpan` |
| CA1862 | 8 | `string.Equals(..., StringComparer)` 加 `StringComparison` |
| CA1830 | 2 | StringBuilder 强类型重载 |
| CA1875 | 2 | `Regex.Matches(...).Count` → `Regex.Count(...)` |

### 第 3 批：CA1854（84 条，6 文件，需引入 out 变量）

`ContainsKey + indexer` → `TryGetValue` 模式，需注意：
- 引入 `out` 变量后续作用域
- `else` 分支的 fallback 逻辑保持不变
- 涉及文件：Creator.Method.cs(34)、VariableData.cs(24)、其他 4 文件各 ~6 条

### 第 4 批：CA1822（66 条，3 文件，加 static 修饰符）

最大单文件 GraphicsImage.cs(52) 需谨慎：
- 修改方法签名为 `static`，需验证所有调用点已通过类名访问
- 调用点可能存在于其他文件，必须全量构建验证
- 若某方法实际依赖实例状态（被分析器误报），用 `#pragma warning disable CA1822` 局部抑制

### 第 5 批：可靠性类（逐 ID 人工判断）

| ID | 数 | 风险点 |
| :--- | :---: | :--- |
| CA1069 | 26 | 枚举值重复，可能影响序列化/外部契约，需核对 VariableCode.cs 枚举语义 |
| CA2208 | 4 | `ArgumentException` 参数名核对 |
| CA2211 | 6 | EncodingHandler.cs 非私有静态字段改为 `readonly`/`internal`，需检查外部访问 |
| CS0162/CS0164 | 2+2 | LogicalLineParser.cs:591 同行触发，疑 `#if DEBUG` 死代码，判断保留/删除 |
| CA2263 | 2 | 优先泛型方法重载 |
| CA1507 | 12 | 字符串字面量 → `nameof(...)`，需确认字符串与符号名一致 |

### 第 6 批：SYSLIB0014（最后，WebClient → HttpClient 迁移）

- 唯一涉及运行时行为改动的批次
- 涉及文件：Instraction.Child.cs（2 处）
- 需要重写 HTTP 调用：`WebClient.DownloadString` → `HttpClient.GetStringAsync` 等
- 需要确认调用上下文是否同步/异步
- 建议放最后，单独 PR 评审

### 永久保留项

| ID | 理由 |
| :--- | :--- |
| CA1416 | **仅自有代码段保留**：`WindowsTerminalSetup.cs` 的 `Console.Buffer*/Window*`（4 处）仍需抑制。**Shared 段已于 2026-07-08 用方案 B 彻底消除**（5 处 `Strings.StrConv` 替换为跨平台 `StringConverter`，`.editorconfig` Shared 段抑制行已删除） |

## 4. 标准操作流程（SOP）

每批按以下流程：

1. **暴露**：`.editorconfig` 中该 ID 的 `severity` 改为 `warning`。
2. **定位**：全量构建捕获日志（`-p:TreatWarningsAsErrors=false`），Grep `CSxxxx/CAxxxx` 按文件聚合统计。
3. **修复**：优先 `replace_all`；复杂上下文用精确 `Edit`（整行替换）；大批量（>200）用 SubAgent 并行。
4. **验收**：全量构建 `0 警告 0 错误`（恢复 `TreatWarningsAsErrors=true`）。
5. **删除规则**：从 `.editorconfig` 删除该 ID 行。
6. **再次构建确认**：0 警告 0 错误。

## 5. 踩坑记录（沿用阶段 3 + 阶段 4 新增）

- **日志捕获**：`dotnet build ... 2>&1 | Out-String`，避免重定向丢失输出。同一日志可能重复 2 次，实际唯一警告数为显示数的一半。
- **SubAgent 核实**：SubAgent 声称"已修复"不代表实际生效，需人工 Grep 复核。
- **CA1822 调用点**：标记 `static` 前必须全局 Grep 调用点，确认无 `instance.Method()` 调用。
- **CA1069 枚举值**：若枚举被序列化（JSON/二进制），改值会破坏兼容性，需检查是否落盘/读盘。
- **SYSLIB0014**：`WebClient` 是同步阻塞 API，迁移到 `HttpClient` 需注意线程模型，可能需引入 `async` 链路。
- **CA1854 + CS8600 陷阱**：`Dictionary.TryGetValue(key, out T)` 的 `out` 参数带 `[MaybeNullWhen(false)]` 特性，当目标变量是非 null 引用类型（如 `XmlDocument doc`）时，`out doc` 会触发 CS8600。修复模式：用临时变量 + null-forgiving，`if (dict.TryGetValue(key, out var temp)) doc = temp!;`。
- **CA1822 + CS0176 陷阱**：将实例方法/属性改为 static 后，调用方 `instance.StaticMember()` 会触发 CS0176 错误（非警告，构建失败）。修复模式：调用方改为类型名限定 `ClassName.StaticMember()`。C# 不允许实例引用访问 static 成员（与 C++/Java 不同）。HEADLESS 存根方法加 static 后，需全局搜索调用点改为 `GraphicsImage.Method()` 形式。
- **CA1069 + VariableCode 计数器语义**：`VariableCode.__COUNT_*` 系列是有意的计数器边界，每种变量类型从 0 开始独立计数，导致与 `__NULL__`（0x00）或其他 `__COUNT_*` 成员值重复。这些值被强制转换为 int 用作数组长度（如 `new long[(int)VariableCode.__COUNT_INTEGER_ARRAY__]`），改值会破坏数组初始化。存档以枚举名序列化，不依赖数值唯一性。修复方式：在枚举定义前后用 `#pragma warning disable/restore CA1069` 局部抑制，并加注释说明理由。
- **CA2211 + readonly**：`EncodingHandler` 的 `public static Encoding` 字段被 CA2211 标记为"非常量字段应当不可见"。这些字段初始化后不再修改，加 `readonly` 即可满足规则，同时保持 public 可见性。
- **CS0162/CS0164 死代码遗留**：`LogicalLineParser.cs` 中 `err:` 标签原本被 `goto err` 引用，goto 被注释后遗留了未引用标签（CS0164）和死代码 return（CS0162）。catch 块已处理异常路径，直接删除 `err:` 标签和后续 return 语句。
- **SYSLIB0014 + HttpClient 迁移**：`Instraction.Child.cs` 的 `UPDATECHECK_Instruction` 使用 `WebClient.OpenRead` 同步阻塞读取更新检查响应。迁移方案：在类内加 `private static readonly HttpClient s_updateCheckClient = new();` 静态单例（HttpClient 设计上应复用，避免 socket 耗尽），用 `GetStreamAsync(url).GetAwaiter().GetResult()` 同步阻塞替代 `OpenRead(url)`。Headless 为控制台程序，无 SynchronizationContext，`.GetAwaiter().GetResult()` 不会死锁。原 `wc.Dispose()` 调用全部删除（静态单例不 Dispose）。需新增 `using System.Net.Http;`（`System.Net` 仍保留，因 `NetworkInterface.GetIsNetworkAvailable()` 依赖）。运行时行为等价：同步 HTTP GET 读取两行文本。

## 6. 修复原则

- **最小改动**：不重构历史逻辑，优先机械修复。
- **保留运行时行为**：`static` 化、`TryGetValue` 改写不得改变执行路径。
- **接口契约**：实现成员签名必须严格匹配接口定义。
- **永久保留项需注释**：保留抑制行需在 `.editorconfig` 注释中说明理由。`CA1416` 为真实触发的保留项（详见上文），当前 Shared 段仅此一项。

## 7. 验证标准

- `dotnet build Emuera.Headless/Emuera.Headless.csproj --no-incremental` 结果为 **0 警告 0 错误**。
- 对应 ID 已从 `.editorconfig` 删除（保留项除外）。
- `[Emuera.Headless/Shared/**]` 段最终仅保留 `CA1416`（真实触发，Shared 内 Strings.StrConv 5 处），仅后续新增的合理保留项按需追加。

## 8. 进度跟踪

| 批次 | ID | 标注 | 实际 | 状态 |
| :--- | :--- | :---: | :---: | :--- |
| 1 | CS0472/CS0649/CA1806/CA1816/CA1835/CA1859/CA2016 | 0 | 0 | ✅ 已完成 |
| 2 | CA1834/CA1829/CA2249/CA1858/CA1864/CA1514/CA1845/CA1846/CA1862/CA1830/CA1875 | 38 | 80 | ✅ 已完成 |
| 3 | CA1854 | 84 | 84 | ✅ 已完成 |
| 4 | CA1822 | 66 | 66 | ✅ 已完成 |
| 5 | CA1069/CA2208/CA2211/CS0162/CS0164/CA2263/CA1507 | 50 | 27 | ✅ 已完成 |
| 6 | SYSLIB0014 | 2 | 1 | ✅ 已完成 |
| - | CA1853 | 6 | 3 | ✅ 已完成（2026-07-08，VariableData.cs 3 处 ContainsKey+Remove → 直接 Remove，撤销抑制） |
| - | CA1416（保留） | 5+4 | 9 | ⏸️ 自有代码段 4 处保留抑制；**Shared 段 5 处 2026-07-08 已消除（方案 B）** |

---

## 9. CA1416 消除专项（2026-07-08 新增计划）

> 阶段 4 主线（批次 1–6）已全部完成。CA1416 原是唯一的保留项，且为**真实触发**（非死配置）。
> 本节原计划将其从 Shared 段彻底消除，作为后续独立任务。**2026-07-08 已按方案 B 执行完成**（见 9.7）。

### 9.1 真实触发点清单

Shared 段（5 处，全为 `Microsoft.VisualBasic.Strings.StrConv`，日文假名/全半角转换，仅 Windows 支持）：

| 文件 | 行 | 调用 | 语义 |
| :--- | :---: | :--- | :--- |
| `Runtime/Script/Statements/ExpressionMediator.cs` | 69 | `Strings.StrConv(str, VbStrConv.Katakana, 0x0411)` | 片假名化 |
| `Runtime/Script/Statements/ExpressionMediator.cs` | 73 | `Strings.StrConv(str, VbStrConv.Hiragana \| VbStrConv.Wide, 0x0411)` | 平假名 + 全角 |
| `Runtime/Script/Statements/ExpressionMediator.cs` | 75 | `Strings.StrConv(str, VbStrConv.Hiragana, 0x0411)` | 平假名化 |
| `Runtime/Script/Statements/Function/Creator.Method.cs` | 4571 | `Strings.StrConv(str, VbStrConv.Narrow, Config.Language)` | 半角化（STR_FORM） |
| `Runtime/Script/Statements/Function/Creator.Method.cs` | 4573 | `Strings.StrConv(str, VbStrConv.Wide, Config.Language)` | 全角化（STR_FORM） |

> 注意：这些调用**不在** `#if !HEADLESS` 分支内（I-14 隔离未覆盖），故在 Headless 编译路径中真实参与编译并触发 CA1416。
> 自有代码段另有 4 处（`WindowsTerminalSetup.cs` 的 `Console.BufferWidth/BufferHeight/WindowWidth/WindowHeight` .set），
> 不在本 Shared 专项范围内，需另行处理（见 9.5）。

### 9.2 方案对比

| 方案 | 做法 | 评价 |
| :--- | :--- | :--- |
| **A. 平台守卫 + 跨平台回退** | `if (OperatingSystem.IsWindows()) return Strings.StrConv(...); else return CrossPlatformConvert(...);` | ✅ 真消除 CA1416；需为非 Windows 实现等价转换（Unicode 映射表）；Headless 实际只跑 Windows，回退分支极少执行 |
| **B. 整体替换 StrConv** | 自实现跨平台全半角/假名转换，彻底去掉 `Microsoft.VisualBasic` 依赖 | ✅ 最干净，顺带减依赖；需保证与 VB `LCMapString` 行为一致（尤其日文），需测试 |
| C. 加 `[SupportedOSPlatform("windows")]` | 给 `ConvertStringType` / `GetStrValue` 打特性 | ⚠️ 仅把警告上推给调用方，未真消除；且与 Headless `net10.0` 跨平台定位矛盾，**不采用** |
| D. 保留抑制（现状） | 不动 | 当前务实做法，属"掩盖"非"解决" |

### 9.3 推荐执行方案：B（整体替换）

理由：一次性消除 Shared 段 CA1416 并移除 `Microsoft.VisualBasic` 依赖，避免方案 A 的双实现维护成本；
转换语义明确（全角↔半角、片假名↔平假名均为 Unicode 码点区间映射），可实现为纯跨平台函数。

**实现要点**：
1. 新建 `Shared/Runtime/Utils/StringConverter.cs`（或并入既有工具类），提供：
   - `ToFullWidth(string)` / `ToHalfWidth(string)`：映射 `U+0021–U+007E` ↔ `U+FF01–U+FF5E`（含空格 `U+0020 ↔ U+3000`）。
   - `ToKatakana(string)` / `ToHiragana(string)`：映射平假名 `U+3041–U+3096` ↔ 片假名 `U+30A1–U+30F6`。
   - 组合标志（`Hiragana | Wide`）按位分步应用。
2. `ExpressionMediator.ConvertStringType`：三处 `Strings.StrConv` → 调用上述函数（保持 `0x0411` 日文 locale 语义）。
3. `Creator.Method.cs` `GetStrValue`（STR_FORM 的 Half/Full 分支）：`VbStrConv.Narrow/Wide` → `ToHalfWidth/ToFullWidth`；`Config.Language` 参数可忽略（跨平台实现与 locale 无关，但需确认日文游戏场景下无差异）。
4. **不引入行为回归**：原 `Strings.StrConv` 对空/非字母字符原样返回，自实现需保持同等退化行为。

### 9.4 验收门槛（与阶段 4 一致）

1. 修改后全量构建 `0 警告 0 错误`（`TreatWarningsAsErrors=true`，**务必用真实配置，勿用 `-p:AnalysisMode=All` 覆盖**，否则会漏报）。
2. 从 `.editorconfig` `[Emuera.Headless/Shared/**]` 段删除 `dotnet_diagnostic.CA1416.severity = none` 行，重建确认 Shared 不再触发 CA1416（0 个）。
3. **行为一致性测试**：选取含 `STR_FORM` 全/半角、`@ 片假名`/`@ 平假名` 转换的 ERB 脚本，在 Windows 上对比替换前后输出字节一致；建议补充单元测试覆盖 `StringConverter` 各映射。
4. 同步将本节结论回填进度跟踪表（第 8 节 CA1416 行由"保留"改为"已消除"）。

### 9.5 范围外（不在本专项）

- 自有代码 `WindowsTerminalSetup.cs` 的 4 处 `Console.Buffer*/Window*`：需单独任务，方案同 A（守卫 + 跨平台终端尺寸获取回退）或 B。
- 消除后 `.editorconfig` `[Emuera.Headless/**]`（自有代码段）的 CA1416 抑制行保留至该任务完成。

### 9.6 踩坑提醒

- **切勿凭 `-p:AnalysisMode=All` 全量构建的"0 CA1416"判定抑制可删**：该覆盖会改变分析器行为/被缓存，曾导致误判死配置、删抑制后真实构建 9 错误。验证必须基于**项目真实配置** + **真实触发点**。
- 实现跨平台转换时禁用 `Microsoft.VisualBasic` 后，检查 `Shared` 内是否还有其他 `Strings.` / `Microsoft.VisualBasic.` 引用（若有，一并迁移或保留依赖）。

### 9.7 执行结果（2026-07-08 已完成 ✅）

**方案 B 已落地**，Shared 段 CA1416 彻底消除，真实配置全量构建 **0 警告 0 错误**（CA1416 = 0）。

**改动清单**
1. 新增 `Shared/Runtime/Utils/StringConverter.cs`（纯跨平台，无 Windows/`Microsoft.VisualBasic` 依赖）：
   - `internal enum StrConvFlags`（值与 `VbStrConv` 对齐：Katakana=16 / Hiragana=32 / Wide=4 / Narrow=8 / Hiragana|Wide 组合）。
   - `Convert(string, StrConvFlags, int locale)`：按位分步应用，调用形态与原 `Strings.StrConv(str, flags, locale)` **完全一致**（同标志、同 locale 参数），行为差异完全由实现决定。
   - `ToKatakana` / `ToHiragana`（平片假名偏移 0x60，扩展假名 30F4–30F6 与长音记号 30FC 不转换，与 VB 一致）、`ToFullWidth` / `ToHalfWidth`（ASCII `U+0021–U+007E ↔ U+FF01–U+FF5E`、半角片假名 `U+FF61–U+FF9F` 经 NFKC 合成预成字；命名匹配 §9.3 规范要求的 `ToFullWidth`/`ToHalfWidth`）。
   - 关键细节：反斜杠 `U+005C` 与全角反斜杠 `U+FF3C` 不做全半角互换（VB 在日语区域下保留）；基础字 + 半角浊点/半浊点组合经 NFKC 合成预成字；日元 `U+00A5` 全局归一化为 `U+005C`，全角日元 `U+FFE5` 仅在 HalfWidth 归一化（与 VB 行为一致）。
2. `ExpressionMediator.cs`：3 处 `Strings.StrConv(...)` → `StringConverter.Convert(...)`，并移除 `using Microsoft.VisualBasic;`。
3. `Creator.Method.cs`：2 处 `Microsoft.VisualBasic.Strings.StrConv(...)` → `StringConverter.Convert(...)`（STR_FORM 的 Half/Full 分支）。
4. `.editorconfig`：`[Emuera.Headless/Shared/**]` 段删除 `dotnet_diagnostic.CA1416.severity = none`（注释改为说明已消除）；`[Emuera.Headless/**]` 自有代码段 4 处抑制保留。

**行为一致性验证（关键）**
- 用 Windows 上真实的 `Microsoft.VisualBasic.Strings.StrConv` 作 oracle，对 **2455 组**输入（标准假名单字符全扫描、ASCII/半角片假名、浊点/半浊点组合、现实短语、反斜杠/日元变体，5 种标志组合 × 日语 locale 0x0411）逐项比对：`MISMATCH=0, ORACLE_ERR=0`。
- **已知与 VB 的有意差异（改进而非回归）**：VB6 的 `LCMapString` 对扩展兼容假名 `U+3095/U+3096/U+30F4–U+30FA` 输出字面 `?`（VB bug，原 Emuera 亦有此 bug）；`StringConverter` 保留这些字符原样（如 `関ヶ原` 的 `ヶ` 不再变成 `?`）。标准假名与 ASCII 全半角 100% 一致。

**遗留**
- 自有代码段 `WindowsTerminalSetup.cs` 的 4 处 `Console.Buffer*/Window*` 仍为 CA1416 保留项，需另立任务消除（方案 A：平台守卫 + 跨平台终端尺寸回退）。
- `Shared` 内已无任何 `Microsoft.VisualBasic` 代码引用；项目级 `Microsoft.VisualBasic` 包引用保留（非 Shared 部分可能仍用，且移除包引用超出本专项范围）。
