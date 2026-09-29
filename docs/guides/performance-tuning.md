# 性能与加载开关

本文汇总引擎启动性能相关的加载开关、实测数据，以及与第三方实现的对照。

游戏目录下的 `emuera.config`（格式 `名前:値`，支持日文/英文双名）控制引擎启动性能。

两项加载优化项目（ERB 文件级并行加载 + 未调用函数延迟解析）合计贡献 **eraTW 活堆 -583.9 MiB / 冷启 Init -1.1s**，对应三个正交的开关，**默认全部 `true` 开启**；AnalysisMode 下一律强制串行/eager。

关闭顺序（排查语义偏差时，逐层回退到原始串行形态）：`LazyFunctionLoad:NO` → `ParallelParseScript:NO` → `ParallelErbLoad:NO`。

| 开关（日文键名） | 英文键名（等价） | 默认值 | 功能 | 设为 NO 时的效果 |
|---|---|---|---|---|
| ERBファイルを並列して読み込む | Load ERB files in parallel | YES | ERB **文件级并行加载**（分词/解析按文件粒度并发，跨文件警告序按文件名回放到串行等价） | 退化为单线程逐文件加载；等价性与加载期警告逐字节一致 |
| 未呼び出し関数の解析を並列して行う | Parse uncalled functions in parallel | YES | ParseScript 三步（setArgument / nestCheck / setJumpTo）对**未调用函数集**并行遍历；主循环 BFS 按层仍保持串行 | 三步退化为单线程串行遍历；警告序不变、语义等价 |
| 未呼び出し関数の本体解析を遅延する | Defer uncalled function body parsing | YES | 加载期**不为未调用函数做 ParseScript**，只注册标签桩 + 行号引用；**首调用时单函数重解析物化**（幂等：二次调用直接命中已物化链） | 回退为加载期 eager 全量 ParseScript；与 `ParallelErbLoad:NO` + `ParallelParseScript:NO` 组合即为原始串行 eager 形态 |

---

## 关联警告与忽略开关

与上表三项正交，但常联动使用：

| 开关（日文键名） | 英文键名（等价） | 默认值 | 取值/说明 |
|---|---|---|---|
| 呼び出されなかった関数を無視する | Ignore uncalled functions | YES | 语义上忽略未被任何路径调用到的函数（加载期仍写入 LabelDictionary 保证跳转解析，只是 ParseScript 不再对其执行语义检查）。与 `LazyFunctionLoad` 正交——后者只**延迟**解析，本开关决定**是否**解析。 |
| 関数が見つからない警告の扱い | Function is not found warning | IGNORE | `IGNORE` / `LATER` / `WARN` / `DISPLAY`。`LazyFunctionLoad:YES` 下，未定义函数的报错仍在加载期跳转解析阶段即刻抛出（不延迟到物化）。 |
| 関数が呼び出されなかった警告の扱い | Function not called warning | IGNORE | `IGNORE` / `LATER` / `WARN` / `DISPLAY`。警告发射时机：加载期 eager 全量 ParseScript 时即刻发射；`LazyFunctionLoad:YES` 下按 `Depth==-1` 深差集零解析导出，**与 eager 同集但可能延迟**（定义性行为，不断言逐字节相等）。 |

---

## 实测参考

eraTW Release CLI，本机 2026-08-24，3 轮稳态后取 2 轮：

| 项 | 三开关全关（原始串行 eager） | 三开关全开（出厂默认） | Δ |
|---|---|---|---|
| 活堆（`dumpheap -stat -live`） | 701.7 MiB | **117.8 MiB** | **-583.9 MiB** |
| 冷启 Init:End（Stopwatch 段） | 3449ms | **2315ms** | **-1134ms** |
| 冷启 Proc:Init:ERB 段 | 2647ms | **1554ms** | **-1093ms** |

验收门禁：Init ≤ 3.0s / 活堆 ≤ 350 MiB（lazy 形态下分别为 2.3s / 117.8 MiB，均通过）。

**CI 不加入性能/内存断言**：跨机硬件差异大，以上数值仅作本机复测判读基准。

---

## 与第三方实现的对照

### Android 启动速度

同一台真机，游戏 eratohoK 1.29.3：

| 实现 | 耗时 | 说明 |
|---|---|---|
| uEmuera | 30.57s | 打开游戏 |
| EraCore | **1.35s** | 快速轨（真实路径直通 accessor）+ IR 缓存命中轮 |

- IR 缓存是**本项目新增能力**（首次扫描时建立，之后每次启动复用），uEmuera 没有对应机制——该对比反映的是产品能力差异。
- 1.35s 为无头控制面 `/load-game` 的命中轮实测值（真机 n=3，两组分布不重叠）。同一游戏在同一台机器上走 SAF 兼容轨时为 1.60s（`genmerge` 阶段由 780~841ms 降至 48ms）。
- 快速轨需要「所有文件访问权限」（`MANAGE_EXTERNAL_STORAGE`）；未授权时走 SAF 兼容轨。

### Windows 内存占用

游戏 eratohoK，进入游戏标题界面时的进程内存：

| 实现 | 内存 |
|---|---|
| 原版 Emuera | 1072.5MB |
| EraCore CLI | **155.1MB** |

约降至 1/7。
