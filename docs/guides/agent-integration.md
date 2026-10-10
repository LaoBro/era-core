# Agent 集成

Agent 通过中转 CLI `eracore_agent` 操控游戏，不走 MCP。

**原则**：一次调用 = 一个回合，stdout 只有一行 turn JSON，错误走 stderr + 非零退出码；控制权排他（同一时刻一个 Controller），读取画面需先持有控制权，让权以显式 `release` 为准。

操控礼仪（启动序列、回合循环、紧凑视图的页语义截断、控制权交接与错误恢复）见自包含的 [`.agents/skills/eracore-playtesting/SKILL.md`](../../.agents/skills/eracore-playtesting/SKILL.md)——只凭它即可完成操控。回合/按钮字段的权威来源是 `TurnRecord` / `AgentJsonlProtocol`。

---

## 调用链

```text
agent
  └─ eracore_agent   (python -m eracore_agent <subcommand>
                     或安装后的 eracore_agent)
       └─ HTTP      /load-game /turn /input /state /control/*
            └─ EraCore.Server 或 MAUI 托管的 HttpListenerHost（agent 自动侦测复用）
                 ├─ EraCore.Core（共享层 Server/：协议 + 控制状态机 + 会话）
                 └─ GET /ws  →  Web 旁观（MAUI 托管 server 亦暴露同契约 /ws）
```

---

## `start` 的 server 复用优先级

自高到低：

1. **MAUI 托管 server** — 用户正在 MAUI 应用里玩的那一局。读 `%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json` 发现记录（端口/token/游戏目录动态）；命中即复用，不 load_game 抢占。
2. **本地记录** `.eracore-server.json` — 上次 `start` 拉起的 standalone server。
3. **`--host:--port` 上已在跑的 server** — 都没有才用 `--eracore-path` 二进制拉起新 server。

---

## 运行期产物（不要提交）

| 文件 | 内容 |
|------|------|
| `.eracore-agent.json` | 路径预设（`binaryPath` / `gameDir`） |
| `.eracore-server.json` | 本次运行记录（`host` / `port` / `pid` / `token` / `gameDir` / `startedByAgent` / `mauiHosted`）。`start` 写入，`stop` 删除，`release` 不删 |

---

## 回合协议

字段规格见 **[turn-protocol.md](turn-protocol.md)**（协议权威文档，含 `TurnRecord` 全字段、
`diff` 四种行操作、状态与输入类型表、版本演进史）。本节只给最小可用的形状。

一个会话产出**一帧一个 turn**：每回合是一个完整的「画面 + 输入需求」JSON。
`GET /turn` 长轮询取帧（25s 超时返 `204`，需继续轮询），`GET /ws` 收同一种裸帧。

```python
# 首帧自动产出（无需发任何命令），携带 protocolVersion：
{"awaiting": {"input": {"type": "IntValue"}},
 "diff": null, "protocolVersion": 12, "generation": 0}

# 客户端发送输入（POST /input 的 body）：
{"type": "input", "value": "0"}

# 服务端回下一个 turn，画面变化在 diff 里：
{"awaiting": {"input": {"type": "AnyKey"}},
 "diff": {"lineOps": [{"type": "append", "newLines": [/* ... */]}], "bgColor": "#101010"},
 "generation": 1}
```

要点三条，细节见规格文档：

- **`awaiting` 是唯一契约字段**（v12）——"服务端此刻在等什么"：`input` / `wait` 两个正交维度
  + `ended` 互斥终态键。七个旧顶层字段（`state`/`inputType`/`needValue`/`timeLimit` 等）已删除。
- **`diff` 是帧内增量**——`lineOps` 按序应用（`shift_head` → `clear_line_diff` →
  `clear_screen` → `append`）；`diff` 为 null/省略表示画面完全未变。
- **`text` / `buttons` 字段已不存在**（v4/v5 起被 `diff` 与 `generation` 取代）。
- **定时等待 / 让路看 `awaiting.wait`**：`durationMs > 0` 是定时等待（`displayTime` 为真时
  显示倒计时），`durationMs == 0` 是"下一帧立刻到"。`input` 缺省即"不接受输入"。
  🔴 **定时等待一律不可打断**——客户端在 `awaiting.input` 缺省期间投的任何输入都会被
  服务端吞掉（不推进、不报错），等待只由服务端定时器结束（`interruptible` 键已撤回，
  见 issue 10）。

参考 [`tests/emuera_server.py`](../../tests/emuera_server.py)（server 模式测试 helper）
与 [`tests/test_jsonl.py`](../../tests/test_jsonl.py)（示例）。
