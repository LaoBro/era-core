# 方言指令移植指南

本文回答“**如何把其他 Emuera 分支的指令/函数移植进 era-core**”，以及“**第 N 次移植**怎样只加一份描述符和一条 fixture”。

适用读者：需要在 `EraCore.Core` 增加内置指令/式中函数、增加游戏适配键、或接入新 fork 的开发者。

相关文档：

- 运行需要方言的游戏：[`running-eratw.md`](running-eratw.md)
- 66 个方言键的索引（生成物 [`dialect-keys.md`](../reference/dialect-keys.md)）与口径/已知差异（[`dialect-surface.md`](../reference/dialect-surface.md)）
- 架构总览：[`../../ARCHITECTURE.md`](../../ARCHITECTURE.md)

---

## 0. 一句话模型

> 新增一条指令/函数 = **一条 descriptor（带方言标签）** + **一个处理体** + **测试/快照**。
> `funcDic`、`methodList`、`IdentifierDictionary` 名称表都由 descriptor 生成；profile 只做会话级可见面减法，解析/执行热路径不做 profile 判断。

---

## 1. 分层与单一真相源

```text
descriptor（Dialect = Baseline | Snake | Eratw）
   │  指令：FunctionIdentifier.Registry.cs
   │  函数：Function/Creator.Registry.cs
   ▼
静态注册表
   │  FunctionIdentifier.funcDic（指令 + methodList 合并的 METHOD 项）
   │  FunctionMethodCreator.methodList（式中函数）
   ▼
DialectRegistry：模块 → profile 闭包 → 不可变 DialectProfile（按 id 缓存）
   │  v24pure = [baseline]
   │  snake   = [baseline, snake]
   │  eratw   = [baseline, snake, eratw]
   ▼
GlobalStatic.OpenScope(configData, profileId)：在构造 Process 之前绑定
   ▼
IdentifierDictionary 构造期：DialectProjection.Filter 一次性过滤
   │  instructionDic + methodDic → nameDic
   ▼
解析器 / 执行器只读过滤后的表（零 profile 分支）
```

### 文件地图

| 职责 | 文件 |
|---|---|
| 指令 descriptor（唯一声明点） | `EraCore.Core/Shared/Runtime/Script/Statements/FunctionIdentifier.Registry.cs` |
| 函数 descriptor（唯一声明点） | `EraCore.Core/Shared/Runtime/Script/Statements/Function/Creator.Registry.cs` |
| descriptor 类型 + 方言枚举 + `PortStatus` | `EraCore.Core/Shared/Runtime/Script/Statements/BuiltInRegistry.cs` |
| 指令处理体 | `EraCore.Core/Shared/Runtime/Script/Statements/Instraction.Child.cs`（跳转解析另有 `.JumpResolution.cs`） |
| 函数处理体 | `EraCore.Core/Shared/Runtime/Script/Statements/Function/Creator.Method.*.cs` |
| 参数形状 | `FunctionArgType.cs` / `ArgumentBuilder.cs` / `Argument.cs` |
| profile 闭包与模块 | `DialectRegistry.cs` / `DialectProfile.cs` |
| 面快照序列化 | `DialectSurface.cs` |
| 投影接缝 | `EraCore.Core/Shared/Runtime/Script/Data/IdentifierDictionary.cs` |
| 会话绑定 | `EraCore.Core/GlobalStatic.cs` |
| 控制台语义接缝 | `EraCore.Core/UI/Game/EmueraConsole.cs` |
| SQL 深模块 | `EraCore.Core/Shared/Runtime/Utils/Sql/SqlManager.cs` |
| 上游 delta 工具 | `tools/dialect-inventory/extract.py` |
| fork 描述符 | `dialects/<forkId>.json` |
| 面快照 | `tools/dialect-inventory/surface.<profile>.txt` |
| 键索引（生成物） | `docs/reference/dialect-keys.md`（门禁 `DialectKeyIndexTests`） |
| fork 进度报告 | `tools/dialect-inventory/extract.py --status`（门禁 `DialectForkConsistencyTests`） |
| 家族测试 | `EraCore.Tests/P0*.cs`、`Dialect*.cs` |
| 一致性套件 | `tests/conformance_kit.py`、`tests/games.json` |

