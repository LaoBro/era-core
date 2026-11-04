# 从零实现一个新前端

> 第一次接手时读这篇。它讲「先选哪条路、怎么跑起第一帧、哪里最容易错」，
> 不讲字段级规格。字段看 [turn-protocol.md](turn-protocol.md)，
> 套件与清单看 [host-conformance.md](host-conformance.md)。

你大概会用 C# 或 TypeScript，但没读过 era-core 源码。全文出现的专有名词第一次出现时会解释。

## 1. 先做选择：三种接入方式

| 方式 | 适合谁 | 拿到什么 | 代价 |
|---|---|---|---|
| **进程内 C# 前端**（引用 `EraCore.Hosting`） | 桌面/移动原生应用，想直接控制窗口、字体与输入 | 强类型对象：`DisplaySnapshot`、`TurnRecord`、`AwaitInfo`，没有网络与序列化 | 与 .NET 绑定；渲染、字体测量要自己做 |
| **浏览器前端**（HTTP + WebSocket） | Web、Electron、远程界面，或非 C# 技术栈 | JSON 帧与 `wire` 字符串，语言无关 | 要处理连接、重连、晚加入与版本协商 |
| **终端前端**（VT/ANSI） | 命令行、CI、远程 SSH 场景 | 终端字符流与鼠标事件 | 表现力最受限：图片/颜色/布局都要降级 |

一句话结论：**进程内最省事，wire 最通用，终端最受限**。不确定时先用进程内方式验证语义，再考虑换 wire。

三种方式的起手位置：

- 进程内：引用 `EraCore.Hosting`，按本文第 2 节直接构造 `GameHost`。
- 浏览器：用 `--server --port 8080` 起服务，先 `GET /snapshot`，再连 `/ws` 收增量；也可以用 `GET /turn` 长轮询，上行发 `POST /input`。
- 终端：同一个 CLI 加 `--protocol cli`，VT/ANSI 输出与鼠标事件由宿主自己处理。

## 2. 最小可用路径（以进程内 C# 为例）

每一步结尾有一句「做到这一步应该能看到什么」，可以拿来自查。

### 第一步：准备游戏目录

引擎不直接碰 `File.*`，而是通过 `IGameDirAccessor` 读游戏文件。你要按目录形态选实现：

- **普通本地路径**（Windows/macOS/Linux、Android 的真实路径）→ 用 `FileSystemGameDirAccessor`。
- **Android 真实路径模式**（已获 `MANAGE_EXTERNAL_STORAGE`）→ 仍然是普通路径，同样用 `FileSystemGameDirAccessor`，游戏目录零拷贝直读。
- **Android SAF**（只有 `content://` URI）→ 需要自己实现 `IGameDirAccessor`；仓库里 MAUI 有一份 internal 实现可参考。

> 自查：`DirectoryExists(gameRoot)` 为真，且 `csv/GameBase.csv` 能读到。

### 第二步：起会话

`CoreSession` 是引擎的公共组装面；`GameHost` 是它的薄包装，只暴露生命周期、元信息、快照/增量和输入。**新项目建议用 `GameHost`**：它把「先订阅再取快照」这类顺序封装好了，也避免你不小心依赖 Core 的内部类型。

```csharp
var options = new CoreSessionOptions(gameRoot, new FileSystemGameDirAccessor());
await using var host = new GameHost(options);
var started = await host.StartAsync();
if (!started.Succeeded) { /* 展示 started.ErrorCode 与 ErrorMessage */ }
```

> 自查：`host.State` 走到 `Running` 或 `WaitingForInput`，`host.Metrics` 可读（字号、行高、窗口尺寸、默认前景色）。

### 第三步：读第一帧

两个入口，别混用：

- `host.Snapshot`：当前全量画面。适合「每隔一会儿看一眼」的简单场景。
- `host.OpenStream()`：一次调用同时给你 `InitialSnapshot` 和后续增量 `Turns`。它保证「先订阅、后取快照」，中间不会漏帧。

有增量就用增量；只有轮询时，直接用 `Snapshot` 也能工作，但会丢掉同一画面版本内的中间状态。

