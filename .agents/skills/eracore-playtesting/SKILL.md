---
name: eracore-playtesting
description: Playtest Emuera games via eracore_agent. Use when driving test_game or an ERB game, handing control to the user, recovering after a steal or CONTROL_LOST, or driving the MAUI app (list games / auto-load / measure load time) before a session exists.
---

# Emuera Playtesting

用 `python -m eracore_agent <subcommand>` 操控本仓库的 Headless 游戏，也支持连到正在跑的 MAUI 应用会话。一次调用 = 一个回合；stdout 只有 JSON，错误在 stderr + 非零退出码。本文档自包含：**只凭下述命令与判据即可完成操控，无需再检索代码库**。

术语（Controller / Spectator / Acquire / Release / Steal / Lease / Control Event / Controller Kind）以根目录 `CONTEXT.md`「控制权交接」为准（该文件仅存在于本地工作区、不入库）。回合/按钮字段由 `AgentJsonlProtocol` / `TurnRecord` / `DisplaySnapshot` 决定，本文不复述协议结构。

## 0. 连接目标（谁被操控）

`eracore_agent` 背靠一个单实例会话。`start` 的复用优先级（自高到低）：
1. **MAUI 托管 server**（用户正在 MAUI 应用里玩的那一局——`%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json`，端口/token/游戏目录动态）。命中即复用，不另起 server，不 load_game 抢占。**该记录在 MAUI app 启动到列表页时就已存在**（控制 server 随 app 启动上线）——所以未加载游戏时 `start` 也能复用 MAUI，不会退回 standalone。
2. **本地记录** `.eracore-server.json`（上次 `start` 拉起的 standalone server）。
3. `--host:--port` 上已在跑的 server。
4. 都没有才用 `--eracore-path` 二进制拉起新 server。

所以：若用户正在 MAUI 里玩，直接 `start` 就会连到那一局（agent 与 user 共享同一局，控制权排他）。

## 0.5 MAUI 控制面（未加载游戏时也可用）

`python -m eracore_agent maui <subcommand>` 驱动 MAUI app 本体。控制 server 在 app 启动期即上线，因此可以**在没有任何游戏会话时**列出游戏并触发加载：

```bash
python -m eracore_agent maui status           # 探活：state / gameDir / mainGameDir / 端口
python -m eracore_agent maui games            # 列主目录下的游戏（Android SAF 路径同样可用）
python -m eracore_agent maui load --name TK   # 按名加载（先 scan 主目录解析出 fullPath）
python -m eracore_agent maui load --path "D:\era\TK"
```

- **端口/token/主目录来源**：Windows 读 `%LOCALAPPDATA%\EmueraCore\eracore-maui-server.json`；Android 自动 `adb logcat -d -s EmueraMaui` 解析 `eracore-control-server port=...` 行并 `adb forward`（加 `--adb` 强制走此路径；`games`/`load --name` 缺省用 app 同步的 `mainGameDir`，无需手输 SAF URI）。
- **`load` 是异步受理**：返回 `accepted` 后 CLI 轮询 `/state` 等 `gameDir` 变为目标目录（默认 15s，`--wait-timeout` 可调；超时不算失败——加载可能仍在进行）。
- **加载链路与手点完全一致**（C# 转 Vue `loadGameFromPath` → `OnReloadGame`）：测量启动耗时用 logcat 的 `OnReloadGame` / `hosted session` 标记，口径不被自动化污染。
- **真机息屏必须先提醒用户点亮**（2026-09-09 实测教训）：息屏触发 USB 节电——`adb logcat -d` 曾实测 30s 超时，且 CPU 调度不同使加载耗时与亮屏**不可比**（息屏轮次数据作废）。检测：`adb shell "dumpsys deviceidle get screen"` 返回 `false` 即息屏。注意本机 shell 无 `INJECT_EVENTS` / `WRITE_SECURE_SETTINGS` 权限——`input keyevent` 亮屏和改息屏超时都会被 SecurityException 拒绝，**只能提醒用户手动点亮并在整个测量期间保持**。
- **加载完成后**：`start` + `acquire` 即可接管这一局（复用 MAUI 会话，不需要也不要再 load_game）。
- 退出游戏回列表页后发现记录仍有效（gameDir 置空、server 不下线）——此时可再 `maui load` 换下一个游戏。

## 1. 启动序列

```bash
python -m eracore_agent start --game-dir test_game   # 或省略 --game-dir 用配置/默认 test_game
python -m eracore_agent acquire
```

`start` 复用或拉起 server 并写 `.eracore-server.json`。其它子命令在记录文件缺失或端口不通时直接报错退出；只有要自己开局、确认复用、或想让 `start` 刷新连接记录时才再 `start`。