---

## 2. 先判断归属：Baseline / Snake / Eratw

| 情况 | 归属 | 处理 |
|---|---|---|
| era-core 移植前的既有键 | `Baseline` | 不写标签；默认可见 |
| 上游 fork 存在的语义键（如 Skia 的 `TINPUTNF`、`SQL_*`） | `Snake` | 写 descriptor + `Dialect.Snake` |
| 只服务于某个游戏、上游不存在的兜底键 | `Eratw` | 写 descriptor + `Dialect.Eratw`，默认在 `eratw` profile 可见 |
| 已有键的**向后兼容**签名扩展（只新增可选尾参，不改既有语义/返回/副作用） | `Baseline` | 直接改实现，**不加标签**，面快照保持不变 |
| 已有键的**不兼容**签名变化（改参数语义/返回值） | 暂不实现 | 需要先扩展投影为“同名多 variant 择一”，见 §9 |
| 渲染/性能层键（SkiaSharp 管线、lazyload、`SET_SKIA_QUALITY` 等） | 不移植 | 在 fork JSON 标 `status: out-of-scope`，不登记 descriptor |

> 判据：**上游是否存在该行为**。有 → 方言面（`snake`）；没有（游戏专属）→ 游戏适配（`eratw`）。
> 注意：运行时 `DialectModule.Owner` 默认取模块名（`Snake` / `Eratw`），`dialects/*.json` 里的 `owner: skia` 是**库存工具元数据**，两者不是同一字段。

---

## 3. 移植一条指令

### 3.1 写 descriptor

在 `FunctionIdentifier.Registry.cs` 的 `BuiltInInstructionDescriptors` 中追加：

```csharp
new InstructionDescriptor
{
    Name = "TINPUTNF",
    CreateInstruction = () => new TINPUT_Instruction(false, noFocus: true),
    Dialect = Dialect.Snake,
    Family = "NF 输入",
    Note = "无强制滚动步，与普通等待等价（已知差异）",
},
```

字段说明：

| 字段 | 用法 |
|---|---|
| `Name` | ERB 中出现的大写指令名；同时是 `funcDic` 的键 |
| `CreateInstruction` | 处理体工厂；可复用既有私有嵌套类，也可新建 |
| `AdditionalFlag` | 在 `AInstruction` 自带 flag 之上追加（如 `EXTENDED`、`IS_PRINT`） |
| `Code` | **新增方言指令保持默认 `FunctionCode.__NULL__`**；只有迁移既有结构化指令才填真实码 |
| `Dialect` | `Baseline` / `Snake` / `Eratw` |
| `Family` | 检索分组；非空值必须 ∈ canonical 10 词（`DialectDescriptorMetadataTests` 锁定），未归类留 `null` |
| `Status` | `Semantic`（默认）/ `AcceptedNoOp` / `StateOnly`；非默认值必须在 [`dialect-surface.md`](../reference/dialect-surface.md) §5 点名 |
| `Note` | 中文一句话差异/理由；`Status != Semantic` 与 `Eratw` 键**必填**，不复制参数表 |

no-op / 仅状态示例：

```csharp
Status = PortStatus.AcceptedNoOp,
Note = "参数解析 + ColorMatrix 5×5 校验；控制台 no-op",
```

### 3.2 写处理体

在 `Instraction.Child.cs` 新建 `private sealed class Xxx_Instruction : AInstruction`，或给既有类加参数复用。

必须做对的事：

1. **参数构造**：`ArgBuilder = ArgumentParser.GetArgumentBuilder(FunctionArgType.XXX)`。
2. **flag**：按语义设置（见 §3.4）。
3. **取值**：`DoInstruction` 里从 `func.Argument` 取表达式并 `GetIntValue/GetStrValue(exm)`。
4. **跨模块行为**：不要直接操作宿主；走 `exm.Console` 的语义接缝（如 `WaitInputNoFocus`、`SetImageLayer`、`PrintHtmlC`）。
5. **异常**：参数/语义错误抛 `CodeEE`，不要抛宿主异常。

复用示例：

- `TINPUTNF` 族复用 `TINPUT_Instruction` / `TINPUTS_Instruction`，只多一个 `noFocus` 开关。
- `CALLSTR` 族复用 `CALLS_Instruction`，用 `isJump` / `isTry` / `isTryCatch` 三个布尔区分五种指令。

