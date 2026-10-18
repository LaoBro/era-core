# 方言指令移植指南

本文回答“**如何把其他 Emuera 分支的指令/函数移植进 era-core**”，以及“**第 N 次移植**怎样只加一份描述符和一条 fixture”。

适用读者：需要在 `EraCore.Core` 增加内置指令/式中函数、增加游戏适配键、或接入新 fork 的开发者。

相关文档：

- 运行需要方言的游戏：[`running-eratw.md`](running-eratw.md)
- 66 个方言键的权威清单与实现状态：[`../reference/dialect-surface.md`](../reference/dialect-surface.md)
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
| descriptor 类型 + 方言枚举 | `EraCore.Core/Shared/Runtime/Script/Statements/BuiltInRegistry.cs` |
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
| 渲染/性能层键（SkiaSharp 管线、lazyload、`SET_SKIA_QUALITY` 等） | 不移植 | 明确记为范围外，不登记 |

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
new() { Name = "SIN", CreateMethod = () => new SinMethod(), Dialect = Dialect.Snake },
```

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

新增/删除方言键后，快照门禁会红。确认差异符合预期后，显式重写：

```powershell
$env:ERACORE_DIALECT_SURFACE_UPDATE=1
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~DialectSurfaceSnapshotTests
Remove-Item Env:ERACORE_DIALECT_SURFACE_UPDATE
dotnet test EraCore.Tests/EraCore.Tests.csproj --filter FullyQualifiedName~DialectSurfaceSnapshotTests
```

快照文件：`tools/dialect-inventory/surface.v24pure.txt` / `surface.snake.txt` / `surface.eratw.txt`。

口径：每行 `name<TAB>kind`，按行序号排序，UTF-8 / LF，末尾换行。比对时会归一化 BOM 与 CRLF，但**不要**手工编辑快照。

### 6.2 上游 delta（opt-in）

仓库不包含上游 fork 源码，因此该门禁默认不跑：

```bash
export EMUERA_SKIA_REF=/path/to/skia-checkout
python tools/dialect-inventory/extract.py --fork skia --check
python tools/dialect-inventory/extract.py --fork skia --print-delta
```

- `--check`：冻结产物 / `dialects/skia.json` 的 `additions` 与上游漂移 → 非零退出；
- `--update`：确认漂移后重写清单 / hash / additions；
- 缺 `sourceRoot` → fail-fast，禁止静默跳过。

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

---

## 7. 语义状态必须显式登记

移植不等于“看起来能跑”。每个键必须落在以下四类之一，并写进 [`dialect-surface.md`](../reference/dialect-surface.md)：

| 状态 | 含义 | 例子 |
|---|---|---|
| 真语义 | 行为与上游一致 | `TINPUTNF`、`CALLSTR`、`MAP_*`、`SQL_*` |
| 语义接受（no-op） | 参数完整解析/校验，执行体暂为空 | `SETIMAGELAYER*`、`CLEARIMAGELAYER*` |
| 仅状态 | 保存状态，渲染线未消费 | `TEXT_BGC_ON/OFF` |
| 未实现 | 明确不移植，记录原因 | 3 个 `SQL_*_FLOAT`、Skia 渲染/性能键 |

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
  "exclusions": [],
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
| 测试里 surface 构建抛异常 | 在 `OpenScope` 之前 `DialectRegistry.Resolve` | 先开 scope，再解析 |
| SQLite `DllNotFoundException` | 发布目录缺原生库 | 见 [`running-eratw.md`](running-eratw.md) |

更多踩坑记录见 [`../LESSONS/dialect-and-sqlite.md`](../LESSONS/dialect-and-sqlite.md)。

---

## 10. 提交前 checklist

- [ ] descriptor 只加在 `FunctionIdentifier.Registry.cs` / `Creator.Registry.cs` 之一
- [ ] 新指令保持 `FunctionCode.__NULL__`
- [ ] flag 与上游一致，并有反射测试锁定
- [ ] 新参数形状已注册到 `ArgumentParser` 静态构造器
- [ ] `v24pure` 面未变化；`snake`/`eratw` 面按预期增加
- [ ] 面快照已显式重写并提交
- [ ] P0 家族测试通过
- [ ] 一致性套件对目标游戏通过
- [ ] 语义状态与已知差异已写入 [`dialect-surface.md`](../reference/dialect-surface.md)