完成标准：`acquire` 的 stdout 含 `controller.kind == "agent"`，以及 `state` / `turn` / `turnsAdvanced`。先读确认再 `step`。

### 1.1 方言 profile（`--profile`）——加载真实游戏时**必须**

`--profile` 只对**本次新拉起**的 server 生效，取值 `v24pure`（默认）/ `snake` / `eratw`。

```bash
python -m eracore_agent start --game-dir "d:/era/eratw-sub-modding-develop" --profile eratw
```

- **eraTW 画蛇添足版必须 `--profile eratw`**。不带则 ERB 加载到一半就终止，`start` 返回
  `awaiting.ended.terminal == "Error"` —— 排查见 §11。
- **复用已有 server 时 profile 改不了**：CLI 有守卫，请求的 profile 与在跑 server 的记录
  不一致会直接报错退出 ⇒ 换 profile 必须先 `stop` 再 `start`（不能指望 `start` 覆盖）。
- `test_game` 不需要 profile（默认 `v24pure` 即可）。

## 2. 读取当前画面

**始终先看画面再动手**。两种读法：

```bash
python -m eracore_agent view                 # 低噪紧凑视图：最近 3 页文本 + 按钮({id,label}) + 截断元数据
python -m eracore_agent view --pages 5       # 截取最近 5 页
python -m eracore_agent view --tail 80       # 绝对行数逃生口：只返回最近 80 行（优先于 --pages）
```

- `view` 输出：`{state, inputType, needValue, generation, lines:[string], buttons:[{id,label}], pageRows, totalLines, truncated}`。
  ⚠️ **v12 后 `state` / `inputType` / `needValue` 已不是游戏状态**（见 §2.1）——判定「游戏在等什么」
  一律读 `awaiting`；`inputType` / `needValue` 恒 `null`，顶层 `state` 是**控制权**状态。
- **截断以页为单位**：`pageRows` = 一屏行数，服务端按游戏 emuera.config 的窗口高度/行高换算（`/state` 的 `visibleRows`；旧 server 无此字段时兜底 40）。`totalLines` = console 全缓冲行数，`truncated` = 本次是否截断——据此判断"看到 N/总 M 行"。确认状态一般 2~3 页足够。
- **按钮已过滤**：只保留当前有效代（同 `generation` 的按钮），翻页留在历史里的旧按钮不会混入。直接凭 `buttons[].label` 定位可交互项、用 `buttons[].id` 作为 `step --value` 的输入。
- `--tail N` 只截断文本行；按钮已按代过滤，不受行数影响。
- **防偷窥 / 排他**：`view` 会先 `acquire`（需成为 Controller 才能读），读后**保持持有**、不自动 release——直到任务完成主动 `release`，或 user 强行夺回（报 `CONTROL_LOST`）。若控制权被 user 持有则报 `CONTROL_HELD_BY_USER` 拒绝读取。
- 需要全量渲染细节时另调 `GET /snapshot`（返回 `lines[].entries[].segments[]` / `button{col,width}` 等完整结构）——大多数 agent 回合用 `view` 就够。

### 2.1 ⚠️ `state` 同名不同义（v12 收缩后的第一大坑）

| 出处 | `state` 是 | 取值 |
|---|---|---|
| `view` / `acquire` / `step` 的紧凑视图 | **控制权状态** | `held` / … |
| `status` | **游戏状态** | `WaitInput` / `Quit` / `Error` / … |
| `start` 的返回 | 没有该字段 | 只有 `awaiting` / `protocolVersion` / `generation` |

**游戏状态在 v12 后只由 `awaiting` 表达**：`{"input":{"type":...}}` = 等输入、
`{"wait":{"durationMs":N}}` = 定时等待/让路、`{"ended":{"terminal":"Quit"|"Error"}}` = 终态。
旧文档里"看 `state` 决定下一步"的说法已失效——照它做会把 `held` 当成游戏状态。

## 3. 回合循环

```bash
python -m eracore_agent step --value <输入>     # <输入> 取自 view 的按钮 id，或 inputType/needValue 提示的整数/字符串
python -m eracore_agent step --value 123
python -m eracore_agent step --value "0" --full # --full 输出完整 diff（默认即低噪紧凑视图）
```

每步之后看**游戏状态 = `awaiting`**（不是顶层 `state`，见 §2.1）：

| `awaiting` | 含义 | 下一步 |
|---|---|---|
| `{"input":{"type":...}}` | 在等输入 | 按 `input.type` / 按钮 `id` 再 `step` |
| `{"ended":{"terminal":"Quit"\|"Error"}}` | 终态 | 走第 6 步收尾 |
| 只有 `wait`（无 `input`） | 定时等待 / 让路 | **不要 `step`**——输入会被吞掉，等它自己结束（见 §6.1） |
| 其它 | 中间态 | `status` 确认后再决定 |