### 3.3 新增参数形状（仅在必要时）

如果既有 `FunctionArgType` 无法表达参数表，按三件套增加：

1. `FunctionArgType.cs`：新增枚举值；
2. `ArgumentBuilder.cs`：新增 `Xxx_ArgumentBuilder`，声明 `argumentTypeArray` 与 `minArg`，并在 `ArgumentParser` 静态构造器注册；
3. `Argument.cs`：新增 `XxxArgument`，持有解析后的表达式。

示例：`SP_SETIMAGELAYER` / `SP_SETIMAGELAYERL` / `SP_HTML_PRINTC` / `SP_COLOR_ALPHA`。

注意：

- 数组/矩阵参数在解析期无法确定具体类型时可用 `typeof(void)` 占位，执行期再校验（`ColorMatrixReader` 的 5×5 校验）。
- 参数个数错误、类型错误要给出与上游一致的告警/异常语义。

### 3.4 flag 速查

| flag | 含义 | 常见用途 |
|---|---|---|
| `EXTENDED` | Emuera/方言扩展指令 | 所有移植指令通常都带 |
| `IS_PRINT` | 会被 `SkipPrint` 跳过 | `HTML_PRINTC` 等输出指令 |
| `IS_INPUT` | 输入类指令（`userDefinedSkip` 告警） | `TINPUTNF` 族 |
| `FLOW_CONTROL` | 改变控制流 | `CALLSTR` / `JUMPSTR` 族 |
| `IS_JUMP` | 跳转指令 | `JUMPSTR`、`TRYCJUMPSTR` |
| `IS_TRY` / `IS_TRYC` | TRY / TRY-CATCH 配对 | `TRYCALLSTR` 族 |
| `PARTIAL` | 多行语法的一部分，不能紧跟 `SIF` | `TRYCJUMPSTR` / `TRYCCALLSTR` |
| `FORCE_SETARG` | 加载期必须解析实参 | 动态调用、跳转 |
| `METHOD_SAFE` | 可在 `#FUNCTION` 中调用 | 无输入/无调用的指令 |

> flag 是**可观测契约**：测试会用 `IsPrint()` / `IsInput()` / `IsJump()` / `IsTry()` / `IsTryC()` / `IsPartial()` 锁定，不能凭感觉加。

---

## 4. 移植一个式中函数

### 4.1 写 descriptor

在 `Creator.Registry.cs` 的 `BuiltInMethodDescriptors` 中追加：

```csharp
new() { Name = "SIN", CreateMethod = () => new SinMethod(), Dialect = Dialect.Snake, Family = "数学" },
```

函数与指令共用同一套元数据字段（`Family` / `Status` / `Note`），规则见 §3.1 与 §7。

### 4.2 写 `FunctionMethod`

在对应的 `Creator.Method.<Family>.cs` 中实现：

```csharp
private sealed class SinMethod : FunctionMethod
{
    public SinMethod()
    {
        ReturnType = typeof(double);          // 或 typeof(long) / typeof(string)
        argumentTypeArrayEx = [new ArgTypeList { ArgTypes = { ArgType.Float } }];
        CanRestructure = true;                // 纯函数可为 true；有副作用必须 false
    }

    public override double GetFloatValue(ExpressionMediator exm, List<AExpression> arguments)
        => Math.Sin(arguments[0].GetFloatValue(exm));
}
```

要点：

- 参数规则与返回类型**只写在 `FunctionMethod` 里**，descriptor 不重复声明，避免两处漂移。
- `CanRestructure = false`：有状态、有 IO、有副作用（SQL、reader、console 状态）。
- 变参用 `argumentTypeArrayEx` 的多组 `ArgTypeList` + `OmitStart`；参考 `SQL_CONNECT` 与 `SQL_P_*`。
- 函数名会被 `SetMethodName` 回填，异常消息里用 `Name` 而不是硬编码字符串。

### 4.3 命令形自动成立

`FunctionIdentifier` 会把 `methodList` 每项以 METHOD 指令并入 `funcDic`。因此登记为式中函数后：

