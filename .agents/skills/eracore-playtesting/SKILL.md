---
name: eracore-playtesting
description: Playtest Emuera games via eracore_agent. Use when driving test_game or an ERB game, handing control to the user, or recovering after a steal or CONTROL_LOST.
---

# Emuera Playtesting

用 `python -m eracore_agent <subcommand>` 操控本仓库的 Headless 游戏，也支持连到正在跑的 MAUI 应用会话。一次调用 = 一个回合；stdout 只有 JSON，错误在 stderr + 非零退出码。本文档自包含：**只凭下述命令与判据即可完成操控，无需再检索代码库**。

术语（Controller / Spectator / Acquire / Release / Steal / Lease / Control Event / Controller Kind）以根目录 [`CONTEXT.md`](../../../CONTEXT.md)「控制权交接」为准。回合/按钮字段由 `AgentJsonlProtocol` / `TurnRecord` / `DisplaySnapshot` 决定，本文不复述协议结构。

## 0. 连接目标（谁被操控）

`eracore_agent` 背靠一个单实例会话。`start` 的复用优先级（自高到低）：
1. **MAUI 托管 server**（用户正在 MAUI 应用里玩的那一局——`%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json`，端口/token/游戏目录动态）。命中即复用，不另起 server，不 load_game 抢占。
2. **本地记录** `.eracore-server.json`（上次 `start` 拉起的 standalone server）。
3. `--host:--port` 上已在跑的 server。
4. 都没有才用 `--eracore-path` 二进制拉起新 server。

所以：若用户正在 MAUI 里玩，直接 `start` 就会连到那一局（agent 与 user 共享同一局，控制权排他）。

## 1. 启动序列

```bash
python -m eracore_agent start --game-dir test_game   # 或省略 --game-dir 用配置/默认 test_game
python -m eracore_agent acquire
```

`start` 复用或拉起 server 并写 `.eracore-server.json`。其它子命令在记录文件缺失或端口不通时直接报错退出；只有要自己开局、确认复用、或想让 `start` 刷新连接记录时才再 `start`。

完成标准：`acquire` 的 stdout 含 `controller.kind == "agent"`，以及 `state` / `turn` / `turnsAdvanced`。先读确认再 `step`。

## 2. 读取当前画面

**始终先看画面再动手**。两种读法：

```bash
python -m eracore_agent view                 # 低噪紧凑视图：纯文本行 + 按钮({id,label})
python -m eracore_agent view --tail 80       # 只返回最近 80 行（长历史时避免拖垮上下文）
```

- `view` 输出：`{state, inputType, needValue, generation, lines:[string], buttons:[{id,label}]}`。
- **按钮已过滤**：只保留当前有效代（同 `generation` 的按钮），翻页留在历史里的旧按钮不会混入。直接凭 `buttons[].label` 定位可交互项、用 `buttons[].id` 作为 `step --value` 的输入。
- `--tail N` 只截断文本行；按钮已按代过滤，不受行数影响。
- **防偷窥 / 排他**：`view` 会先 `acquire`（需成为 Controller 才能读），读后**保持持有**、不自动 release——直到任务完成主动 `release`，或 user 强行夺回（报 `CONTROL_LOST`）。若控制权被 user 持有则报 `CONTROL_HELD_BY_USER` 拒绝读取。
- 需要全量渲染细节时另调 `GET /snapshot`（返回 `lines[].entries[].segments[]` / `button{col,width}` 等完整结构）——大多数 agent 回合用 `view` 就够。

## 3. 回合循环

```bash
python -m eracore_agent step --value <输入>     # <输入> 取自 view 的按钮 id，或 inputType/needValue 提示的整数/字符串
python -m eracore_agent step --value 123
python -m eracore_agent step --value "0" --full # --full 输出完整 diff（默认即低噪紧凑视图）
```

每步之后看 `state`：

| `state` | 下一步 |
|---|---|
| `WaitInput` | 按 `inputType` / `needValue` / 按钮值再 `step` |
| `Quit` / `Error` | 走第 6 步收尾 |
| 其它 | `status` 确认后再决定 |

完成标准：每一次 `step` 都对应自己刚提交的输入；不要在未读确认时连打。`acquire`/`step`/`advance` **默认输出低噪紧凑视图**（`lines`+`buttons`），如需原始 diff 结构用 `--full`；`--tail N` 只截断紧凑视图的文本行数。

### 3.1 自动推进（advance）

遇到 `EnterKey` / `AnyKey`（`needValue=false`）且判定为无聊翻页时，用 `advance` 一次推进到需要真实输入，不必逐条 `step --value ""`：

```bash
python -m eracore_agent advance --max-steps 50
```

- 返回单行 JSON：`{"turns":[...], "stopped":{...}|null, "advancedCount":N, "limitReached":bool}`，`turns` 是每个被推进回合（含内容），`stopped` 是需要真实输入的回合（`limitReached` 为真时 `stopped` 为 null）。
- 结束后读 `stopped` 回合再决定下一步；被强夺时 advance 自动停止并报 `CONTROL_LOST`，照第 4 步处理。
- 只在确认是翻页时用；若该 EnterKey 是确认框/剧情节点，就正常 `step` 停下处理。

## 4. 被强夺

`step` / `view` / `advance` 报 `CONTROL_LOST`，或 `GET /control/wait` 收到 `stolen`：

1. 立刻停下，不再 `step` / `view`。
2. 向用户说明停在哪一回合、最后一次输入、当前 `state`。
3. 等用户让权后再 `acquire` 读确认（看 `turnsAdvanced`）。

完成标准：被强夺后零次自动 `acquire`。

## 5. 用户持有时等待

`acquire` 报 `CONTROL_HELD_BY_USER`，或 `status` 显示 `controller.kind == "user"`：

1. 向用户说明你在等，并报当前进度。
2. 长轮询 `GET /control/wait`（204 表示本轮无事件，继续同一条长轮询）。

```bash
curl -sS "http://127.0.0.1:8080/control/wait"
```

主机和端口以 `.eracore-server.json` 为准。`released` / `lease_expired` 后再 `acquire`。若事件是 `game_ended`，走第 6 步。

完成标准：等待期间没有连续的 `acquire` / `step` 重试。

## 6. 游戏结束

`state` 为 `Quit` / `Error`，或收到 `game_ended`：会话会清掉 Controller，不必再 `release`。若 `.eracore-server.json` 里 `startedByAgent` 为真（这次 server 是你拉起的），立刻：

```bash
python -m eracore_agent stop
```

完成标准：自己拉起的 server 已停，`.eracore-server.json` 已删除。MAUI 托管会话（`mauiHosted:true`）时 `stop` 只清本地记录，不杀用户会话。

## 7. 任务完成或把游戏交给用户

游戏还在进行、任务结束，或用户要接手：

```bash
python -m eracore_agent release
```

`release` 只让权，不关 server。只有你启动的 server、且整局结束时才 `stop`。

完成标准：用户侧 Web / MAUI 可以输入；你不再 `step`。

## 约束

- 同一时刻一个活跃会话、一个 Controller。第二个 agent 的 `acquire` 得 `CONTROL_HELD`——停下并告诉用户，不要并行开第二局。
- 控制权排他且不"自动归还"：即使 `view`/`step`/`advance` 完成，你仍是 Controller，直到主动 `release` 或 user 强行夺回。交接以显式 `release` 为准。
- `watch` 只读旁观，不能从终端接管；接管入口在 Web / MAUI（旁观横幅或 Ctrl+T）。
- 身份 token 由 `start` 写入 `.eracore-server.json`，后续命令自动携带。文件缺失时按 CLI 报错处理；不要手造 token。