完成标准：每一次 `step` 都对应自己刚提交的输入；不要在未读确认时连打。`acquire`/`step`/`advance` **默认输出低噪紧凑视图**（`lines`+`buttons`+`pageRows`/`totalLines`/`truncated` 元数据，最近 3 页），如需原始 diff 结构用 `--full`；`--pages N` 调页数、`--tail N` 用绝对行数（优先于 --pages）。

### 3.1 自动推进（advance）

遇到 `EnterKey` / `AnyKey`（`needValue=false`）且判定为无聊翻页时，用 `advance` 一次推进到需要真实输入，不必逐条 `step --value ""`：

```bash
python -m eracore_agent advance --max-steps 50
```

- **默认（紧凑）**：返回单行 JSON = 紧凑视图（同 `view` 契约，含 `pageRows`/`totalLines`/`truncated`）+ `advancedCount`/`limitReached`。**不含 `turns`/`stopped`**——紧凑视图本身就是所停的画面，`limitReached` 区分停因（true=步数用尽，false=停在需真实输入）。
- `advance --full` 才返回 `{"turns":[...], "stopped":{...}|null, "advancedCount":N, "limitReached":bool}`：`turns` 是每个被推进回合（含内容），`stopped` 是需要真实输入的回合（`limitReached` 为真时为 null）。
- 结束后读紧凑视图（或 `--full` 的 `stopped`）再决定下一步；被强夺时 advance 自动停止并报 `CONTROL_LOST`，照第 4 步处理。
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

`awaiting.ended.terminal` 为 `Quit` / `Error`（不是顶层 `state`，见 §2.1），或收到 `game_ended`：会话会清掉 Controller，不必再 `release`。若 `.eracore-server.json` 里 `startedByAgent` 为真（这次 server 是你拉起的），立刻：

```bash
python -m eracore_agent stop
```

完成标准：自己拉起的 server 已停，`.eracore-server.json` 已删除。MAUI 托管会话（`mauiHosted:true`）时 `stop` 只清本地记录，不杀用户会话。

### 6.1 长动画脱身——没有"跳过"，只能等或拆会话

定时等待（`awaiting` 只有 `wait`、没有 `input`）期间的客户端输入**一律被吞掉**：不推进、
不报错。这是刻意为之（参考实现也不能打断）。

叠加另两条事实，某些动画**没有干净退路**：

- 脚本轮询式打断（`GETTEXTBOX()` / `GETKEYTRIGGERED()`）在 headless 下恒为空/0 ⇒ 脚本里的
  退出分支永不命中；
- 引擎侧快进（`\e` / `MESSKIP()`）只在**值输入等待**上能启动，定时等待上启动不了。

⇒ eraTW 的 `@BADAPPLE` 是 6573 帧 / 约 3 分 37 秒，**中途无法打断**。要在动画期间脱身，
只有 `stop`（拆会话）——别指望 `step` 能把它停下来，也不要以为卡住了。

## 7. 任务完成或把游戏交给用户

游戏还在进行、任务结束，或用户要接手：

```bash
python -m eracore_agent release
```

`release` 只让权，不关 server。只有你启动的 server、且整局结束时才 `stop`。

完成标准：用户侧 Web / MAUI 可以输入；你不再 `step`。

## 8. 启动日志覆盖（DisplayReport override）

游戏配置的加载报告（逐文件加载日志 + `time.log` 阶段计时）默认被 server 启动参数压制为关，排查启动性能/加载问题时用 `start` 的透传参数控制（仅对本次新拉起的 server 生效；复用已运行 server 时以该 server 自己的启动参数为准）：

```bash
python -m eracore_agent start --game-dir <目录> --display-report on   # 强制开：逐文件报告 + time.log
python -m eracore_agent start --game-dir <目录> --display-report auto # 跟随游戏配置
# 缺省 = off（强制关，历史默认——逐文件报告对回合输出是噪音）
```

- `on` 时 `time.log` 写在**游戏目录**（`File:Preload:End` / `Proc:Init:ERB:End` 等各阶段累计毫秒数）；测完记得 `stop` 并删掉 time.log，别留在用户的游戏目录里。
- 手动直起 server 也可（`EraCore.Cli.exe --server --port <p> --ExeDir <目录> --loading-report`，与 `--no-loading-report` 互斥），但会绕开 `.eracore-server.json`（无 token / 无 stop 清理），能用 start 就用 start。注意 Release 产物在 `bin-aot/Release/`（不是 `bin/Release/`）。
- **MAUI 托管会话**（`mauiHosted:true`）：跟随 app 设置「启动日志覆盖」三态——跟随游戏 / 强制关（默认）/ 强制开。测量前先与用户确认 app 内设置。

## 9. 录制原始帧（`record`）——定位渲染 / 协议问题