```erb
SQL_CONNECT "db", "Data Source=plugins/qol_data.db"
```

这种无括号语句行也能解析。**不要**再额外登记一条同名指令；重复登记会撞 `funcDic.Add`。

---

## 5. 注册与投影：不需要改的地方

以下都是自动的，除非你要改机制本身：

- `AddInstructionDescriptors(funcDic, BuiltInInstructionDescriptors)` 在 `FunctionIdentifier` 静态构造器调用；
- `AddMethodDescriptors(methodList, BuiltInMethodDescriptors)` 在 `FunctionMethodCreator` 静态构造器、`SetMethodName` 之前调用；
- `DialectRegistry` 从 descriptor 的 `Dialect` 标签收集 `snake` / `eratw` 声明键；
- `Baseline` = 注册表全键 − 方言声明键（隐式基线，新增方言键不会泄漏进 `v24pure`）；
- `IdentifierDictionary` 构造期按 profile 过滤，并据此生成 `nameDic`。

---

## 6. 测试与门禁

每个家族至少四类测试，放在 `EraCore.Tests/P0<Family>Tests.cs`：

| 层 | 断言 |
|---|---|
| 解析 fixture | 指令/函数能被识别，`InstructionLine.IsError == false`；`TRY/CATCH` 等配对正确 |
| 执行 fixture | `DoInstruction` 产出预期 `InputRequest` / 状态变化 / 返回值 |
| 反射 flag | `IsPrint` / `IsInput` / `IsJump` / `IsTry` / `IsTryC` / `IsPartial` 与上游一致 |
| profile 可见性 | `v24pure` 不可见、`snake`/`eratw` 可见；既有基线键不受影响 |

运行：

```bash
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~P0
```

### 6.1 面快照更新

新增/删除方言键或修改 descriptor 元数据后，快照/键索引门禁会红。确认差异符合预期后，用**同一开关**一次重写两个产物：

```powershell
$env:ERACORE_DIALECT_SURFACE_UPDATE=1
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter "FullyQualifiedName~DialectSurfaceSnapshotTests|FullyQualifiedName~DialectKeyIndexTests"
Remove-Item Env:ERACORE_DIALECT_SURFACE_UPDATE
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter "FullyQualifiedName~DialectSurfaceSnapshotTests|FullyQualifiedName~DialectKeyIndexTests"
```

产物：面快照 `tools/dialect-inventory/surface.v24pure.txt` / `surface.snake.txt` / `surface.eratw.txt`，键索引 `docs/reference/dialect-keys.md`。

口径：面快照每行 `name<TAB>kind`；键索引方言行 = 键 / kind / 模块 / 家族 / 状态 / 说明，baseline 行 = 键 / kind / baseline / 处理体。两者均按序号排序、UTF-8 / LF，比对时归一化 BOM 与 CRLF，但**不要**手工编辑。

### 6.2 上游 delta（opt-in）

仓库不包含上游 fork 源码，因此该门禁默认不跑：

```bash
export EMUERA_SKIA_REF=/path/to/skia-checkout
python tools/dialect-inventory/extract.py --fork skia --check
python tools/dialect-inventory/extract.py --fork skia --print-delta
python tools/dialect-inventory/extract.py --fork skia --status   # repo-local，无需 sourceRoot
```

- `--check`：冻结产物 / `dialects/skia.json` 的 `additions` 与上游漂移 → 非零退出；
- `--update`：确认漂移后重写清单 / hash / additions（保留既有 `status` / `note`）；
- `--status`：只读 fork JSON + 已提交面快照，输出 `X/Y ported` 与 pending/excluded/out-of-scope 分组；`ported ∩ (pending|excluded)` 非零退出；
- 缺 `sourceRoot` → fail-fast，禁止静默跳过（`--status` 除外）。

### 6.3 真实游戏门禁

```bash
python tests/test_conformance_kit.py --game-dir <游戏目录> --build
```

它做三件事：解析 smoke（零无法解析标识符/行、零未定义函数、无未预期 `warnLevel≥2`）、面快照、playtest（进入输入循环）。

新增游戏只改 `tests/games.json`：

