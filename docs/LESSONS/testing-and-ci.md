# 构建 / 测试 / CI 环境 失败教训

> **TL;DR**：增量构建"0 警告"是假象（强制重编译才见真警告）；Android 构建失败先查环境（文件锁/安全软件/残留进程）再改代码；行号锚定基线的测试脆弱；残留进程会干扰回归矩阵；code-review 用两轴（Standards + Spec）抓单轴漏掉的实现偏离；**Windows 全绿不等于跨平台正确**——触碰文件名大小写/分隔符/路径语义的改动必须过 WSL2 ext4 关卡，且「只测能加载」的弱断言会漏掉运行期分支；**含中文的 .ps1 必须 UTF-8 with BOM**（Windows PowerShell 5.1 按 GBK 读无 BOM 文件 → ParserError）。

## 教训清单

| # | 标题 | 一句话 | 日期 |
|---|---|---|---|
| 1 | 增量构建"0 警告"是假象 | touch 改动文件强制重编译才见真实警告 | 2026-08-04 |
| 2 | Android 构建报文件锁多为环境问题 | 360/残留进程锁 bin；先查环境再改代码 | 2026-08-04 |
| 3 | 游戏无删档功能时用删 sav 重建覆盖路径 | 进程外删 sav 让游戏重建，验证建目录路径 | 2026-08-04 |
| 4 | 两轴 code-review（Standards + Spec） | Spec 轴对照已批准计划逐行核对抓实现偏离 | 2026-08-04 |
| 5 | 写"已存在文件"被拒多为安全软件/残留句柄 | 排障顺序：残留句柄 → 安全软件 → 文件系统 | 2026-08-07 |
| 6 | 行号基线的测试在源码行号偏移时误报 | 改 Shared/** 后同步更新基线行号 | 2026-08-07 |
| 7 | 残留进程干扰回归矩阵 | 跑全量回归前先清残留进程 | 2026-08-07 |
| 8 | 操纵全局静态的测试必须串行 | DisableParallelization 集合；高频日志放大竞态 | 2026-08-07 |
| 9 | 日志门面改默认阈值要与终端断言同步 | T-027 TerminalSink 默认 Warn，Info 级终端标志测试永远找不到；spawn 设 EMUERA_LOG_TERMINAL=info | 2026-08-10 |
| 10 | Python print 双重编码在 GBK 控制台乱码 | encode/decode(errors="replace") 把中文变 �；直接 print（WinConsoleIO 写 UTF-16） | 2026-08-10 |
| 11 | MAUI 前端资产 glob 依赖 load-time 目录存在 | CI 全新 checkout 无 wwwroot → MauiAsset 收集为空 → APK 缺前端 → 整页 ERR_NAME_NOT_RESOLVED；排查以最终 APK 为准 | 2026-08-22 |
| 12 | Windows 全绿≠跨平台正确 | 大小写/分隔符类缺陷本地无法复现，须过 WSL2 ext4；夹具命名同形 + 「只测能加载」会漏运行期 | 2026-09-12 |
| 13 | publish.ps1 在 Windows PowerShell 5.1 解析失败 | UTF-8 无 BOM 中文注释被按 GBK 读 → ParserError；加 BOM 或直跑内部命令 | 2026-09-17 |

---

## 11. `MauiAsset Include="wwwroot\**\*"` 是 load-time glob——CI 全新 checkout 收集不到，APK 缺前端资源

**场景**：GitHub Actions 构建的 MAUI Android APK 打开后整页报 `ERR_NAME_NOT_RESOLVED`（`https://game.local/wwwroot/index.html` 无法解析）；本地 Release APK 完全正常。Debug 版无法启动（缺运行时依赖），故无法用 Debug/Release 对照做第一轮排查。

**结果**：CI 产物 APK 里**没有 `assets/wwwroot/index.html`** → `WwwrootPathHandler`（`Assets.Open`）找不到文件返回 null → WebView 放行到真实网络解析 `game.local` → DNS 失败。曾误判为 Full AOT/JNI 通道（`RunAOTCompilation` + `AndroidEnableMarshalMethods=false`）问题，方向错了。

**原因**：`EraCore.Maui.csproj` 的 `MauiAsset Include="wwwroot\**\*"` 在 **MSBuild project evaluation（load-time）** 做 glob；而 Vue 前端产物由 `build/VueBuild.targets` 在 **Build target 阶段**才 `Copy` 进 `wwwroot/`。本地开发时 `EraCore.Maui/wwwroot` 残留着历史构建产物，load 阶段能扫到 → 正常；CI 全新 checkout 没有该目录（`.gitignore` 排除、不入库）→ glob 为空 → `MauiAsset` 一个文件都没收集 → APK 里没有前端资源。这解释了"本地正常、CI 异常"的割裂。

**排查要点（本次踩坑）**：
- `obj/.../android/assets` 中间目录**不是最终依据**——本地 `obj/Release/net10.0-android/android/assets` 里没有 wwwroot，但最终 APK 里有。必须以**最终 APK 内容**为准：`tar -tf com.eracore.maui-Signed.apk | grep assets/wwwroot`。
- 整页 `ERR_NAME_NOT_RESOLVED` 不一定是网络/JNI 问题——**资源缺失被 PathHandler reject 后放行到真实网络**是同一症状的另一条路径（LESSONS `maui-android-webview.md` §6 记载了图片层同名症状，本次是整页层）。
- 本地模拟 CI 干净场景可复现：`Remove-Item wwwroot` → 预置 dist-maui → 构建 → 查 APK。

**解决**：release workflow 的 MAUI job 在 publish 前显式两步：`npm run build -- --base=./ --outDir=dist-maui` 生成最新产物，再 `Copy-Item` 到 `EraCore.Maui/wwwroot`，让 load-time glob 在 CI 也能拾取——与本地正常路径一致。已本地模拟验证 APK 正确包含 `assets/wwwroot/*`。

**教训**：
1. **load-time glob（`MauiAsset`/`Content`/`None` 的 `Include`）只拾取"项目加载时已存在"的文件**；由构建 target 在 build 期才产生的文件，要么在 target 内动态 `ItemGroup` 追加，要么在 workflow 预置，不能依赖 load-time glob。
2. **"本地正常、CI 异常"优先查构建环境差异**（有无残留产物/目录是否存在），别急着归因运行时、编译器或 AOT——用 diff 两边的产物内容而不是猜。
3. **排查打包问题以最终产物为准**（APK zip 内容 / publish 目录），obj 中间目录会误导；`git clean` 或 CI 全新建模拟是复现环境类 bug 的标准手段。


## 1. 增量构建"0 警告"是假象——强制重编译才能看到真实警告

**场景**：O1/O4 构建显示"0 警告"，code-review 后 touch 两个改动文件强制重编译，暴露出 41 个既有警告（CS1570 XML 注释格式错误、CA1416、CS8603），其中 3 处 CS8603 还是 O1 引入的隐藏警告（`ParentDocIdOf`/`ResolveDocId` 的 Android API nullable 标注）。

**原因**：增量构建只重编译改动部分，未重编译文件的警告不显示；`DocumentsContract.GetTreeDocumentId`/`GetDocumentId` 返回 `string?`，未加 `!`。

**解决**：改动平台文件后 touch 或 Rebuild 强制全量重编译核对警告；nullable API 返回处补 `!`。

**教训**：验收「0 新增警告」要确认编译是全量还是增量；顺手清理同一文件的历史遗留警告（质量护栏允许，避免下次混淆）。

## 2. Android 构建报 XAFLT7024/XAPRAS7024 多为文件锁——先查环境再改代码

**场景**：`dotnet build -f net10.0-android` 报 `XAFLT7024: 文件被"xxx"锁定` / `XAPRAS7024`，错误信息指向 bin 下 dll 被占用；重试或重启系统后成功。

**原因**：360 安全软件实时防护 / 残留 dotnet/msbuild 测试进程锁住 `bin/Debug` 下程序集；另注意完整首次构建可能很慢（实测 19 分钟 vs 增量 13 秒），不是卡死。

**教训**：Android 构建失败先看错误信息里有没有"used by another process"/"被锁定"——是环境问题就重试/重启，不是代码问题；后台构建任务长时间无输出先查锁，别干等。

## 3. 游戏无删档/建目录功能时，用「删 sav 文件夹让游戏重建」覆盖建目录路径

**场景**：真机验证 O1 建目录失效路径，但目标游戏没有删除存档/新建目录功能。

**解决**：完全退出 app → 文件管理器删除 sav 文件夹 → 重启 → 游戏自动重建 sav（`EnsureRealDirectoryUri`）→ 验证「重建后立即可枚举/可写」。关键：必须在 app 退出状态删——进程内缓存对进程外修改无感知（TTL 盲区）。

**教训**：真机验证缺路径时，用「破坏性重建」制造目标场景（确认无重要数据）；验证建目录/删目录路径靠日志时间线（重建后 EnumerateUri 立即可见 + 写档后重枚举即见新档）。

## 4. 两轴 code-review（Standards + Spec）抓单线程审查漏掉的 P0

**场景**：O1/O4 提交前跑 code-review skill：Standards 轴（仓库标准 + Fowler 坏味基线）与 Spec 轴（对照已批准计划）并行子代理，各不污染上下文。

**结果**：Spec 轴对照计划逐行比对抓到「EnsureRealDirectoryUri 失效 key 错位」（off-by-one）+「DisplayNameForMatch null Name NRE」（实现偏离计划伪码，原实现有 null 保护）；Standards 轴抓到 DirectoryExists 快速路径单级匹配与 FindChildDocument 两级回退不一致。

**教训**：实施后对照「已批准计划」逐行核对（Spec 轴）比泛泛的代码审查更能抓"实现偏离 spec"类 bug；两轴并行避免单轴掩盖。审查发现的问题按严重度分级：正确性 bug 必修（P0），风格类判断性项按用户决定。

## 5. 写"已存在文件"被拒、新建 OK 的怪症——安全软件防篡改/残留句柄，先查环境

**场景**：publish 复制 Core.dll 到输出目录反复 `Access denied`；连 `.gitignore` append 都 `Permission denied`，但 touch 新建文件 OK、C 盘 OK、icacls 权限正常。

**定位**：先查残留进程（MSBuild 驻留节点 `dotnet build-server shutdown`；后台起的 server 进程锁 exe 导致 MSB3027——**起的 server 必须记得杀**），再查安全软件（`ZhuDongFangYu.exe`=360 主防；"改已有文件被拦、新建放行"正是防篡改/勒索防护特征）。用户确认 360 关闭后，锁释放恢复正常。

**教训**：
- "写已有文件被拒 + 新建正常 + 非只读 + ACL 正常"的排障顺序：残留句柄 → 安全软件防护 → 文件系统层；不要一上来改代码。
- Git Bash 的 `rm` 会被 safe-delete 包装拦截（通配符/相对路径失败）、`cmd //c`/`taskkill //PID` 参数被 MSYS 转义破坏——这类环境操作改用 **PowerShell `Stop-Process`/`Remove-Item` 或 Python `os.remove`** 绕开，别在 shell 转义上耗时间。

## 6. 行号基线的测试在源码行号偏移时会误报——SafIoPolicyTests 与"加一行 using"

**场景**：3.3 JSON 迁移给 `JSONConfig.cs` 加了一行 `using MinorShift.Emuera.GameView;`，`SafIoPolicyTests.Shared_runtime_direct_IO_does_not_exceed_documented_SAF_baseline` 立即报 8 处 "outside the explicit baseline"。

**原因**：该测试用 (文件, 行号) → 直接 IO 调用数 的**显式基线**扫描 Shared/** 源码；行号整体 +1 后旧基线全部错位，误报"新增直接 IO"（实际零变化）。

**教训**：这类"行号锚定基线"的测试对源码行号漂移脆弱——改 Shared/**（尤其加/删行）后要同步更新基线行号；改完跑一次该测试确认只有"行号平移"而无真实 IO 变化。反过来它也是称职的守卫：真实新增直接 IO 会被立刻抓到。

> **二次实例（2026-08-10）**：给 `PluginManager.LoadPlugins` 加 NativeAOT 豁免（4 行注释 + 2 行 attribute）后，同一测试报 3 处 "outside the explicit baseline"（白名单 281/287/288 实际已漂到 288/294/295）。**任何在 Shared/** 插入行数的改动（即使纯注释/attribute）都会触发**——改完先跑这个测试，按实际行号更新白名单即可（本次同时确认豁免未引入新 IO）。

## 7. 残留进程干扰回归矩阵——run_all.py 内置 xUnit 与单独跑不一致

**场景**：NativeAOT 产物跑 `run_all.py`：14/14 e2e 全绿但内置 xUnit FAIL；单独 `dotnet test` 却 685 全绿。

**原因**：此前手动起的 AOT server 进程残留，锁住 `agent.log`/`test_game` 等共享资源（日志见过 `[agent-log] file init failed: ... being used by another process`），xUnit 里依赖文件系统的测试被波及。

**教训**：跑全量回归前先清残留进程（`Get-Process -Name "Emuera.Headless.Cli" | Stop-Process`）；"套件内 FAIL、单独跑绿"优先怀疑环境干扰（残留进程/文件锁），而不是代码回归——但也别轻易归因环境，先看失败测试的报错内容确认。

## 8. 操纵全局静态的测试必须进 DisableParallelization 集合;高频日志调用会放大竞态窗口

**场景**：T-027 Phase 4 日志统一收官后，完整套件下 `AgentLogTests.Configure_false_disables_then_runtime_toggle` 间歇失败（2/3 复现），单独跑/组合跑全绿。

**根因**：AgentLogTests 操纵进程级全局 `AppDataPaths.Directory` + `AgentLog.Instance` 静态 Lazy 单例；xUnit 默认类级并行下，`SafStage2StorageTests.AppDataScope` 会全局切换 AppDataPaths，把 AgentLog 的 writer 落点/FilePath 切到别的目录，断言读错文件。Phase 4 新增的 EraStreamReader/Preload `EmueraLog.Debug` 调用把 `AgentLog.Instance` 访问频率抬高（几乎所有读脚本文件的测试都会触发），竞争窗口从"几乎不可见"放大到"2/3 复现"。

**教训**：
1. 凡是操纵进程级全局状态（静态单例、全局目录、静态配置）的测试，一开始就应放入 `[CollectionDefinition(..., DisableParallelization = true)]` 串行集合（仓库先例：`GamePathsIsolated`）——不要指望"现在全绿"就安全，高频调用点一旦增多竞态就会显形。
2. 排查间歇失败：先跑"单独（绿）+ 与嫌疑类组合（绿）+ 完整套件（红）"三段定位，再用全局状态切分推断竞态对象；修复后完整套件连跑 2 次验证稳定（1 次绿不够，之前就是 1/3 绿）。
3. 静态门面（EmueraLog→AgentLog）让"测试进程里谁都会写日志"成为常态——新增高频调用点时要评估对全局状态测试的影响面。

## 9. 日志门面改默认阈值/输出目标，必须同步检查依赖终端输出的测试

**场景**：全量回归 2 项稳定失败（`test_cli_clear`/`test_cli_setbg`）：断言"未识别到终端路径标志（日志缺失，视为回归）"——从 CLI 捕获输出里找不到 `[headless] 终端路径: VT`。CLI 进程正常、游戏文本正常、14 个同类测试全过；重建 Debug exe 后仍失败。

**根因**：`AgentCliProtocol.RunCliLoop` 的"终端路径: VT"是 `EmueraLog.Info` 输出；T-027 引入日志门面后 `TerminalSink`（stderr）**默认阈值 Warn**——Info 级诊断**不进终端**（设计意图：交互式 CLI 只显示警告/错误，不污染画面）。测试按旧行为从终端输出找 Info 标志 → 永远找不到。这是 T-027 产品行为变更与测试断言不同步的遗留问题，与 NativeAOT 改动无关。

**修复**：测试 spawn CLI 时显式设环境变量 `EMUERA_LOG_TERMINAL=info`（env 本就是 EmueraLog 的调节旋钮，不改产品行为）；测试注释说明原因。

**教训**：
1. **日志门面改默认阈值/输出目标（Sink 拆分、级别阈值、stdout→stderr）后，必须 grep 依赖终端输出的测试**（找日志标志、找 stderr 文本的断言）——产品日志改动与测试断言是同一契约的两端。
2. 先确认日志"被阈值挡了"而非"没打"：查日志门面的默认阈值与 Sink 目标（`EmueraLog.cs` 的 TerminalLevel/Write），再决定是调测试 env 还是改产品语义。调测试 env（`EMUERA_LOG_TERMINAL`）是设计支持的用法，优于把 Info 改成 Warn 污染日志语义。
3. 这类失败的特征：与改动文件无交集 + 稳定复现 + 进程正常——优先怀疑"产品行为早已变、测试没跟上"，而不是本次改动的回归。

## 10. Python print 双重编码在 GBK 控制台显示乱码——显示乱码 ≠ 断言失败

**场景**：`run_all.py` 输出里所有中文 section 标题变 `=== Issue 001: ���� tracer... ===`，但测试结果（26 passed）不受影响。

**根因**：`_safe_print_line` 的 `print(line.encode(sys.stdout.encoding, errors="replace").decode(sys.stdout.encoding, errors="replace"))` 双重编码——PowerShell 控制台代码页 GBK(936) 下 `errors="replace"` 把编码不了的字符替换成 `�`（U+FFFD）。Windows 控制台 Python 走 **WinConsoleIO（UTF-16 直接写，不经代码页）**，`print(line)` 本身就能正确显示任意 Unicode——双重编码是冗余且有害的。

**修复**：`_safe_print_line` 直接 `print(line)`（注释说明；被重定向到管道/文件时 print 用 stdout.encoding，UTF-8/gbk 均支持中文，同样安全）。

**教训**：
1. **Windows 控制台 Python 输出中文：直接 `print(str)`**——WinConsoleIO 绕开代码页；任何 `encode(errors="replace")` 中间层都是把"显示问题"变成"字符损毁"的元凶。
2. **显示乱码 ≠ 断言失败**：终端显示编码问题不影响 Python 内部 Unicode 比对（诊断学：先确认是显示层还是数据层，别把乱码当测试失败去查代码）。

## 12. Windows 全绿 ≠ 跨平台正确——大小写/分隔符缺陷需要真敏感环境（2026-09-12）

**场景**：issues/15 把 Android 游戏切到「真实路径 + `FileSystemGameDirAccessor`」（legacy）后，eraTK（TK1.29.3）能正常加载，但点「重新開始」报 `CFLAG` 第 1 引数 -1（`SYSTEM\NEWGAME.ERB:283`）。同一份改动在 Windows 上 `dotnet test EraCore.Tests` **1065/1065 全绿**，既有 `CsvLookupTests` 也全过。

**结果**：Windows 与真机结论完全相反。真机 legacy 下角色 CSV 枚举到 0 个（`[ErhLoader] PrepareERDFileNames: count=19`，SAF 为 20）→ 角色模板为空 → `EXISTCSV()` 全 0 → 新游戏一个角色都不加 → `NAME_TO_CHARA("アリス") = -1`。

**原因**（两条独立的「测试盲区」叠加）：
1. **环境盲区**：缺陷是「托管 glob 在 Unix 上大小写敏感」（`Directory.GetFiles(dir, "CHARA*.CSV")` 匹配不到磁盘真名 `Chara1 霊夢.csv`）。Windows 的文件系统与 .NET 托管 matcher 都大小写不敏感，本地**根本不具备复现条件**。同类盲区还有路径分隔符：`\` 在 Windows 是分隔符、在 Android 不是。
2. **断言盲区**：既有夹具把 CSV 命名成 `CHARA001.CSV`（与引擎 pattern 大小写**完全吻合**），且只验证「能加载」。而加载期不碰角色模板，模板只在运行期（新游戏）被索引——弱断言让缺陷从两条路径同时溜过。

**解决**：
- **真敏感环境 = WSL2 Ubuntu**（本机已装；SDK 用户级装在 `~/.dotnet`，无需 root）。测试夹具用 `Path.GetTempPath()` → Linux 的 `/tmp` 是 **ext4，真大小写敏感**；仓库放在 `/mnt/d`（drvfs 默认不敏感）也不影响。
  ```bash
  wsl -d Ubuntu -- bash -lc "cd /mnt/d/LaoBro/era-core && ~/.dotnet/dotnet test EraCore.Tests/EraCore.Tests.csproj"
  ```
  ⚠️ 命令里**不要出现 `$`**——外层 Git Bash 会先展开 `$PATH`，撞上 `Program Files (x86)` 的括号直接语法错；用绝对路径（`/home/<user>/.dotnet/dotnet`）。
- **分层守卫**（新增 `FileSystemGameDirAccessorPatternTests`）：跨平台主守卫是「accessor 结果 == `Wildcard` 过滤结果」；大小写用例交 WSL；另加一个 **Windows 本地也能红的哨兵**——`Directory.GetFiles(dir, "*.")` 会返回无扩展名文件，而 `Wildcard.Matches(name, "*.")` 为 false。
- **守卫自证「改前红」**：`git stash push -- <生产文件>` → 跑守卫 → 期望红 → `git stash pop`（本次 Windows 红 3 例、WSL 红 6 例）。

**教训**：
1. **`dotnet test` 全绿只证明当前平台正确**。凡触碰「文件名大小写 / 通配 pattern / 路径分隔符 / 相对路径重建」的改动，必须过真大小写敏感环境（WSL2 ext4）——这是 `saf-file-access.md` #12「加载通了不等于可玩」的**测试版**同类教训。
2. **夹具命名要与生产真实形态同形**（大小写、扩展名、空格/非 ASCII）。`CHARA001.CSV` 这种「顺手写成大写」的夹具会把平台缺陷永久藏住；真机是 `Chara1 霊夢.csv`。
3. **「只测能加载」是弱断言**：启动/加载成功不代表数据正确，要选一个**真正消费该数据**的运行期入口（新游戏、剧情推进）做验收。
4. **WSL 下 `EraCore.Tests` 永远不是全绿**：基线固定 5 个既有 Linux 不兼容/枚举顺序失败（`SafPathTests.Filesystem_paths_still_use_Path_semantics`、`AssetPathValidatorTests.Resolve_rejects_backslash_traversal`、`ErbLoaderBoundedSchedulerTests` ×2、`ErbLoaderTests.LoadErbDir_parallel_progress_lines_follow_file_order`）+ **时序/并发类偶发 flaky**（全量跑时超时，单跑稳定通过；已观察到 `ControllerTests.Lease_expiry_releases_agent_and_notifies_waiter` 与 `SessionWaitForTurnTests.Steal_racing_a_queued_turn_leaves_that_turn_for_acquire_drain`）。判读口径 = 只有**不在基线名单里**、且**单跑也不通过**的失败才算回归。
5. **Windows 每目录大小写敏感（`fsutil file setCaseSensitiveInfo`）不可作替代**：需管理员；且 .NET 10 没有按目录探测大小的实现（`PathInternal` 无 `IsCaseSensitive`、框架 DLL 无 `FileCaseSensitiveInfo`）→ 托管 glob 不受该标志影响，它只改 OS 层精确路径解析。
6. **触发清单**（命中任一即需跑 WSL）：`FileSystemGameDirAccessor` / `SafGameDirAccessor` / `IGameDirAccessor`、`Config.GetFiles` 或任何 `GetFiles(..., searchPattern, ...)`、`SafPath`、`Wildcard` / `Preload` 树枚举。

## 13. publish.ps1 在 Windows PowerShell 5.1 下解析失败——UTF-8 无 BOM 中文注释被按 GBK 读

**场景**：跑 `powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 maui-android-aot` 重建 APK，脚本还没执行任何逻辑就报一串 ParserError（`Unexpected token`、`The string is missing the terminator`），报错里的中文全是乱码（`鏈湪`、`锛堢┖锛?`）。

**原因**：`publish.ps1` 是 **UTF-8 无 BOM**（首三字节 `60 35 13`，非 `EF BB BF`）。Windows PowerShell 5.1（`powershell.exe`，Windows 内置）读无 BOM 脚本一律按系统 ANSI（中文系统 = GBK）解码，中文注释/字符串被拆成非法 token → 整个脚本解析失败。PowerShell 7+（`pwsh`）默认 UTF-8，不受影响。

**解决**：
- 临时绕行：不跑脚本，直接执行它内部封装的 `dotnet publish` 命令（本次即如此）。
- 根治（二选一）：给脚本加 UTF-8 BOM；或约定用 `pwsh` 运行。仓库里其它含中文的 `.ps1` 同理——**新增/编辑带中文的 PowerShell 脚本时，保存为 UTF-8 with BOM**。

**教训**：
1. **ParserError + 报错文本乱码 = 编码问题，不是语法问题**：别去修"语法"，先查文件编码（`ReadAllBytes` 看首三字节是否 `EF BB BF`）。
2. Windows PowerShell 5.1 与 PowerShell 7 对无 BOM 文件的默认编码不同（GBK vs UTF-8）——同一脚本"在 pwsh 里能跑、在 powershell 里炸"就是这个差异；仓库脚本要按最保守的 5.1 兼容（带 BOM）。
