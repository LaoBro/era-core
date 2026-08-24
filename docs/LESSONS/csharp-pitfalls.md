# C# 语言陷阱 失败教训

> **TL;DR**：C# 静态字段按声明顺序初始化（有依赖必须按依赖序声明）；诊断代码插入单行 `if` 必须加大括号，否则改变控制流；同一可变状态不要留多份引用——重构收归状态时，便捷字段必须改成属性映射，否则 `Clone()`/替换后各引用分叉。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 静态字段按声明顺序初始化 | 被依赖的字段必须先声明，或用静态构造器 | — |
| 2 | 诊断日志插入 if 无大括号改控制流 | 单行 `if` 体插日志必须写成 `if (...) { log; break; }` | — |
| 3 | 重构后同一可变状态的分叉 | 状态收归某容器后，旧便捷字段必须改成属性映射，否则 `Clone()`/替换后各引用读到不同实例 | 2026-08-25 |

---

## 1. C# 静态字段按声明顺序初始化

**场景**：`s_logEnabled` 在 `s_logPath` 之后声明，`s_logPath` 的初始化调用 `ResolveLogPath()` 读取 `s_logEnabled`。

**结果**：`s_logEnabled` 还是默认值 `false`，`ResolveLogPath()` 直接返回 `null`，日志功能不生效。

**原因**：C# 静态字段按声明顺序初始化。`s_logPath` 先初始化，此时 `s_logEnabled` 尚未被赋值，仍是 `false`。

**解决**：交换声明顺序，`s_logEnabled` 在 `s_logPath` 之前。

**教训**：有依赖关系的静态字段必须按依赖顺序声明。被依赖的字段必须先声明。或者改用静态构造函数/方法避免初始化顺序问题。

## 2. C# 诊断日志插入 if 无大括号会改控制流

**场景**：`ErhLoader.LoadHeaderFiles` 原代码：
```csharp
if (!noError)
    break;
```
诊断时在 `if` 与 `break` 之间插入两行日志且**未加大括号**：
```csharp
if (!noError)
    Console.WriteLine(...);  // 仅此行受 if 控制
    Console.Out.Flush();     // 总是执行
    break;                   // 总是执行
```

**结果**：142 个 ERH **只加载第 1 个**就退出；`noError` 仍可为 True（那一个文件成功）。日志显示 `files=142` 但实际只处理了 KOJO 类头文件 → `#DIM DVAR` / `#DEFINE カラー_*` 未注册 → ERB 阶段海量「解释できない識別子」。修好括号后：`ERH loaded=142/142, dimlines=1692`。

**教训**：
- C# 只认大括号，不认缩进；往单行 `if` 体插日志必须写成 `if (...) { log; break; }`
- 「列表长度正确」≠「循环真的跑完」——验收要打 **实际 loaded 计数**（如 `loaded=142/142`）
- 诊断代码与生产控制流绑在一起时，宁可用大括号块，也不要依赖「暂时只加一行」

## 3. 重构后同一可变状态的分叉——Process.state 字段未随 executionState.CurrentState 同步

**场景**：ADR-0011 把引擎执行状态收归 `ExecutionState`（`CurrentState` + 状态栈），但 `Process` 仍保留独立字段 `ProcessState state` 作为便捷引用。`saveCurrentState()` 只更新 `executionState.CurrentState`（`state = state.Clone()` 的新实例，`Clone()` 不复制 functionList），`Process.state` 字段保持旧引用不动。

**结果**：`SAVEGAME`/`LOADGAME` 后 `DoScript`（读 `Process.state`，functionList 仍在 → `ScriptEnd=false`）与 `ScriptProc`/`SystemProc`（读 `executionState.CurrentState`，functionList 空 → `ScriptEnd=true`）读到**两个不同**的 `ProcessState`：
- `ScriptProc.Run()` 每执行一行就因 `ScriptEnd=true` 返回；
- `DoScript` 因 `ScriptEnd=false` 不调 SystemProc，继续调 `ScriptProc.Run()`；
- 两者打架 → `@SYSTEM_TITLE` 的 `DO…LOOP` 死循环，`LOADGAME` 后回不到标题/进不了存档界面。TW4 表现为：第一次点「存檔再開」无反应（标题重绘），第二次点因 `SystemState` 已变 `LoadGame_Begin`（无 `__CAN_SAVE__`）抛「不能使用命令」。上游 emuera.em 正常，因为它 `saveCurrentState` 直接 `state = state.Clone()`（同一字段）。

**解决**：把 `Process.state` 改成属性映射 `ProcessState state => (ProcessState)executionState.CurrentState;`，`Initialize`/`BeginTitle` 的赋值同步到 `executionState.CurrentState`——单一事实来源，不再有两份引用。另补回归测试 `tests/test_title_loadgame.py`（标题 → LOADGAME → 存档界面 → [100] 返回 → 脚本继续）。

**教训**：
1. **单一事实来源**：重构把可变状态收归容器（如 `ExecutionState`）后，旧的便捷字段/属性必须改成对容器的**属性映射**，禁止保留独立字段——否则 `Clone()`、`LoadPrevState`、会话替换等一切"替换当前实例"的操作都会让各引用分叉。评审时检查"源状态变更点是否同步了所有别名"。
2. **这类 bug 的正常游玩路径测不到**：`saveCurrentState → Clone → ScriptEnd → SystemProc 接管` 只在 `SAVEGAME`/`LOADGAME`/`RELOADERB` 等"保存/恢复状态栈"流程触发；test_game 的 `@SYSTEM_TITLE` 不含这些指令，全量回归 0 覆盖，bug 潜伏至今。任何涉足状态栈/标题流程的改动，必须补针对性 e2e（见 `tests/test_title_loadgame.py` 模式：临时夹具 + 驱动「进入存档界面 → 返回」）。
3. 与上游（emuera.em）diff 等价性是这类移植重构的照妖镜：行为不一致时，先逐行对比上游对应方法的 `state` 来源。