```json
{
  "id": "eratw-sub-modding-develop",
  "profile": "eratw",
  "matchHints": ["eratw"],
  "anchors": [{ "path": "ERB/TITLE.ERB", "contains": "@SYSTEM_TITLE" }],
  "knownLv2": ["TEMP_CHARA_LIST_BY_DT.ERB:第931行"],
  "expectState": "WaitInput"
}
```

### 6.4 键分档器（classify.py）

移植前先判断“这个键能不能机械搬”，避免把时间花在注定要人工重写的键上。

位置：`tools/dialect-inventory/classify.py`（纯标准库，无 LLM 依赖）。前置与 `extract.py` 相同——需要 `EMUERA_SKIA_REF` 指向上游 checkout：脚本要读**实现体**，不是只读键名。era-core 侧无需额外输入：脚本自行扫描 `EraCore.Core` 建立符号索引。

```bash
export EMUERA_SKIA_REF=/path/to/skia-checkout
python tools/dialect-inventory/classify.py --fork skia          # 全量分档 + 证据
python tools/dialect-inventory/classify.py --fork skia --quiet  # 只看计数
python tools/dialect-inventory/classify.py --fork skia --key ARGLEN
python tools/dialect-inventory/classify.py --fork skia --json
python tools/dialect-inventory/classify.py --fork skia --verify # 内置断言自检
```

输出三档：

| 档 | 含义 | 处理 |
|---|---|---|
| **A** | 结构同构，可机械搬 + 编译修复 | 按 §3 / §4 写 descriptor 或方法类 |
| **B** | 结构可搬，但语义/执行需人工映射 | 骨架可复制，执行体必须对照上游逐行核对 |
| **C** | 宿主能力 / 语言运行时缺失 | 不要动手；先补能力或标 `out-of-scope` |

判定信号优先级（命中即停，顺序不可换）：

1. **Float 专用签名**（`ReturnType = EraType.Float` / `ArgType.Float`）→ C；仅内部有 Float 分支、返回仍是 Integer/String → B（可裁剪）；
2. **语言运行时上下文**（`ExecutionContext` / `CurrentVariadicArgCount` / `exm.Process.State`）→ C；
3. **渲染/字体后端**（`Config.TextDrawingMode`、`bitmapCache`、`setRedrawTimer`、`GetLinePointY`、SkiaSharp 等）→ C；
4. **音频后端**（`GlobalStatic.Bgm` / `Sound`）→ C；
5. **输入宏 / 序列输入**（`inputMacroEnabled` / `sequenceInputValue`）→ C；
6. **图像层管线**（`ImageLayer`）→ 指令 B（参数骨架可搬、执行降级）/ 函数 C；
7. **仅状态**（`TextBackgroundColor` 等有同名字段可承接）→ B；
8. **变量反射 / 自定义参数校验**（`GetVariableToken` / `VariableTerm` / `CheckArgumentType`）→ B；
9. 其余 → A。

**KNOWN_OVERRIDES**：脚本内的人工边界表。静态扫描看不出的语义映射（如 `TINPUTNF` 族的 noFocus 在 era-core 无焦点概念）、以及跨仓 API 改名但语义等价（如 `EXIST_IN_CSV` ↔ era-core `ConstantData.isDefined`）都在此落脚。新增条目必须写明理由并定期复核——它是“已知盲区清单”，不是补丁抽屉。

### era-core 符号探测（C → B 的唯一降级路径）

分类器不只读上游：它同时扫描 `EraCore.Core` 的类/方法/属性/字段（记录可见性与文件行号），把上游实现体里的成员符号与 era-core 声明做匹配。

- 命中且可信 → 由 C 降为 **B**；**不降到 A**——等价性必须人工确认；
- 证据随结果输出：上游信号符号、era-core 候选（`Owner.Name @ 文件:行`）、可见性、置信度。

| 置信度 | 触发条件 | 含义 |
|---|---|---|
| `exact` | 同名且可见 | 很可能是同一能力，仍需核对参数与副作用 |
| `probable` | 仅去 `Get`/`Set`/`Is`/`Has`/`Try` 前缀或 `Method`/`Instruction`/`Manager`/`Info` 后缀，或仅大小写差异 | 需人工确认是否同一概念 |
| `weak` | 命中人工同义词表 `SYNONYM_HINTS` | 已知跨仓改名，必须人工验证语义等价 |

