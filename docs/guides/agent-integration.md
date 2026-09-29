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

## JSONL 协议

无头运行器的 JSONL 协议由 server 模式（`--server`）通过 HTTP 暴露，适合脚本和自动化。创建会话后游戏自动输出初始 turn，之后每发送一条输入命令返回一个 turn。

```python
# 游戏自动输出初始 turn（无需发送任何命令）：
{"text": "标题画面...", "state": "WaitInput", "inputType": "IntValue", "needValue": true, "buttons": [...]}

# 客户端发送输入：
{"type": "input", "value": "0"}

# 服务器响应下一 turn：
{"text": "输出文本...", "state": "WaitInput", "inputType": "IntValue", "needValue": true, "buttons": [...]}
```

参考 [`tests/emuera_server.py`](../../tests/emuera_server.py)（server 模式测试 helper）与 [`tests/test_jsonl.py`](../../tests/test_jsonl.py)（示例）。
