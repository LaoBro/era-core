# 运行 eraTW 画蛇添足版

本文是运行 **eraTW 画蛇添足版**（以及其它依赖 Emuera Skia 方言键的游戏）的操作手册。

结论先行：

1. **必须显式指定 `--profile eratw`**（MAUI 在设置页选“方言兼容 = eratw”，重载游戏后生效）；
2. **CLI/Server 必须能加载 `e_sqlite3.dll`**（MAUI Windows 已内置）；
3. 游戏自身的已知 `Lv2` 告警已在 `tests/games.json` 登记，不计入兼容性失败。

相关文档：

- 移植机制与新增方言键：[`dialect-porting.md`](dialect-porting.md)
- 66 键与已知差异：[`../reference/dialect-surface.md`](../reference/dialect-surface.md)
- 构建与发布：[`build-and-publish.md`](build-and-publish.md)

---

## 1. 为什么需要 profile

`eraTW 画蛇添足版` 使用了 Emuera Skia 分支新增的指令/函数（`TINPUTNF`、`TEXT_BGC_OFF`、`SQL_*` 等）。这些键默认不在 `v24pure`（移植前行为）的可见面里：

- 不指定 profile → 解析期报“无法解析的标识符/行” → 加载失败；
- 指定 `--profile eratw` → `baseline + snake + eratw` 三个模块的可见面生效；
- 绑定发生在构造 `Process` 之前，**不能对当前会话热切换**，必须重载游戏/重启 server。

### profile 一览

| profile | 模块闭包 | 用途 |
|---|---|---|
| `v24pure`（默认） | `baseline` | 普通 Emuera 游戏；与移植前行为逐位一致 |
| `snake` | `baseline + snake` | 只启用 Skia 语义键，不启用游戏适配 |
| `eratw` | `baseline + snake + eratw` | eraTW 画蛇添足版；当前 `eratw` 模块为空，等价于 `snake` |

> 当前 `eratw` 模块没有声明键：原计划的 `陥落状態/陷落状態` 兜底因游戏自带定义而 0 告警，已降级为非必需。`--profile eratw` 仍应作为运行该游戏的正式入口，以便未来游戏适配键落位。

---

## 2. 前置条件

### 2.1 软件

- .NET 10 SDK 或更高版本；
- 游戏目录（含 `ERB/`、`CSV/`、`SAV/` 等）；
- CLI/Server 运行 SQL 相关脚本时需要 SQLite 原生库（见 §2.2）。

### 2.2 `e_sqlite3.dll` 放在哪里

`EraCore.Core` 引用 `Microsoft.Data.Sqlite`，它经 `SQLitePCLRaw.bundle_e_sqlite3` 传递原生库。不同输出形态位置不同：

| 形态 | 原生库位置 | 说明 |
|---|---|---|
| CLI 框架依赖构建/发布 | `EraCore.Cli/bin/<Config>/net10.0/runtimes/win-x64/native/e_sqlite3.dll` | 依赖 `runtimes/` 目录结构，运行时按 RID 解析 |
| CLI 拷贝分发 | 建议把 `runtimes/win-x64/native/e_sqlite3.dll` 平铺到 `EraCore.Cli.exe` 同目录，或连同 `runtimes/` 整树一起拷 | 只拷 exe + 托管 dll 会 `DllNotFoundException` |
| MAUI Windows | `EraCore.Maui/bin/<Config>/net10.0-windows10.0.19041.0/win-x64/e_sqlite3.dll` | RID 发布已压平，随应用内置 |
| Android | 尚未接入 `SqliteRuntime` 式原生库定位 | 当前不在桌面闭环范围，见 §5 |

快速自检：

```powershell
# 构建目录里应该能看到
Test-Path EraCore.Cli/bin/Debug/net10.0/runtimes/win-x64/native/e_sqlite3.dll
```

如果只把 `EraCore.Cli.exe` 复制到别处，至少把 `e_sqlite3.dll` 一起复制过去：

```powershell
Copy-Item `
  EraCore.Cli/bin/Debug/net10.0/runtimes/win-x64/native/e_sqlite3.dll `
  <目标目录>/
```

---

## 3. 四种启动方式

以下命令均以仓库根为当前目录；`<游戏目录>` 替换为 eraTW 画蛇添足版根目录。

### 3.1 CLI 交互模式（需要真实 TTY）

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll `
  --ExeDir <游戏目录> --protocol cli --profile eratw
```

### 3.2 HTTP Server（推荐给 Web/Agent）

```bash
dotnet exec EraCore.Cli/bin/Debug/net10.0/EraCore.Cli.dll `
  --ExeDir <游戏目录> --server --port 8080 --profile eratw
```

启动日志应包含：

```text
dialect profile = eratw (source: --profile)
```

浏览器打开 `http://127.0.0.1:8080` 即可。

### 3.3 eracore_agent

```bash
python -m eracore_agent start --game-dir <游戏目录> --profile eratw
python -m eracore_agent acquire
python -m eracore_agent step --value 0
python -m eracore_agent release
```

注意：