误配防护（防止 SkiaSharp / 音频被错误降级）：

- **硬墙不参与探测**：Float、`ExecutionContext`/`CurrentVariadicArgCount`、渲染绘制原语与配置字段、音频后端；
- 通用词黑名单（`Console` / `Argument` / `GetIntValue` / `NAME` 等）绝不作为证据；
- 候选必须可见、**实现非空**、且不属于纯语法容器类（`LogicalLine` / `InstructionLine` / `GlobalStatic` 等）；
- 近似匹配只允许白名单词形变换，不做任意模糊；
- 同义词表只收人工确认条目，命中恒为 `weak`。

**冗余检测**：普通运行与 `--verify` 都会检查 `KNOWN_OVERRIDES`——若某条 override 已被探测在**当前流程中真正取代**（候选在 `KNOWN_PROBE_SYMBOLS` 内，且该键不是靠 override 兜底），输出「可核对清理」。反向不成立：走 A 分支的键（如 CSV 反查）虽能探测到候选，但删除 override 会退化，**不会**被提示清理。

当前保留 10 条 override，保留理由写在代码注释：NF 族 4 条（实现体无可探测符号、era-core 无焦点概念）、CSV/ERD 反查 6 条（上游无 C 类信号，探测不参与 A 分支）。`GETANIMETIMER` 的旧 override 已删除——它由探测稳定给出 B（`weak`：上游属性 `AnimeTimer` ↔ era-core `getRedrawTimer`）。

**自检**：`classify.py --fork skia --verify`

内置 9 条断言：六条硬墙保持 C（`G_POLYGON_DRAW` / `SET_SKIA_QUALITY` / `BGMCONTROL` / `EVALF` / `SQL_EXECUTE_SCALAR_FLOAT` / `ARGLEN`），三条已有等价能力降为 B（`GETANIMETIMER` / `EXIST_IN_CSV` / `GETCSVNOBYNAME`）。失败打印差异并返回非零退出码。

### `--scaffold`：为 A 类键生成骨架

```bash
# 默认 dry-run：只打印目标路径与产物预览
python tools/dialect-inventory/classify.py --fork skia --scaffold --key NEW_KEY

# 写到临时目录（评审 + 编译验证，不污染仓库）
python tools/dialect-inventory/classify.py --fork skia --scaffold --key NEW_KEY --out /tmp/scaffold

# 写入仓库
python tools/dialect-inventory/classify.py --fork skia --scaffold --key NEW_KEY --apply

# 家族无法自动判定时人工指定
python tools/dialect-inventory/classify.py --fork skia --scaffold --key NEW_KEY --family 数学
```

**只对 A 类生效**，其余一律拒绝并说明：

| 拒绝条件 | 输出 |
|---|---|
| 非 A 类 | `判定 B/C，scaffold 仅支持 A 类（<理由>）` |
| era-core 已登记 | `已在 era-core 登记（<注册文件>）——拒绝重复生成` |
| 上游无实现类 | `上游注册表未登记，无法定位实现类` |
| 指令处理体不在 era-core | `上游处理体 <Class> 在 era-core 不存在；scaffold 不复制解析代码` |
| 家族无法判定 | `无法判定家族，请用 --family 指定` |
| 目标文件已有同键条目 | `幂等跳过` |

产物：

- **函数**：在 `Creator.Method.<Family>.cs` 追加 `private sealed class <Name>Method`，含 `ReturnType`、`argumentTypeArrayEx`（`ArgType.Any` 占位）、以及**抛带键名异常**的未实现主体（不静默返回 0/""）；
- **指令**：在 `FunctionIdentifier.Registry.cs` 追加 descriptor，`CreateInstruction` 复用既有处理体并**沿用上游注册行的构造参数**，不复制解析代码；
- **测试**：新建 `P0Scaffold<Name>Tests.cs`，两条 `Assert.True(false, "TODO...")`——**必然失败**，直到人工补齐。

脚本不写 `surface.*.txt` / `dialects/*.json` / `docs/reference/*.md`；结束时打印人工后续步骤：

