## IScriptHost 接口计划

---

### 问题本质

当前 ERB 脚本通过 `ExpressionMediator` 直接持有 [Process](../../EraCore.Core/Shared/Runtime/Script/Process.cs#L26-L67)、[VariableEvaluator](../../EraCore.Core/Shared/Runtime/Script/Statements/Variable/VariableEvaluator.cs#L21-L40)、[EmueraConsole](../../EraCore.Core/Shared/Runtime/Script/Statements/ExpressionMediator.cs#L14-L24) 三个对象——脚本想做什么就做什么，没有边界。C# 脚本进来后会面临同样的情况：直接拿到引擎内部对象，要么全部暴露要么全部不可用。

`IScriptHost` 的目标：**定义脚本"能做什么"的契约，把"怎么做"藏在实现后面**。

---

### Phase 0：审计脚本能力（2-3 天）

不审计就定义接口，要么太宽（等于没抽象），要么太窄（C# 脚本什么都干不了）。

#### 0-1 指令级审计

[Instraction.Child.cs](../../EraCore.Core/Shared/Runtime/Script/Statements/Instraction.Child.cs) 有 3824 行，是所有 ERB 指令的实现。每条指令的 `DoInstruction(exm, func, state)` 签名中，`exm` 是脚本访问引擎的唯一入口。逐条梳理它调用了 `exm` 的哪些成员：

| 分类 | 典型指令 | 访问的 exm 成员 |
|------|---------|----------------|
| 输出 | PRINT / PRINTS / PRINTL | `exm.Console.Print()` / `PrintSingleLine()` |
| 变量读写 | VAR / VARSET | `exm.VEvaluator` → `VariableData` |
| 流程控制 | GOTO / CALL / RETURN | `exm.Process` → `LabelDictionary` / `CalledFunction` |
| 输入等待 | INPUT / TINPUT | `exm.Console.ReadInput()` |
| 文件 IO | SAVEDATA / LOADDATA | `exm.VEvaluator` → 文件操作 |
| 随机数 | RAND | `exm.VEvaluator.Rand` |

#### 0-2 SystemProc 审计

[SystemProc](../../EraCore.Core/Shared/Runtime/Script/SystemProc.cs#L17-L49) 持有 `process` / `console` / `vEvaluator` / `gamebase` / `trainName` / `executionState`——这是系统流程（标题→训练→商店→升级→回合结束）的调度器。C# 脚本是否能控制系统流程？这是一个产品决策，需要先回答。

#### 0-3 产出物

一份**脚本能力矩阵**：

```
能力域          ERB 指令数    必须暴露？    风险等级
─────────────────────────────────────────────────
文本输出        ~20          ✅           低
变量读写        ~15          ✅           中（需作用域限制）
流程控制        ~10          ✅           中（栈深度限制）
输入等待        ~8           ✅           低
文件 IO         ~6           ⚠️ 沙箱化    高
系统流程        ~15          ❌ 仅 ERB    高
图形/HTML       ~5           ⚠️ 预留      中
配置读取        ~3           ✅ 只读       低
```

---

### Phase 1：定义 IScriptHost 接口（3-5 天）

基于 Phase 0 的能力矩阵，分域定义接口。**一个接口太大会沦为 Process 第二**，按能力域拆成小接口，`IScriptHost` 是它们的聚合。

#### 1-1 接口分层

```csharp
namespace MinorShift.Emuera.ScriptHost;

/// 脚本输出能力
public interface IScriptOutput
{
    void Print(string text, bool newLine = false);
    void PrintLine(string text);
    void PrintAlign(ContentAlignment align);
    void ClearLine(int count);
    void ClearScreen();
    void SetBackgroundColor(EmuColor color);
}

/// 脚本变量访问能力
public interface IScriptVariables
{
    long GetInt(VariableCode code, params long[] indices);
    void SetInt(VariableCode code, long value, params long[] indices);
    string GetStr(VariableCode code, params long[] indices);
    void SetStr(VariableCode code, string value, params long[] indices);
    long GetInt(string name, params long[] indices);
    string GetStr(string name, params long[] indices);
    void SetInt(string name, long value, params long[] indices);
    void SetStr(string name, string value, params long[] indices);
    int GetArraySize(VariableCode code, int dimension = 0);
}

/// 脚本流程控制能力
public interface IScriptFlow
{
    void CallFunction(string label, params (string name, object value)[] args);
    void GotoLabel(string label);
    void Return(object? result = null);
    void Throw(string? message = null);
    void SkipPrint(bool skip);
}

/// 脚本输入能力
public interface IScriptInput
{
    Task<long> WaitInputAsync(long[]? validValues = null, long? timeoutMs = null);
    Task<string> WaitInputStrAsync(string[]? validValues = null, long? timeoutMs = null);
    void ForceTimeout();
}

/// 脚本文件 IO（沙箱化）
public interface IScriptFileIO
{
    // 仅允许游戏存档目录下的操作
    Task<bool> SaveDataAsync(int index, string? comment = null);
    Task<bool> LoadDataAsync(int index);
    string[] GetSaveFileList();
    bool DeleteSaveData(int index);
}

/// 聚合接口——脚本持有的唯一引用
public interface IScriptHost : 
    IScriptOutput, 
    IScriptVariables, 
    IScriptFlow, 
    IScriptInput,
    IScriptFileIO
{
    /// 当前引擎状态（只读）
    ConsoleState State { get; }
    
    /// 当前输入请求（只读）
    InputRequest? CurrentRequest { get; }
    
    /// 配置访问（只读子集）
    IReadOnlyConfig Config { get; }
    
    /// 游戏基础数据（只读）
    IGameInfo GameInfo { get; }
}
```

#### 1-2 关键设计决策

| 决策 | 选项 | 推荐 | 理由 |
|------|------|------|------|
| 接口粒度 | 一个大接口 vs 分域小接口 | **分域小接口** | C# 脚本可能只需要 IScriptOutput + IScriptVariables，不该被强制实现流程控制 |
| 变量访问方式 | 按 VariableCode vs 按名称字符串 | **双模式** | 按 code 快速且类型安全，按名称灵活（C# 脚本动态构建变量名时需要） |
| IScriptFlow.CallFunction | 同步 vs 异步 | **同步** | ERB 的 CALL 是同步的（压栈→执行→返回），C# 脚本的调用模型应该一致 |
| 文件 IO 范围 | 全盘 vs 沙箱 | **沙箱** | C# 脚本不可信，只能访问存档目录 |
| 系统流程控制 | 暴露 vs 不暴露 | **不暴露** | Title→Train→Shop 流程是 ERB 专有概念，C# 脚本通过 IScriptInput 参与交互即可 |

#### 1-3 验证点

- 接口能覆盖 Phase 0 审计中标记"必须暴露"的所有能力
- 接口不暴露 Process / EmueraConsole / VariableData 等内部类型
- 接口方法签名中只使用 Primitives 命名空间下的类型（EmuColor 等）

---

### Phase 2：实现 ScriptHost（5-7 天）

基于 IScriptHost 接口，写一个持有引擎对象引用的实现类。

#### 2-1 核心实现

```csharp
internal sealed class EmueraScriptHost : IScriptHost
{
    private readonly EmueraConsole _console;
    private readonly Process _process;
    private readonly VariableEvaluator _veval;

    internal EmueraScriptHost(EmueraConsole console, Process process)
    {
        _console = console;
        _process = process;
        _veval = process.VEvaluator;
    }

    // --- IScriptOutput ---
    public void Print(string text, bool newLine = false)
    {
        _console.Print(text, newLine);
    }

    public void PrintLine(string text)
    {
        _console.Print(text, true);
        _console.NewLine();
    }

    // --- IScriptVariables ---
    public long GetInt(VariableCode code, params long[] indices)
        => _veval.GetVariableValue(code, indices);

    public void SetInt(VariableCode code, long value, params long[] indices)
        => _veval.SetVariableValue(code, value, indices);

    // --- IScriptFlow ---
    public void CallFunction(string label, params (string name, object value)[] args)
        => _process.CallFunction(label, args); // 薄包装，逻辑仍在 Process

    // --- IScriptInput ---
    public async Task<long> WaitInputAsync(long[]? validValues = null, long? timeoutMs = null)
    {
        // 委托给 EmueraConsole 的输入等待机制
        return await _console.ReadInputAsync(validValues, timeoutMs);
    }

    // ... 其余实现
}
```

**注意**：Phase 2 的实现是**薄转发层**——每个方法都是一行委托。这不是偷懒，而是正确做法：接口的价值在于**约束能力边界**，不在于重新实现逻辑。将来 C# 脚本拿到的是 `IScriptHost` 引用，它无法向上转型拿到 `EmueraScriptHost`，更无法拿到 `Process`。

#### 2-2 EmueraScriptHost 的构造时机

当前 [Session.GameLoopAsync()](../../EraCore.Core/Server/Session.cs#L71-L81) 中构造顺序：

```csharp
using var scope = GlobalStatic.OpenScope(_configData);
_ui = new HeadlessConsole();
_console = new EmueraConsole(_ui, _terminalSetup);
_protocol = new AgentJsonlProtocol(_console, _ui, _io);
_console.SetAgentBridge(_protocol);
```

`EmueraScriptHost` 应在 `console.Initialize()` 之后构造（Process 此时已创建），并通过 `IScriptHost` 注入到需要的位置。

#### 2-3 验证点

- 每个接口方法都有对应单元测试（mock 引擎对象，验证调用转发正确）
- `EmueraScriptHost` 不暴露任何 `internal` 引擎类型

---

### Phase 3：ERB 执行路径经 IScriptHost 走一遍验证（5-7 天）

这是最关键的验证步骤——证明接口真的能用，而不是纸上谈兵。

#### 3-1 选取 3-5 条代表性指令做端到端验证

| 指令 | 覆盖能力域 | 验证要点 |
|------|----------|---------|
| PRINTL | IScriptOutput | 文本输出 + 换行 |
| VARSET | IScriptVariables | 变量批量赋值 |
| CALL | IScriptFlow | 函数调用 + 返回值 |
| INPUT | IScriptInput | 等待用户输入 |
| SAVEDATA | IScriptFileIO | 存档写入 |

#### 3-2 双路径对比

不修改 ERB 的现有执行路径。而是在每条指令的 `DoInstruction` 中**同时调用旧路径和新路径**，对比结果：

```csharp
// Instraction.Child.cs 中某条指令，验证期双路径
public override void DoInstruction(ExpressionMediator exm, InstructionLine func, ProcessState state)
{
    // 旧路径（不变）
    exm.Console.Print("hello", true);
    
    // 新路径（验证期并行执行）
    var host = exm.ScriptHost; // 新增属性
    host.PrintLine("hello");
    
    // Debug.Assert 两条路径的副作用一致
}
```

#### 3-3 验证点

- 双路径对比测试全部通过（副作用一致）
- 无性能退化（新路径是薄转发，理论上零开销）

---

### Phase 4：C# 脚本运行时基础设施（7-10 天）

有了 IScriptHost，C# 脚本才有运行的基础。

#### 4-1 脚本加载模型

```csharp
public interface IScriptLoader
{
    /// 从文件加载 C# 脚本，编译并返回可执行实例
    Task<IScript> LoadAsync(string path);
    
    /// 从源码字符串编译
    Task<IScript> CompileAsync(string source, string scriptName);
}

public interface IScript
{
    string Name { get; }
    
    /// 脚本入口点。IScriptHost 是脚本访问引擎的唯一合法途径。
    Task ExecuteAsync(IScriptHost host, CancellationToken ct);
}
```

#### 4-2 编译策略

```csharp
internal sealed class RoslynScriptLoader : IScriptLoader
{
    public async Task<IScript> CompileAsync(string source, string scriptName)
    {
        // 使用 Roslyn Microsoft.CodeAnalysis.CSharp.Scripting
        // 引用 IScriptHost 所在程序集 + Primitives 程序集
        var options = ScriptOptions.Default
            .AddReferences(typeof(IScriptHost).Assembly)
            .AddReferences(typeof(EmuColor).Assembly)
            .AddImports("MinorShift.Emuera.ScriptHost")
            .AddImports("MinorShift.Emuera.Primitives");
        
        var script = CSharpScript.Create(source, options, globalsType: typeof(ScriptGlobals));
        // 编译检查
        var diagnostics = script.Compile();
        // ...
        return new CompiledScript(script, scriptName);
    }
}

/// C# 脚本的全局变量——脚本内直接用 host.PrintLine(...)
public sealed class ScriptGlobals
{
    public IScriptHost host = null!;
}
```

#### 4-3 脚本示例

```csharp
// 文件: scripts/example.csx
// C# 脚本可以使用 IScriptHost 的全部能力

host.PrintLine("Hello from C# script!");
host.ClearScreen();

var count = host.GetInt("COUNT");
host.SetInt("COUNT", count + 1);

var choice = await host.WaitInputAsync(validValues: [1, 2, 3]);
host.PrintLine($"You chose: {choice}");
```

#### 4-4 安全沙箱

| 措施 | 说明 |
|------|------|
| Roslyn `ScriptOptions` 限制引用 | 只暴露 IScriptHost + Primitives，不暴露 Process / EmueraConsole |
| `IScriptFileIO` 沙箱化 | 文件操作限定在存档目录 |
| 超时机制 | `ExecuteAsync` 接受 `CancellationToken`，无限循环可被强制终止 |
| 变量作用域 | `IScriptVariables` 按名称访问时，需检查变量是否对脚本可见（只暴露用户变量，隐藏系统变量） |

#### 4-5 验证点

- C# 脚本能通过 `host.PrintLine()` 输出文本
- C# 脚本能读写变量
- C# 脚本无法访问 `Process` / `EmueraConsole` 等内部类型
- 无限循环脚本可被 `CancellationToken` 终止

---

### Phase 5：ERB 路径渐进迁移（5-7 天）

Phase 3 证明了接口可用，Phase 5 把 ERB 的 `ExpressionMediator` 逐步替换为 `IScriptHost`。

#### 5-1 ExpressionMediator 添加 IScriptHost 属性

```csharp
internal sealed class ExpressionMediator
{
    public readonly VariableEvaluator VEvaluator;
    public readonly Process Process;
    public readonly EmueraConsole Console;
    
    // 新增：IScriptHost 视图（只暴露接口方法，隐藏引擎内部）
    internal IScriptHost ScriptHost => _scriptHost ??= new EmueraScriptHost(Console, Process);
    private IScriptHost? _scriptHost;
}
```

#### 5-2 逐指令迁移

按风险从低到高排序：

1. **输出指令**（PRINT 系列）→ `IScriptOutput` — 风险最低，副作用最直观
2. **变量指令**（VAR / VARSET）→ `IScriptVariables` — 需仔细验证索引计算
3. **输入指令**（INPUT / TINPUT）→ `IScriptInput` — 涉及异步，需注意死锁
4. **流程指令**（CALL / GOTO）→ `IScriptFlow` — 最复杂，最后迁移

每批迁移后运行完整回归测试。

#### 5-3 最终状态

迁移完成后，`ExpressionMediator` 退化为 `IScriptHost` 的薄包装。ERB 指令通过 `exm.ScriptHost.PrintLine()` 调用，C# 脚本通过 `host.PrintLine()` 调用——**同一个实现，两种语言**。

---

### 风险与回退

| 风险 | 影响 | 缓解 |
|------|------|------|
| 接口设计遗漏能力 | C# 脚本无法实现某些功能 | Phase 0 审计 + Phase 3 端到端验证；接口可版本化扩展 |
| 变量访问性能 | IScriptVariables 比直接访问 VariableData 多一层间接 | 薄转发，JIT 内联后理论上零开销；基准测试验证 |
| C# 脚本安全逃逸 | Roslyn 沙箱被绕过 | 限制引用集 + CAS 权限 + 超时终止 |
| ERB 迁移回归 | 逐指令迁移可能引入细微行为差异 | Phase 3 双路径对比 + 每批迁移后全量回归 |
| IScriptFlow.CallFunction 与 Process 内部调用语义不一致 | C# 脚本的函数调用走 IScriptHost，ERB 的走 Process.CalledFunction | Phase 5 最后处理流程指令，积累足够经验后再动 |

---

### 时间线

```
Phase 0  ███                              2-3 天   审计
Phase 1  ██████                           3-5 天   接口定义
Phase 2  █████████                        5-7 天   实现
Phase 3  █████████                        5-7 天   验证
Phase 4  ██████████████                   7-10 天  C# 运行时
Phase 5  █████████                        5-7 天   ERB 迁移
                                          ────────
                                          ~28-39 天
```

Phase 0-3 是基础——做完后 IScriptHost 接口就可用了，C# 脚本运行时（Phase 4）可以并行推进。Phase 5 是偿还技术债，风险最高，建议在 Phase 4 稳定后再执行。