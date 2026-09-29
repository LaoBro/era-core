# T-022 阶段 3：Shared/ 历史代码 nullable 警告清理（精简工作备忘）
**创建**：2026-07-03  
**范围**：`Emuera.Headless/Shared/**` 子树  
**目标**：逐步撤销 `.editorconfig` 中的 CS86xx 抑制规则，使历史代码 nullable-clean，最终全量通过 `TreatWarningsAsErrors=true` 构建。
## 1. 背景与约束
- **构建环境**：项目已启用 `<Nullable>enable</Nullable>` + `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`。
- **策略**：历史代码（`Shared/`）原则不重构逻辑，只做“让编译器静默”的最小改动，保持运行时行为不变。
- **当前状态**：已完成 CS8605, CS8767, CS8629, CS8601, CS8604, CS8602, CS8600, CS8603, CS8618, CS8625 的清理。
## 2. 已完成清理（模式与修复摘要）
| CS ID | 警告模式 | 核心修复策略 |
| :--- | :--- | :--- |
| **CS8767** | 接口实现的引用类型 null 性与接口定义不匹配（如 `IComparable<T>` 实现）。 | 修改实现成员签名匹配接口（如 `T` → `T?`），并在方法体内补充空值检查（如 `if (other is null) return 1;`）。 |
| **CS8629** | 访问 `Nullable<T>.Value`（主要是 `ScriptPosition?.Value`）。 | 使用 null-forgiving 运算符 `!`，保持原 `.Value` 行为（如 `Position!.Value`）。 |
| **CS8601** | 可能 null 引用赋值（BCL 返回、`as` 表达式、`ref` 参数）。 | 1. **BCL/`as`/三元表达式**：尾部加 `!`（如 `ExePath = Environment.ProcessPath!`）。<br>2. **`ref` 参数**：声明处用 `null!` 初始化（如 `string errMes = null!;`）。 |
| **CS8604** | 可能 null 实参传给非 null 形参。 | 统一在调用点使用 `!` 断言非 null（如 `func(arg!, arg2!)`）。 |
| **CS8602** | 解引用可能为 null 的引用（在 `T?` 上访问成员）。 | 1. **常规**：使用 `!`（如 `node!.SelectNodes()`）。<br>2. **`as` 表达式**：必须用括号包裹 `(expr as Type)!`，**严禁** `as Type!`。<br>3. **编译器限制**：极少数情况用 `#pragma warning disable CS8602`。 |
| **CS8600** | null 转 non-null 类型（显式转换、s 赋值、
ull 字面量初始化、TryGetValue out 参数）。 | 1. **null 字面量赋值**：= null!（如 string x = null!;）。<br>2. **s 表达式**：必须用括号包裹 (expr as Type)!，**严禁** s Type!。<br>3. **方法返回 Type? 赋值**：调用点加 !（如 
eader.ReadLine()!）。<br>4. **TryGetValue out 参数**：声明改 out Type? v，后续使用 !（或依赖 [MaybeNullWhen(false)] 流分析收窄）。<br>5. **显式 cast (T)x**：((T)x)!；编译器限制场景用 #pragma。 |
| **CS8618** | 构造函数未初始化非 null 字段/自动属性（实例字段、静态字段、auto-property `{ get; set; }`、带 protected/private set 的属性、readonly 字段）。同一字段在多个构造函数报告只需在声明处修复一次。 | 在字段/属性声明处添加 `= null!;` 初始化器：<br>1. **字段** `Type x;` → `Type x = null!;`<br>2. **自动属性** `{ get; set; }` → `{ get; set; } = null!;`<br>3. **带 protected/private set** `{ get; protected set; }` → `{ get; protected set; } = null!;`<br>4. **readonly 字段** 同样可加 `= null!;` inline 初始化<br>5. **跳过场景**：已有初始化器（`= ""`/`= new()`/`= []`）、计算属性（无 setter）、abstract 属性。 |
| **CS8625** | null 字面量转换为非 null 引用类型（多种上下文：字段/局部赋值、方法参数、对象初始化器、数组元素、`return null`、Dispose 模式 `field = null`）。 | 统一将警告位置的 `null` 替换为 `null!`（null-forgiving 运算符）：<br>1. **字段/局部赋值** `x = null;` → `x = null!;`<br>2. **方法调用传参** `Method(null)` → `Method(null!)`<br>3. **对象初始化器** `field = null,` → `field = null!,`<br>4. **数组/集合元素** `[null]` → `[null!]`、`new Type[]{null}` → `new Type[]{null!}`<br>5. **构造调用** `new Type(null)` → `new Type(null!)`<br>6. **return** `return null;` → `return null!;`<br>7. **跳过场景**：`== null`/`!= null` 比较表达式、已有 `null!`、字符串/注释中的 null、nullable 类型赋值（`Type? x = null;`）。 |
## 3. 标准操作流程（SOP）
1. **暴露**：`.editorconfig` 中该 CS ID 的 `severity` 改为 `warning`。
2. **定位**：全量构建捕获日志，Grep `CSxxxx` 按文件聚合统计。
3. **修复**：优先使用 `replace_all`；短字符串或复杂上下文使用精确 `Edit`（整行替换）。
4. **验收**：全量构建 `0 警告 0 错误` 后，从 `.editorconfig` 删除该规则行并再次构建确认。
## 4. 踩坑记录（合并精简）
- **构建与分析**：
    - `.editorconfig` 注释中的警告统计数可能过时，一切以实际构建日志为准。
    - `TreatWarningsAsErrors` 会导致依赖链断裂，需“修复一批 → 重建 → 检查”迭代，防止漏报。
    - 同一行多处同类警告可能只报一处，修复时应整行清理。