1. 补全方法体/参数规则，替换测试 TODO 断言；
2. `dotnet build EraCore.Cli/EraCore.Cli.csproj -c Debug -m:1`；
3. `ERACORE_DIALECT_SURFACE_UPDATE=1 dotnet test --filter "DialectSurfaceSnapshotTests|DialectKeyIndexTests"`；
4. `extract.py --fork <id> --status`。

> 骨架只保证**编译通过**，不保证语义；参数规则与副作用必须逐行对照上游补齐后才能标 `Semantic`。

与 `extract.py` 的配合：

1. `extract.py --fork <id> --print-delta` 给出 fork-only 键清单；
2. `classify.py --fork <id>` 对同一清单分档；
3. 按 A → B → C 排期；C 类直接写进 `dialects/<id>.json` 的 `additions[].status`（`out-of-scope` + note）；
4. 每落地一个键，跑 §6.1 的面快照重写与 §6.2 的 `--status`，确认 `ported` 推导正确且无矛盾。

**分类结果不等于实现结论**：分类器读上游实现体并探测 era-core 符号，但仍不判断参数边界与副作用。A 类仍要写行为 fixture（解析能过 ≠ 行为正确）；B 类必须逐行对照上游；探测给出的 B（尤其 `probable` / `weak`）只是“era-core 可能有等价能力”的线索，必须人工确认后才能落地。C 类若经人工确认“其实可做”，应挪进 A/B 并在 `SYNONYM_HINTS` 或 `KNOWN_OVERRIDES` 记录理由。反例：`GETANIMETIMER` 命中 `AnimeTimer` 会被静态扫描判 C，探测凭 `getRedrawTimer` 降为 B（`weak`）——跨仓改名只能靠同义词表与人工核对。

---

## 7. 语义状态必须显式登记

移植不等于“看起来能跑”。状态分两层（ADR-0053），互不越界：

| 层 | 字段 | 值域 | 落点 / 门禁 |
|---|---|---|---|
| 已登记键（descriptor） | `Status` | `Semantic`（默认）/ `AcceptedNoOp`（参数完整解析校验，执行体空）/ `StateOnly`（保存状态，消费线未接） | 两个 descriptor；生成物 [`dialect-keys.md`](../reference/dialect-keys.md)；反漂移 `DialectDescriptorMetadataTests` |
| fork 上游键（`dialects/<forkId>.json` 的 `additions[]`） | `status` | `pending`（缺省，待移植）/ `excluded`（明确不移植）/ `out-of-scope`（范围外，如渲染/性能层） | fork JSON；`extract.py --status` 进度报告；矛盾门禁 `DialectForkConsistencyTests` |

- **`ported` 不是可登记值**：fork 键是否已移植由 `additions ∩ surface.*.txt` 推导，任何文件不得显式书写。
- 已 ported 的键**不写** `status`；未移植键缺省 `pending`。已 ported 却显式标 `pending`/`excluded` 是矛盾，`dotnet test` 红、`--status` 退出 1。
- `Status != Semantic` 的键必须在 [`dialect-surface.md`](../reference/dialect-surface.md) §5 **逐键点名**，并写 `Note`；`Eratw` 键也必须写 `Note`。
- 进度查询：`python tools/dialect-inventory/extract.py --fork <id> --status`（repo-local，不需要上游 checkout）。

禁止把 no-op 写成“已完成”。没有登记的状态就是隐藏的兼容性债务。

---

## 8. 接入新 fork / 新游戏

### 8.1 新 fork

1. 新增 `dialects/<forkId>.json`：

```json
{
  "id": "skia",
  "displayName": "Emuera Skia",
  "sourceRoot": "$EMUERA_SKIA_REF",
  "base": "era-core-baseline",
  "registryFiles": {
    "instructions": "Emuera/Runtime/Script/Statements/BuiltInFunctionCode.cs",
    "methods": "Emuera/Runtime/Script/Statements/Function/Creator.cs",
    "argBuilders": null
  },
  "additions": [],
  "ownerNotes": "..."
}
```

2. 跑 `extract.py --fork <id> --update`，把 fork-only 键写进 `additions`。
3. 在 `DialectRegistry` 增加模块与 profile 闭包（注意保持“由窄到宽”的单调链）。
4. 生成 `surface.<profile>.txt` 快照。
5. 在 `tests/games.json` 增加至少一个 fixture。

