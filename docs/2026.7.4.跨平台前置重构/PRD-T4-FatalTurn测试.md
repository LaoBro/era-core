# PRD: Fatal Turn 结构测试覆盖

> 对应 [PRD-T3](./PRD-T3-Turn协议版本化.md) 的 Out of Scope 项——fatal turn 路径
> 的测试覆盖。在 [PRD-T3](./PRD-T3-Turn协议版本化.md) 中作为后续工作记入
> [TODO.md](./TODO.md)，本 PRD 把它升格为独立交付。

## Problem Statement

作为 Emuera.Headless 的维护者，我发现 fatal turn 路径（脚本运行期抛异常时服务端
推送的 turn）没有任何回归测试覆盖。当未来重构 `AgentJsonlProtocol.StepAsync` 的
catch 块时，无法验证 fatal turn 的 wire format（`text=""`、`buttons=[]`、
`error=<异常消息>`、非 `WaitInput` 状态、无 `protocolVersion` 字段）是否被保留。

ADR-0001 明确把"fatal 路径独立
构造、不与 step 路径合并"作为有意安全策略——这是 surprising 决策，需要测试
守住它，否则未来 reader 可能在"统一构造路径"的简化冲动下把 fatal 路径合并进
`BuildTurn()`，导致 buffer 残留泄漏。

## Solution

新增独立测试 `tests/test_fatal_turn.py`，参照 [test_tinput_timeout.py](../../tests/test_tinput_timeout.py)
的"复制 `test_game` + 覆盖 ERB"模式：在 ERB 脚本里通过 `THROW` 指令在 input 推进
后抛异常，断言服务端返回的 fatal turn 符合 [PRD-T3](./PRD-T3-Turn协议版本化.md)
与 ADR-0001 定义的结构。

测试不引入新的测试 seam——继续复用 HTTP 接口（`POST /session` → `GET /turn` →
`POST /input` → `GET /turn`），与现有 [test_jsonl.py](../../tests/test_jsonl.py)
一致。

## User Stories

1. 作为 Emuera.Headless 维护者，我希望有一个 fatal turn 路径的回归测试，这样
   未来重构 `StepAsync` 的 catch 块时能验证 wire format 未漂移。
2. 作为 Emuera.Headless 维护者，我希望测试验证 fatal turn 的 `text` 字段为
   空字符串，这样我能确认"不泄漏半截 buffer"这一安全策略被保留。
3. 作为 Emuera.Headless 维护者，我希望测试验证 fatal turn 的 `buttons` 字段
   为空数组，这样我能确认"不泄漏可能失效的按钮"这一安全策略被保留。
4. 作为 Emuera.Headless 维护者，我希望测试验证 fatal turn 的 `error` 字段
   包含 ERB 脚本里 `THROW` 的字符串参数，这样我能确认异常消息被正确传递给
   客户端。
5. 作为 Emuera.Headless 维护者，我希望测试验证 fatal turn 的 `state` 字段
   不等于 `WaitInput`，这样我能确认客户端不会误判为"游戏仍在等待输入"并继续
   发 input。
6. 作为 Emuera.Headless 维护者，我希望测试验证 fatal turn 不携带
   `protocolVersion` 字段，这样我能确认版本字段仅出现在 initial turn 这一
   约束在 fatal 路径也成立。
7. 作为 Emuera.Headless 维护者，我希望测试通过 ERB `THROW` 指令触发 fatal，
   这样 `ex.Message` 可控、断言可写成子串匹配。
8. 作为 Emuera.Headless 维护者，我希望 fatal 在某个 input 推进后触发（而非
   `@SYSTEM_TITLE` 启动阶段），这样测试走的是 `StepAsync` 的 catch 路径——
   fatal turn 路径的主语义。
9. 作为测试开发者，我希望新增的 `test_fatal_turn.py` 与现有 [test_tinput_timeout.py](../../tests/test_tinput_timeout.py)
   风格一致，这样维护成本低、可读性高。