- `--profile` 只对 **本次新拉起的 server** 生效；
- 如果复用已在运行的 server，以该 server 的启动参数为准；
- 请求的 profile 与复用 server 记录的 profile 不一致时，agent 会直接报错并要求先 `stop`，不会静默用错误可见面加载游戏。

### 3.4 MAUI Windows / Android

1. 打开设置页的“方言兼容”分组；
2. 选择 `eratw`（下拉值来自 `DialectRegistry.KnownProfileIds`，新增 fork 会自动出现）；
3. 提示“重载游戏后生效”——使用现成的“同目录一键重载”；
4. 启动日志/桥接日志应包含 `dialect profile = eratw (source: setting)`。

非法 profile id 会被设置页拒绝；外部改坏 Preferences 时，建会话前会 fail-fast，不会静默回落到 `v24pure`。

---

## 4. 验证是否真的跑起来了

### 4.1 单入口一致性套件

```bash
python tests/test_conformance_kit.py --game-dir <游戏目录> --build
```

它依次做：

1. 构建 CLI；
2. 校验三 profile 面快照；
3. 以 `tests/games.json` 里的 `profile`（eraTW 为 `eratw`）加载游戏；
4. 断言零“无法解析的标识符/行”、零未定义函数、无未预期 `warnLevel≥2`；
5. 进入输入循环（`expectState: WaitInput`）。

eraTW 的已知游戏数据缺陷登记在 `knownLv2`：

```json
"knownLv2": ["TEMP_CHARA_LIST_BY_DT.ERB:第931行"]
```

这是三份参考引擎（gEmuera / emuera.em / Skia）都会报的“循环外 CONTINUE”，不是 era-core 缺陷。

### 4.2 单元测试

```bash
dotnet test EraCore.Tests/EraCore.Tests.csproj
```

方言相关至少应包含：

- `DialectProfileTests`：profile 闭包、可见面减法、会话隔离；
- `DialectSurfaceSnapshotTests`：三 profile 面快照；
- `DialectCrossForkConflictTests`：同键多 owner 裁决；
- `P0*Tests`：每个 P0 家族的解析/执行/flag/可见性。

---

## 5. 已知差异（必须知道）

| 键 / 能力 | 状态 | 影响 |
|---|---|---|
| `SETIMAGELAYER` / `SETIMAGELAYERL` | 参数完整解析 + 5×5 校验，控制台接缝 no-op | 图像层不显示；观感归 era-core 渲染路线 |
| `CLEARIMAGELAYER` / `CLEARIMAGELAYER_ALL` | no-op | 同上 |
| `TEXT_BGC_ON` / `TEXT_BGC_OFF` | 保存 `EmueraConsole.TextBackgroundColor`，渲染线未消费 | 文本背景色不生效 |
| `TINPUTNF` 族的 NoFocus | 语义接缝保留；era-core 无强制滚动步 | 当前与普通 `TINPUT` 等价 |
| `SQL_*_FLOAT` 3 键 | 未实现（依赖浮点变量/浮点返回） | 仅使用 Float 变体的脚本会报未定义 |
| Skia 渲染/性能键 | 范围外（`SET_SKIA_QUALITY`、`BITMAP_CACHE_ENABLE`、lazyload 等） | 不影响 eraTW 主路径 |
| Android SQLite 原生库 | 未接入原生库定位 | Android 上 SQL 脚本暂不可用 |

完整清单与每个键的实现状态见 [`../reference/dialect-surface.md`](../reference/dialect-surface.md)。

---

## 6. 故障排查

| 症状 | 最可能原因 | 处理 |
|---|---|---|
| 启动即报未知 profile | id 拼错 | 用 `v24pure` / `snake` / `eratw`；日志会列出已知 id |
| `解釈できない行` / `无法解析的标识符`，且报错行含方言键 | 忘了 `--profile eratw`，或 MAUI 设置未生效 | 检查启动日志的 `dialect profile = ...`；MAUI 需重载游戏 |
| `DllNotFoundException: e_sqlite3` | 分发目录缺原生库 | 按 §2.2 复制 `runtimes/win-x64/native/e_sqlite3.dll` 或整树 |
| SQL 相对路径找不到数据库 | CWD 不是游戏目录 | era-core 已按 `ExeDir` 归一；确认 `--ExeDir` 指向游戏根 |
| agent 报 profile 不一致 | 复用了旧 server | `python -m eracore_agent stop`，再以 `--profile eratw` 重新 `start` |
| MAUI 改了设置但没变化 | profile 只在建会话前绑定 | 重载游戏（重建 `BridgeHost`） |
| 仍有 Lv2 告警 | 可能是新的方言缺口，也可能是已登记的游戏缺陷 | 先比对 `tests/games.json` 的 `knownLv2`；新键按移植指南补 |

---

## 7. 分发 checklist

- [ ] 游戏目录完整（`ERB/`、`CSV/`、`SAV/` 可写）
- [ ] CLI 分发目录含 `runtimes/win-x64/native/e_sqlite3.dll`（或平铺的 `e_sqlite3.dll`）
- [ ] 启动命令带 `--profile eratw`
- [ ] 首次启动确认日志出现 `dialect profile = eratw`
- [ ] 运行 `python tests/test_conformance_kit.py --game-dir <游戏目录>` 复核