### 8.2 跨 fork 同键冲突

若两个 fork 声明同一个键，`DialectProfile` 构造期会要求显式裁决：

- `override`：一个模块显式覆盖其余全部 owner；
- `priority`：唯一最高者胜出；
- 二者皆无 → 构造期 `CodeEE`，**禁止静默合并**。

不要用“后加载覆盖先加载”这类隐式规则。

### 8.3 不兼容签名变化

当前投影只支持“整键可见/隐藏”，不支持同键多签名。若上游修改了既有参数的语义或返回值：

1. 先评估能否做成向后兼容扩展（新增可选尾参、旧调用逐位不变）→ 可以则并入 `Baseline`；
2. 不能兼容 → 先扩展投影为“同 name 多 variant 按模块择一”，再移植；
3. 不要为了单个键引入第二套解析器。

---

## 9. 常见错误

| 症状 | 原因 | 处理 |
|---|---|---|
| 新增键在 `v24pure` 也可见 | descriptor 忘了 `Dialect = Dialect.Snake` | 补标签并更新快照 |
| 解析期报“无法解析的标识符” | profile 未绑定，或键没进当前 profile 闭包 | 检查 `--profile` 与 `DialectRegistry` 闭包 |
| `funcDic.Add` 重复键 | 同名既登记为指令又登记为函数 | 函数登记一次即可，命令形自动成立 |
| 新增指令后 `FunctionCode` 分派异常 | 给新指令填了不存在的 `Code` | 保持 `__NULL__` |
| 快照门禁红但快照没变 | CRLF/BOM 或排序口径不对 | 用 `ERACORE_DIALECT_SURFACE_UPDATE=1` 重写，不要手改 |
| 移植完成后 `dotnet test` 红 / `--status` 退出 1 | 已进面快照的键仍显式标 `pending`/`excluded` | 删除该键的 `status` 字段；ported 由面快照推导 |
| fork JSON 写了 `status: ported` | 把推导值当登记值 | 删除 `status`（或改 pending/excluded/out-of-scope） |
| `dialect-keys.md` 门禁红 | 改了 descriptor 元数据但未重生成 | 用 `ERACORE_DIALECT_SURFACE_UPDATE=1` 重写并评审 diff |
| `Status != Semantic` 但反漂移测试红 | 已知差异区未点名或 `Note` 缺失 | 在 `dialect-surface.md` §5 逐键点名并补 `Note` |
| 测试里 surface 构建抛异常 | 在 `OpenScope` 之前 `DialectRegistry.Resolve` | 先开 scope，再解析 |
| 分类器判 C 但人工认为可做 | 命中硬墙信号，或探测未覆盖跨仓改名 | 先看探测证据；确认等价后加 `SYNONYM_HINTS` 或 `KNOWN_OVERRIDES`（见 §6.4） |
| SQLite `DllNotFoundException` | 发布目录缺原生库 | 见 [`running-eratw.md`](running-eratw.md) |

更多踩坑记录见 [`../LESSONS/dialect-and-sqlite.md`](../LESSONS/dialect-and-sqlite.md)。

---

## 10. 提交前 checklist

- [ ] 已跑 `classify.py --fork <id>` 判定 A/B/C，C 类未混入实现
- [ ] descriptor 只加在 `FunctionIdentifier.Registry.cs` / `Creator.Registry.cs` 之一
- [ ] 新指令保持 `FunctionCode.__NULL__`
- [ ] descriptor 带 `Family`；非真语义键写 `Status` + `Note`
- [ ] 非 `Semantic` 键与已知差异已写入 [`dialect-surface.md`](../reference/dialect-surface.md) §5（逐键点名）
- [ ] flag 与上游一致，并有反射测试锁定
- [ ] 新参数形状已注册到 `ArgumentParser` 静态构造器
- [ ] `v24pure` 面未变化；`snake`/`eratw` 面按预期增加
- [ ] 面快照与 [`dialect-keys.md`](../reference/dialect-keys.md) 已用同一开关显式重写并提交
- [ ] fork JSON 中已 ported 的键没有残留 `status: pending/excluded`；`extract.py --fork <id> --status` 退出 0
- [ ] P0 家族测试通过
- [ ] 一致性套件对目标游戏通过