10. 作为测试开发者，我希望 `test_fatal_turn.py` 加入 [run_all.py](../../tests/run_all.py)
    回归套件，这样日常 `python tests/run_all.py` 会自动覆盖它。
11. 作为测试开发者，我希望 [tests/README.md](../../tests/README.md) 的测试
    清单更新，这样新测试的用途被记录。
12. 作为未来读者，我希望测试代码注释引用 ADR-0001，
    这样我能理解"为何 fatal 路径独立构造"的决策背景。

## Implementation Decisions

### 范围决策（grilling 阶段共识）

- **A. 注入方式**：ERB `THROW "fatal-test-marker"` 指令。
  - ERB 的 `THROW` 内置函数在 `Process.ScriptProc.cs#L741-L742`
    抛 `CodeEE`，`ex.Message` 就是 ERB 字面量参数——可控、可断言。
  - 排除"除零/未定义函数"等运行期崩溃——这些异常路径各异，单一测试不能代表其他。
  - 排除"语法错误"——解析期崩溃在 `Session.Start()` 阶段就失败，测不到
    `StepAsync` 的 catch 路径。
- **B. 触发位置**：在某个 input 推进后才触发 fatal。
  - 走 `StepAsync` 的 catch 路径（[AgentJsonlProtocol.cs#L52-L69](../../EraCore.Core/Agent/AgentJsonlProtocol.cs#L52-L69)）。
  - 排除"在 `@SYSTEM_TITLE` 里直接 `THROW`"——那条路径走 `GetInitialTurnAsync`，
    异常会冒到 `Session.GameLoop` 的 finally，是另一条路径，不属于 fatal turn
    测试的主语义。
- **C. 文件组织**：新增独立 `tests/test_fatal_turn.py`。
  - 参照 [test_tinput_timeout.py](../../tests/test_tinput_timeout.py) 的
    `copy_test_game_with_erb()` 模式：复制 `test_game` → 覆盖 `erb/TEST.ERB`
    → 启动 server → 推进 → 断言。
  - 排除"在 `TEST.ERB` 加 fatal 菜单项"——会污染 [test_jsonl.py](../../tests/test_jsonl.py)
    的精确按钮计数断言（"Turn 1 有 2 个按钮"等）。
- **D. 断言精度**：严格断言（D1）。
  - `text == ""`
  - `buttons == []`
  - `error` 字段包含 "fatal-test-marker" 子串
  - `state != "WaitInput"`
  - `protocolVersion` 字段不存在

### ERB 脚本设计

测试 ERB 在 [test_tinput_timeout.py](../../tests/test_tinput_timeout.py) 的
`@SYSTEM_TITLE` 块基础上改造，结构示意：

```erb
@SYSTEM_TITLE
PRINTL Fatal Turn Test
PRINTL [0] Trigger fatal
PRINTL [1] Quit
INPUT
IF RESULT == 0
    THROW "fatal-test-marker"
ELSEIF RESULT == 1
    QUIT
ENDIF
```

- 初始 turn 显示菜单（`state=WaitInput`、`inputType=IntValue`），与
  [test_jsonl.py](../../tests/test_jsonl.py) 的初始 turn 结构一致。
- `POST /input {"value":"0"}` 推进 → ERB 执行到 `THROW` → `CodeEE` 抛出 →
  `StepAsync` catch 块构造 fatal turn。
- `POST /input {"value":"1"}` 是退出路径，让测试可以选择不触发 fatal 后清理
  会话——但本测试主要路径是触发 fatal，1 路径仅作 ERB 完整性。

### 测试流程

1. `copy_test_game_with_erb(<erb_text>)` 复制 `test_game` 到临时目录，覆盖
   `erb/TEST.ERB`。
2. `start_server(game_dir)` 启动 server。
3. `POST /session` 创建会话，断言 `201`。
4. `GET /turn` 取 initial turn，断言：
   - `state == "WaitInput"`
   - `text` 包含 "Fatal Turn Test"
   - `protocolVersion == 1`（与 [PRD-T3](./PRD-T3-Turn协议版本化.md) 的 initial
     turn 断言一致——此处复用同一约束）
5. `POST /input {"value":"0"}` 触发 fatal。
6. `GET /turn` 取 fatal turn，断言：
   - `text == ""`
   - `buttons == []`
   - `"fatal-test-marker" in error`
   - `state != "WaitInput"`
   - `"protocolVersion" not in turn`（fatal turn 不是 initial turn）
7. （可选）`GET /state` 验证会话状态——fatal 后 server 应该 still alive
   （参照 [test_force_quit_survival.py](../../tests/test_force_quit_survival.py)
   的"I-11 exit survival"语义），但 fatal 是否走 `@QUIT` 路径需要实测确认。
   若 fatal 后会话直接进入 `Error` 状态、`GET /turn` 返回 404，则跳过此步。
8. `delete_session()` 清理。

### 边界处理

- **fatal turn 是否为 final turn**：当前 [AgentJsonlProtocol.cs#L67](../../EraCore.Core/Agent/AgentJsonlProtocol.cs#L67)
  fatal 路径调用 `Stop()`，会让 `RunLoopAsync` 下一轮退出，由 Session 走
  `BuildFinalTurn + GlobalStatic.Reset`。所以 `GET /turn` 拿到的可能是 fatal
  turn 本身，也可能是紧随其后的 final turn——这取决于 Session 的实现细节。
  - 测试应**对实现细节宽容**：断言"任一 turn 满足 fatal 结构"即可，不锁定
    "fatal turn 是第几个 turn"。
  - 具体写法：循环 `GET /turn` 直到拿到一个 `error` 字段非空的 turn，断言它
    符合 fatal 结构。若 30s 内未拿到，测试失败。
- **`state` 字段的具体值**：`ConsoleState` 枚举有 `Error=6`、`Quit=5`、
  `Running=7` 等（[ConsoleStateData.cs#L19-L27](../../EraCore.Core/UI/Game/Console/ConsoleStateData.cs#L19-L27)）。
  fatal 时 `console.State` 可能停在 `Running` 或其他——测试只断言
  `state != "WaitInput"`，不锁定具体值。
- **`error` 字段在 input rejection 路径也会出现**：[AgentProtocolBase.cs#L97](../../EraCore.Core/Agent/AgentProtocolBase.cs#L97)
  的 `OnInputRejected` 也填 `error`。测试用 "fatal-test-marker" 子串区分——
  rejection 路径的 error 是"当前需要整数输入，请重试"等中文提示，不会包含
  这个英文 marker。

### 涉及文件清单

| 文件 | 改动类型 |
|------|----------|
| `tests/test_fatal_turn.py` | 新增 |
| `tests/run_all.py` | 修改（加入 `test_fatal_turn.py` 到回归套件） |
| `tests/README.md` | 修改（测试清单新增 fatal turn 项） |

### 与 [PRD-T3](./PRD-T3-Turn协议版本化.md) 的依赖关系

本 PRD 是 [PRD-T3](./PRD-T3-Turn协议版本化.md) 的后续工作。**依赖关系**：
PRD-T3 必须先落地——因为 fatal turn 的 `protocolVersion` 字段不存在断言依赖
PRD-T3 引入的 `protocolVersion` 字段本身。

若 PRD-T3 未落地，`protocolVersion` 字段在所有 turn 里都不存在——fatal turn
断言"字段不存在"无意义。

实施顺序建议：
1. 先实施 [PRD-T3](./PRD-T3-Turn协议版本化.md)（TurnRecord + protocolVersion）。
2. 再实施本 PRD（fatal turn 测试）。

## Testing Decisions

### 测试哲学

仅测外部行为（HTTP 接口暴露的 turn JSON 结构），不测内部实现细节（C# catch
块结构、`TurnRecord` 构造点、`Stop()` 调用时机）。如果未来把 fatal 路径重构
成另一套机制，只要 wire format 不变，测试不应失败。

### 测试 Seam

复用现有 HTTP 集成测试 seam，与 [test_jsonl.py](../../tests/test_jsonl.py)、
[test_tinput_timeout.py](../../tests/test_tinput_timeout.py) 一致。**不新增
测试 seam。**

### Prior Art

- [test_tinput_timeout.py](../../tests/test_tinput_timeout.py)：
  `copy_test_game_with_erb()` 模式——复制 `test_game` + 覆盖 ERB + 启动
  server + 推进 + 断言。本测试直接复刻此结构。
- [test_jsonl.py](../../tests/test_jsonl.py)：turn 结构断言模式
  （`text`/`state`/`inputType`/`needValue`/`buttons` 字段检查）。
- [test_force_quit_survival.py](../../tests/test_force_quit_survival.py)：
  fatal 后 server 存活断言模式（若本测试需要扩展到 survival 验证）。

### 测试套件集成

加入 [run_all.py](../../tests/run_all.py) 的回归列表，位于
"TINPUT timeout" 之后、"I-11 exit survival" 之前——语义上是 timeout 与
survival 之间的"异常路径"测试。

### 不验证项

- **fatal 后 server 是否存活**：fatal 路径调用 `Stop()`，但 Session 的 finally
  会 `GlobalStatic.Reset()`——server 进程应仍能创建新会话。但这是
  [test_force_quit_survival.py](../../tests/test_force_quit_survival.py) 的
  语义范畴，本 PRD 不重叠。
- **fatal turn 是否为 final turn**：依赖 Session 实现细节，本测试用"循环
  `GET /turn` 直到拿到 error 非空的 turn"宽容处理。
- **`ex.Message` 的精确文本**：`THROW "fatal-test-marker"` 的 `ex.Message`
  就是 "fatal-test-marker"，但若未来引擎在 `CodeEE` 消息上加前缀（如
  "CodeEE: fatal-test-marker"），子串断言仍能通过——这是有意的宽容。

## Out of Scope

- **`@SYSTEM_TITLE` 启动期 fatal**：不走 `StepAsync` catch 路径，是另一条
  异常处理路径（`GetInitialTurnAsync` 异常冒泡到 Session finally）。若未来
  需要覆盖，单独立项。
- **多种异常类型覆盖**：除零、未定义函数、数组越界、`CodeEE`、`ExeEE` 等
  各类异常路径。本 PRD 仅覆盖 `THROW`（`CodeEE`）——这是最贴近"脚本运行期
  异常"语义的可控触发点。其他异常类型的覆盖属于延伸工作。
- **fatal 后 server 存活断言**：由 [test_force_quit_survival.py](../../tests/test_force_quit_survival.py)
  承担，本 PRD 不重叠。
- **C# 单测层验证 `TurnRecord` 字段**：与 [PRD-T3](./PRD-T3-Turn协议版本化.md)
  一致，不引入 C# 单测 seam——HTTP 接口是正确观察点。

## Further Notes

### 与 ADR-0001 的关系

ADR-0001 的 Rejected Alternatives
段记录了"为何 fatal 路径不调 `BuildTurn()`"——本 PRD 是该决策的测试守卫。
如果未来有人尝试把 fatal 路径合并进 `BuildTurn()`，本测试会在
`text == ""` 和 `buttons == []` 断言上失败，强制提醒 reviewer 重读 ADR。

### 与 [TODO.md](./TODO.md) 的关系

本 PRD 把 [TODO.md](./TODO.md) 中的"未覆盖测试：fatal turn 结构断言"项升格
为独立 PRD。PRD 落地后，[TODO.md](./TODO.md) 应标记此项为"已转 PRD-T4"。

### 实施前验证

实施者应在落地代码后验证：

1. `python tests/test_fatal_turn.py --binary <path> --game-dir test_game`
   全部通过。
2. `python tests/run_all.py --binary <path> --game-dir test_game` 全套通过——
   验证新增 fatal 测试未破坏现有套件。
3. [tests/README.md](../../tests/README.md) 的测试清单已更新。