```csharp
var stream = host.OpenStream();
var baseline = stream.InitialSnapshot;
await foreach (var turn in stream.Turns!.ReadAllAsync())
{
    if (turn.Record.revision <= baseline.revision && turn.Record.awaiting.ended is null) continue;
    Apply(turn.Record);
}
```

> 自查：第一帧的行数和文本与游戏开场一致，且 `baseline.revision` 是个非零数字。

### 第四步：画出来

一帧的画面是「行 → 条目 → 片段」三层：`snapshot.lines` 是行，每行 `entries` 是逻辑条目，每个条目 `segments` 是同样式的一段文本、一个图片或一个形状。按钮挂在条目上，不在片段上。背景图不在行里，单独放在 `snapshot.bgImages`。

```text
foreach line in snapshot.lines:
    offset = AlignGeometry.OffsetColumns(line.align, 行内容宽度, 容器列数)
    foreach entry in line.entries:
        foreach seg in entry.segments:
            有 seg.image → 按 image.width/height 绘制，占位宽度就是 image.width
            有 seg.shape → 按 x/y/width/height 填充，占位宽度是 x + width
            否则按 seg.color/bold/italic/underline/strikeout/fontname 画 seg.text
        if entry.button != null:
            把 entry.button.col + offset 与 entry.button.width 记成可点击区间
```

六个要点：

- `align` 只决定整行的前导偏移；按钮的 `col` 是相对内容起点的列，命中时要加偏移。
- 片段样式字段是「缺省即不加该样式」，不要把手动 `false` 当成特殊含义。
- `image.src` 可能是根相对路径（如 `img/x.png`），也可能是 sprite 名（如 `FACE_1`，映射在 `resources/*.csv`）；两种都要能解析。
- 图片/形状的占位宽度必须与绘制宽度同源：图片占 `image.width`，形状占 `x + width`。当成 0 宽会让整行在加图后跳动。
- `image`/`shape` 有值时忽略同片段的 `text`：`text` 只是 CLI 调试标记（缺图时 Protocol 会带 AltText）。两者都画会把占位框推到文字右侧，缺图占位就看不见。
- 带 `crop` 的图片按「裁剪矩形 + 整图负偏移」绘制（等价于 overflow 容器 + 负 margin）。
- 背景图按 `depth` 升序绘制（小的在底层），`opacity` 是 0–1 的透明度，位置固定不随行滚动。缩放策略要对齐 Emuera `BakeBackground`：等比 cover（`max(视口宽/图宽, 视口高/图高)`），水平居中、顶部对齐、超出裁切，不做非等比拉伸。**以 WinForms 为语义权威**：Native 与 Web（`.term-bg-img` 用 `width/height:100%` + `object-fit:cover` + `object-position:center top`）均已按 cover 实现，三端口径一致。Web 的 cover 基准是游戏窗口列宽（`effectiveWindowWidth × scale`），屏幕更宽时右侧留白是刻意设计、不是缺陷；Native 以整块画布为基准，属实现差异。
- `image.srcm` 是「点击图片取点击处像素色」的映射图：点击坐标按 `<img>` 元素矩形（含 `crop` 负偏移、`ypos`、缩放）反算显示图自然像素，再读 `srcm` 同坐标像素，`0xRRGGBB` 以十进制提交。**已知跨前端差异**：Web `readPixel` 忽略 alpha（透明像素照样返回 RGB），Native 有意收紧——透明像素拒绝提交并记日志；这是取舍不是 bug。
- 行高、字号、默认前景色从 `host.Metrics` 读，不要写死。

> 自查：文字、颜色、粗体/斜体、居中行都出现在正确位置；按钮能看出边界；含图片的行宽度不变、背景层按 depth 叠在内容后面。

### 第五步：接受输入

`AwaitingView` 把服务端「在等什么」翻译成界面状态。用 `AwaitingView.From(snapshot.awaiting, waitStartedAt)` 得到模式与倒计时。