`view` / `watch` 只给**某一瞬间**的画面，看不出**每帧搬运了几行**。连续帧的 `diff.lineOps`
才是「画面只有一部分在更新」「行数恒定帧流」「帧间隔是否积压」这类问题的判据。

```bash
python -m eracore_agent record --seconds 2                      # 收 2 秒
python -m eracore_agent record --seconds 3 --post-input 2 --out frames.jsonl
```

| 参数 | 说明 |
|---|---|
| `--seconds FLOAT` | 录制时长（默认 2.0） |
| `--out PATH` | 原始帧落盘（JSONL，每行一帧）；缺省不落盘 |
| `--post-input VALUE` | **连上 WS 后**再提交该输入再录制 —— 抓动画的**第 1 帧**（需控制权，会自动 `acquire`） |

读法（stdout 的 `summary` + `rows`；人读表格走 stderr，stdout 仍只有 JSON）：

| 字段 | 判据 |
|---|---|
| `net` | `append` 行数 − `clear_line_diff` 行数。**恒为 0** ⇒ 行数不变帧流（前端按行数变化触发的滚动/吸底全程不会触发） |
| `lines` | 以录制起点 `GET /snapshot` 行数为基线、逐帧应用 lineOps 后的总行数 |
| `gap` | 相邻帧到达间隔；与脚本里的 `AWAIT t` 对比即可判断服务端是否积压 |
| `netConstant` / `linesConstant` | 上面两条的聚合布尔，先看这两个 |

🔴 **必须先连 WS 再 `--post-input`**（CLI 已保证此顺序）：反序会漏掉第 1 帧，而那帧往往
是唯一能看出「本帧到底打印了多少显示行」的一帧。
🔴 `lines` 只在**没漏帧**时有意义；漏帧会让基线一直偏小。

实测样例（eraTW `@BADAPPLE`）：

```
  0     18.7  {'append': 156}                              156    292
  1     49.1  {'clear_line_diff': 11, 'append': 11}          0    292  (+30.5)
  2     92.0  {'clear_line_diff': 11, 'append': 11}          0    292  (+42.9)
```

帧 0 一次性 `append 156`（100 空行 + 45 行字符画 + 10 行 UI），之后每帧只搬运 11 行、
净增 0 —— 而字符画有 45 行 ⇒ 每帧只更新最后 1 行，前 44 行从第一帧起再没被写过。
需要 `pip install websockets`。

## 10. 全局设置（`settings`，ADR-0030）

```bash
python -m eracore_agent settings list [--adb]
python -m eracore_agent settings get <key>
python -m eracore_agent settings set <key> <value>       # perf / 诊断旋钮下次 load-game 生效
python -m eracore_agent settings reset                   # 清 headless 内存 store，回出厂 auto
```

13 个键，`list` 看全量。`--adb` 走 Android MAUI 控制 server（同 §0.5）。

## 11. 加载失败排查（`start` 后 `awaiting.ended.terminal == "Error"`）

先看三处，**按这个顺序**——日志里往往什么都没有：

1. **`status`**：`state` / `isRunning` / `threadAlive` / `lastError`。
   ⚠️ `lastError` **经常是 `null`**，别据此判断"没出错"。
2. **profile 对不对**（§1.1）——eraTW 没带 `--profile eratw` 就是这个症状，且极难从日志看出：
   `agent.log` 只会停在 `LoadPendingParallel: merged=3840/3936` 之类的进度行上，**没有任何错误行**。
3. **游戏目录**：`status` 的 `game.gameDir` 是否真是你要加载的那个。

都没收获再翻 `logs`（§8）与 `time.log`（需 `--display-report on`，写在**游戏目录**里）。

### 编码

Windows PowerShell 下 `view` / `acquire` / `step` 的**中文输出是 GBK 乱码**（CLI 输出 UTF-8，
控制台按 GBK 解码）。要读准中文文本行，别在 PowerShell 里硬看——直接取
`GET /snapshot`（自己写几行 python `urllib` 拉 JSON），或先把控制台切到 UTF-8
（`chcp 65001`）。

## 约束

- 同一时刻一个活跃会话、一个 Controller。第二个 agent 的 `acquire` 得 `CONTROL_HELD`——停下并告诉用户，不要并行开第二局。
- 控制权排他且不"自动归还"：即使 `view`/`step`/`advance` 完成，你仍是 Controller，直到主动 `release` 或 user 强行夺回。交接以显式 `release` 为准。
- `watch` 只读旁观，不能从终端接管；接管入口在 Web / MAUI（旁观横幅或 Ctrl+T）。
- 身份 token 由 `start` 写入 `.eracore-server.json`，后续命令自动携带。文件缺失时按 CLI 报错处理；不要手造 token。