- **工具操作**：
    - **日志捕获**：推荐 `dotnet build ... 2>&1 | Out-String`，避免重定向丢失输出。
    - **编辑失败**：短字符串 `replace_all` 容易失配，建议使用包含缩进和上下文的精确 `Edit`。
    - **SubAgent 核实**：SubAgent 声称“已修复”不代表实际生效（尤其是 `as!` 语法），需人工 Grep 复核。
- **语法与特性**：
    - **`as Type!` 陷阱**：`!` 会绑定到类型名导致语法错误，必须写为 `(expr as Type)!`。
    - **`null!` 传播性**：`= null!` 仅抑制赋值警告，后续使用编译器仍可能判为 null，需再次加 `!`。
    - **短路求值**：`if (x == null || x.Member...)` 中 `||` 会破坏 nullable 推导，后续使用 `x` 需显式加 `!`。
    - **`!` 不能替代运行时守卫**：`!` 仅抑制编译器警告，不做运行时安全检查。若原有代码用 `Count > 0 &&`、`!= null &&` 等守卫条件保护空值，**不得**将它们替换为 `!`。例如：

```csharp
// ❌ 错误：移除了运行时守卫，! 在空链表上仍是 null，.Value 抛出 NRE
if (selectLine!.IfCaseList!.Last!.Value!.FunctionCode == ...)

// ✓ 正确：保留 Count > 0 守卫，仅对编译器无法推导的非空处加 !
if (selectLine.IfCaseList.Count > 0 && selectLine.IfCaseList.Last!.Value.FunctionCode == ...)
```

  > **典型案例**：T-022 CS8602 清理时误删了 `ErbLoader.nestCheck` 中 `SELECTCASE` 的 `IfCaseList.Count > 0 &&` 守卫，导致所有含 `SELECTCASE`/`CASE`/`CASEELSE` 的 ERB 函数在加载时 NRE。
    - **`#pragma`**：仅在 `!` 无法抑制的编译器分析已知限制场景下使用。
## 5. 修复原则
- **最小改动**：不重构历史逻辑，优先用 `!` 过渡。
- **接口契约**：实现成员签名必须严格匹配接口定义。
- **显式意图**：历史代码“假设非 null”处用 `!` 明确标注。
- **保留运行时守卫**：`!` 不能替代 `Count > 0`、`!= null` 等运行时空值守卫。`!` 只抑制编译器警告，不改变运行时语义。若原守卫是防止 `null`/空集合上的成员访问，必须保留。（参见踩坑记录 §4）
## 6. 剩余清理计划
所有 CS86xx 抑制规则已全部撤销，`.editorconfig` 中 `[Emuera.Headless/Shared/**]` 段不再含任何 CS86xx 严重性覆盖。T-022 阶段 3 nullable 警告清理工作完成。
- **大批量策略**：单批 >200 条时，使用 Grep 按 Top-N 文件聚合，利用 SubAgent 并行分析方案，主会话执行精确 Edit。
## 7. 验证标准
- `dotnet build ... --no-incremental` 结果为 **0 警告 0 错误**。
- 对应 CS ID 已从 `.editorconfig` 删除。
## 8. 进度跟踪
| CS ID | 标注 | 实际 | 状态 |
| :--- | :--- | :--- | :--- |
| CS8605 | 2 | 0 | ✅ 已完成 |
| CS8767 | 6 | 3 | ✅ 已完成 |
| CS8629 | 52 | 26 | ✅ 已完成 |
| CS8601 | 98 | 49 | ✅ 已完成 |
| CS8604 | 176 | 81 | ✅ 已完成 |
| CS8602 | 350 | 318 | ✅ 已完成 |
| CS8600 | 674 | 266 | ✅ 已完成 |
| CS8603 | 606 | 279 | ✅ 已完成 |
| CS8618 | 686 | 286 | ✅ 已完成 |
| CS8625 | 550 | 248 | ✅ 已完成 |