- **按钮**：`awaiting` 无文本输入、只有按钮时，提交 `button.value`；提交前用 `CanSubmitButton(button, generation)` 检查代数。
- **文本/数字**：`input.type` 为 `IntValue`/`StrValue`/`AnyValue` 时显示输入框；`Submit(new InputFrame(Value: 文本))`。
- **任意键/回车**：`AnyKey`/`EnterKey` 不提交按钮值，而是提交空字符串：`Submit(new InputFrame(Value: ""))`。
- **倒计时**：`wait.durationMs > 0` 才计时；`durationMs == 0` 是让路点，不要画倒计时。超时文案只在 `timedOut` 帧带。

> 自查：点按钮能推进；输入文本能回显；按任意键能继续；倒计时从正确起点开始走。

### 第六步：跑一致性套件

套件用一组手写 JSON 夹具检查你的实现是否读懂协议。你实现 `IHostUnderTest`，然后：

```csharp
var report = await ConformanceRunner.RunAsync(new YourHost());
Console.WriteLine(report.ToTextTable());
```

先看 `Failed`，再看 `DeclaredButUnverified`。**不要为了让报告变绿而删掉 manifest 里的能力声明**，那只会把失败变成跳过。

> 自查：必需夹具全部 `Passed`；剩下的 `DeclaredButUnverified` 你能逐条说清「为什么还没验证」。

## 3. 必须理解的五条规则

1. **`revision` 是画面修订号，`generation` 是按钮代号。**
   `revision` 每次画面重建递增，等长行替换也会变；`generation` 只在按钮结构变化时递增。
   *漏了会怎样：* 用 `generation` 判断画面是否变化，等长替换会一直不刷新。

2. **先订阅，再取快照；丢弃 `revision <= snapshot.revision` 的帧。**
   这是「晚加入」的正确姿势：订阅与取快照之间产生的帧可能比快照新。
   *漏了会怎样：* 画面会先倒退再前进，或者一直停在一张旧画面上。

3. **终态帧（`awaiting.ended`）不受去重约束，必须应用。**
   终态包括 `Quit` 与致命错误 `Error`；它们可能带着不比快照新的 `revision`。
   *漏了会怎样：* 游戏已经结束或崩溃，你的界面还在等输入。

4. **未知 `awaiting` 键不要猜，回退到快照并记录 warning。**
   协议会演进，旧前端遇到新键时，正确做法是重新取一次全量画面，而不是静默忽略。
   *漏了会怎样：* 新版本服务端一上线，界面停在错误的输入模式，用户点不动。

5. **陈旧按钮（`isLive=false`）不得提交输入。**
   按钮几何可能还在屏幕上，但它属于上一代画面；提交会被拒绝或推进到错误分支。
   *漏了会怎样：* 用户点了看起来还在的按钮，游戏没反应或跳错。

## 4. 常见坑

| 症状 | 原因 | 做法 |
|---|---|---|
| 等长行替换后画面不刷新 | 只比较行数或 `generation` | 用 `revision` 判断是否需要重绘 |
| 比例字体下按钮点偏 | 用「列宽 × 字号」推算像素位置 | 用实际文本测量结果定位按钮区间 |
| 含内联图片的整行错位 | 图片段被当成 0 宽（不支持 `Images` 时直接跳过） | 不支持绘制也要按 `image.width` 补空白占位；形状占 `x + width` |
| 立绘/CG 渲染后整片空白且无任何提示 | 图片路径解析失败或解码失败被静默吞掉 | 失败要有可观测后果：占位框或告警日志，不能静默丢图 |
| 历史回看越翻越错 | 漏了 `shift_head` / `clear_screen` 的滚动补偿 | 先应用行操作，再按可见窗口重算滚动位置 |
| 终态后界面卡死 | 仍在等输入，忽略 `awaiting.ended` | 终态到达即切换为结束界面并停止输入 |

这些都是接入时真实出现过的现象，不是假设。

## 5. 一致性套件怎么用

实现 `IHostUnderTest` 时，可以按六组职责回读（一共十个小成员）：

