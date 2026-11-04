# 原生前端一致性套件（第一版）

> 目标：把「读 `turn-protocol.md` + 读 `EraCore.Native` 才能确认的显示语义」变成可机械对照的
> 清单与夹具。**它降低漏实现的概率，不保证前端正确**——像素观感、字体、输入法、动画流畅度
> 不在套件覆盖内（见文末）。

相关代码：

- `EraCore.Hosting/HostFeatures.cs`——能力枚举、语义目录、`HostManifest`。
- `EraCore.Hosting/HostContractCheck.cs`——版本协商。
- `EraCore.Hosting.Conformance/`——夹具模型、`IHostUnderTest`、执行器与 14 份内嵌 JSON。
- `EraCore.Native/NativeHostUnderTest.cs`——Native 参考适配器。
- `EraCore.Hosting.Tests/ConformanceTests.cs`——参考适配器与三个反例。

## 1. Manifest、能力位与「自称」

`HostManifest` 由四部分组成：

```csharp
record HostManifest(int ApiVersion, int DataVersion,
                    InputCapabilities InputCapabilities, RenderCapability RenderCapability,
                    IReadOnlyList<HostFeature> Features);
```

- `RenderCapability` 是**传输/绘制能力位**（能否画图片、形状、背景图、命名字体、动画帧）。
- `Features` 是**语义清单**：每一项在 `HostFeatureCatalog` 里有一条一句话语义 + `turn-protocol.md`
  条款号 + 实际 DTO 字段名。
- `Has(HostFeature)` 决定夹具跑还是跳过。

两者的关系：`RenderCapability.Images = true` 只说明「画布支持图片」，不等于「`PrintSegment.image`
的降级、`bgImages` 整体替换语义都实现对了」；后者是 `HostFeature.Images` / `BackgroundImages`。
`HostFeatureCatalog.RequiredFor(dataVersion)` 给出该 wire 版本下必须实现的语义项。

> Manifest 是宿主**自称**。套件只验证「声明了的、且有夹具覆盖的」部分。没声明的一律 `Skipped`。

## 2. 接入 `IHostUnderTest`

实现八个成员即可：

1. 把 `ConformanceSnapshot` 载入为「全量基线」，重置增量状态与告警（等同一次晚加入）。
2. 对 `ConformanceTurn` 依次应用 `diff.lineOps`；先做 revision 去重，但 `awaiting.ended`
   出现时**无条件应用**（`turn-protocol.md` §2）。
3. 把行投影成 `RenderedLine`（文本片段 + 对齐 + 按钮），文本拼接可直接用于断言。
4. 由 `awaiting` 计算 `AwaitingUiState`（`Mode`/`InputType`/`RequiresValue`/倒计时/终态）。
5. `LastAppliedRevision` 返回当前已应用的最大 revision。
6. 未知 `awaiting` 键必须产生 `HostWarning { Code = "unknown-awaiting-key" }`，且保守降级为
   不可交互（§2 未知键规则）。
7. `HitTest` 按字符列 + 行返回按钮，`IsLive` 需同时满足「正在等输入」且 generation 与当前帧一致（§5）。
8. `CapturePngAsync` 可选；返回 `null` 表示不参与截图比对。

`ConformanceRunner.RunAsync(host, options)` 返回 `ConformanceReport`，可用
`ToTextTable()` / `ToJson()` 输出。`options.IncludeOptionalFixtures` 控制 `optional=true`
的夹具（本版未收录图片/背景夹具，见 §5 的说明）。

## 3. 14 个夹具与断言

| # | fixture | 一句话断言 |
|---|---|---|
| 1 | `initial_snapshot` | 全量快照的行文本/对齐、文本样式字段、按钮列命中（含 center 对齐偏移）都正确。 |
| 2 | `diff_append` | `append` 的 `newLines` 按序追加到末尾。 |
| 3 | `diff_replace_equal_length` | `clear_line_diff(1) + append(1)` 得到等长替换后的末行。 |
| 4 | `diff_shift_head` | `shift_head(1)` 删除头部一行，并与 `append` 共存。 |
| 5 | `diff_clear_screen` | `clear_screen` 全清后由 `append` 重印。 |
| 6 | `revision_replay` | 落后/相等 revision 的帧被丢弃，更新的帧仍应用。 |
| 7 | `awaiting_input_text` | `IntValue` → Text 必填；`AnyKey`/`EnterKey` 可空但不强制值。 |
| 8 | `awaiting_tinput_wait` | `input + wait{durationMs>0,displayTime}` 两维同现，倒计时开启。 |
| 9 | `awaiting_tinput_timedOut` | 超时续跑帧带 `timedOut`/`timeUpMessage`，倒计时仍按 duration 开启。 |
| 10 | `awaiting_zeroduration` | `wait{durationMs:0}` 零时长让路点：无倒计时、不等待输入。 |
| 11 | `awaiting_terminal_quit` | `ended{terminal:Quit}` 投影为终态，不再等待输入。 |
| 12 | `error_frame_fatal` | revision 落后于快照的 `ended{terminal:Error}` 仍必须应用（显式断言终态存活）。 |
| 13 | `awaiting_unknown_key` | 未知 awaiting 键产生 `unknown-awaiting-key` 告警并保守降级。 |
| 14 | `button_stale_generation` | 旧 generation 按钮几何仍可命中，但 `IsLive=false`。 |