| 职责 | 回读什么 | 常见错误 |
|---|---|---|
| `HostName` / `Declared` | 自报名字与 manifest（版本、能力位、语义项） | 声明了没实现的能力，失败被伪装成「已支持」 |
| `LoadSnapshotAsync` | 把全量快照变成自己的行/awaiting 状态，并清空旧告警 | 忘了重置 `LastAppliedRevision` 与 `Warnings` |
| `ApplyTurnAsync` | 按 `lineOps` 顺序应用增量，先做 revision 去重，终态例外 | 终态也被去重丢掉 |
| `RenderedLines` / `Awaiting` / `LastAppliedRevision` | 当前行投影、输入状态、已应用的 revision | 文本拼接少片段，或 awaiting 没有投影 |
| `Warnings` | 结构化告警（`Code` + `Detail`），未知 `awaiting` 键必须有 | 只返回空数组，未知键被静默吞掉 |
| `HitTest` / `CapturePngAsync` | 字符列命中按钮；截图可选，没有就返回 `null` | 命中忘了加对齐偏移，或忘了 generation 过期判断 |

报告里的四种状态：

- **`Passed`**：声明的能力有夹具覆盖，且断言通过。
- **`Failed`**：真错。消息里会写清夹具、期望值与实际值。
- **`Skipped`**：你没声明这项能力，套件不跑它。**跳过不等于通过**。
- **`DeclaredButUnverified`**：你声明了，但当前没有夹具能验证。它提示「这项自报需要你自己补证据」。

套件**不能**证明字体细节、动画流畅度、输入法行为、像素级观感是正确的——这些没有机械判据。完整边界见 [host-conformance.md](host-conformance.md#5-边界与未覆盖项)。

## 6. 检查清单

- [ ] 首帧的行、对齐、样式、按钮位置与游戏开场一致。
- [ ] 用 `revision` 去重，且等长替换能触发重绘。
- [ ] `awaiting.ended` 无条件应用，终态后停止输入。
- [ ] 未知 `awaiting` 键回退快照，并留下结构化 warning。
- [ ] 按钮命中校验 `generation`，旧代按钮不提交。
- [ ] `shift_head` 与 `clear_screen` 后滚动位置正确。
- [ ] 图片按 `image.width` 占位并绘制，形状按 `x + width` 占位；背景按 `depth` 升序、`opacity` 透明绘制，并按 cover 等比缩放（不留拉伸变形）。
- [ ] 含 `srcm` 的图片点击能按像素取色提交，未解码/越界/透明有区分日志。
- [ ] 图片解码失败或资源缺失有占位/日志，不静默丢图。
- [ ] 文本、数字、任意键/回车、按钮四类输入都能推进。
- [ ] 倒计时只在 `durationMs > 0` 且 `displayTime` 为真时显示。
- [ ] manifest 只声明真实实现的能力。
- [ ] 一致性套件必需夹具全绿，`DeclaredButUnverified` 逐条有解释。

做到这里，你有一个能跑、能输入、能被套件检查前端。剩下的是平台细节：字体、布局、动画与输入法。

## 7. 术语速查

| 术语 | 一句话解释 |
|---|---|
| `Snapshot` | 当前全量画面：所有行、`bgImages` 背景层、awaiting、`revision`。 |
| `bgImages` | 背景图层：`src` + `depth`（小的在底层）+ `opacity`（0–1），固定不随行滚动。 |
| `diff` / `lineOps` | 相对上一帧的行变化，按数组顺序应用。 |
| `revision` | 画面修订号；每次重建递增，用来判断「这帧是否比我的画面新」。 |
| `generation` | 按钮代号；按钮结构变化时递增，用来拒绝旧代按钮。 |
| `awaiting` | 服务端此刻在等什么：输入、定时等待或终态。 |
| `terminal` | `awaiting.ended` 里的结束原因，目前是 `Quit` 或 `Error`。 |
| `wire` | 已经序列化好的帧字符串；转发型前端直接用它，不重新序列化。 |
| `manifest` | 前端自报的版本、能力位与语义项清单，套件据此决定跑或跳过。 |