判定规则：

- 宿主 manifest 未声明夹具所需任一能力 → `Skipped`，消息为 `declared-unsupported: ...`。
- 声明了但断言不符 → `Failed`，消息含 fixtureId、失败项、期望与实际。
- manifest 声明了某能力但**没有任何夹具覆盖** → `DeclaredButUnverified`。
  这不是失败，只表示「这项自报没被机械验证」；参考适配器刻意只声明有夹具覆盖的能力。

## 4. 版本协商

`HostContractCheck.Check(hostData, hostApi, data, api)`：

| 结果 | 条件 | 处理 |
|---|---|---|
| `Ok` | data 与 api 都相等 | 正常启动 |
| `HostBehind` | 任一宿主版本严格小于契约版本 | **必须拒绝启动并报告**缺失版本 |
| `HostAhead` | 没有落后、且任一严格大于 | 允许运行；未知 awaiting 键回退快照，未知能力位按不支持 |
| `Unknown` | 任一版本不是正整数 | 无法协商，按不可用处理 |

混合方向（一个超前、一个落后）按落后处理：无法安全降级。

## 5. 边界与未覆盖项

**不可机械验证**（不要写成「已保证」）：

- 字体族回退、字形度量、比例字体下按钮像素位置；
- 动画流畅度、帧率、`AWAIT` 中间帧的合并策略；
- IME / 输入法组合、光标、键盘布局；
- 颜色空间、抗锯齿、缩放后的像素观感；
- 图片解码、`srcm` 像素取色、背景图叠层透明度；
- 剪贴板、缩放等宿主自有交互（协议 v13 无 wire 字段，未进能力目录的机械判定）；
- 无障碍、屏幕阅读器与键盘导航。

本版 14 个夹具全部手写、不依赖 `test_game/` 或引擎；图片/背景夹具因 `IHostUnderTest`
未暴露背景状态面，暂不收录（`HostFeature.Images` / `BackgroundImages` 仍保留在目录中，
宿主声明它们会得到 `DeclaredButUnverified`）。

## 6. 前端接入检查清单（12 条）

1. 先订阅 `/ws`，再取 `/snapshot`；按 `revision` 丢弃过期帧。
2. `awaiting.ended` 出现时无条件应用，不参与 revision 去重。
3. `ended` 与 `input`/`wait` 互斥，且终态不再接受输入。
4. 未知 `awaiting` 键回退 `GET /snapshot`，不静默忽略。
5. `wait.durationMs == 0` 只表示「下一帧立刻到」，不渲染倒计时。
6. `wait.durationMs > 0` 且 `displayTime` 为真才显示倒计时。
7. `timedOut`/`timeUpMessage` 只在超时帧出现，不自行编造默认文案。
8. `input.type` 决定输入控件；`IntValue`/`AnyValue` 不可空提交，`StrValue`/`AnyKey`/`EnterKey` 可空（§3 输入类型表）。
9. 按钮命中同时校验行、字符列与 `generation`；旧代按钮不可提交。
10. `diff.lineOps` 按数组顺序应用；`append` 的 `newLines` 是全量 `DisplayLine`。
11. `bgImages` 是整体替换：省略/null 不变，`[]` 清空。
12. 文本样式字段缺省即「默认/不加该样式」，不要按 `false` 之外的语义解释 null。

## 7. 运行

```bash
dotnet build EraCore.sln -c Debug -m:1
dotnet test EraCore.Hosting.Tests/EraCore.Hosting.Tests.csproj --no-build -m:1
```

`ConformanceTests` 覆盖：参考适配器全绿、三个反例分别让
`revision_replay` / `awaiting_unknown_key` / `error_frame_fatal` 变红。
