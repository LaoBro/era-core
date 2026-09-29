# 实验结果：ConPTY/pywinpty 是否拦截 SGR mouse 输入序列

- **日期**: 2026-07-09
- **背景**: ADR-0006 PRD 计划 11 个 PTY 鼠标测试用例（L190-207）全部未实现，[test_cli_scroll.py:11-12](../../tests/test_cli_scroll.py#L11-L12) 注释声称"ConPTY 在应用启用 ?1000h 后会拦截输入侧的 SGR mouse 序列，不转发到应用 stdin"。本实验验证该说法。

## 研究问题

1. pywinpty（ConPTY 默认 backend）是否拦截 SGR mouse 输入字节？
2. pywinpty（winpty backend）是否拦截？
3. ConPTY 本身是否拦截（排除 pywinpty 中间层）？
4. 是应用端 VtParser 在 ?1000h 模式下吃掉序列，还是字节根本没到达应用？

## 实验 A：pywinpty 注入 SGR mouse 到 Emuera.Headless

### A1：pywinpty 默认 ConPTY backend

- **工具**: `PtyProcess.spawn(backend=0)`（默认 ConPTY）
- **子进程**: Emuera.Headless.exe（自动设 `ENABLE_VIRTUAL_TERMINAL_INPUT` + `EnableSgrMouse`）
- **ERB**: 50 行 PRINTL + `[0] Done` + INPUT
- **注入**: `ESC[<64;1;1M`（SGR mouse 滚轮上，cb=64）
- **判断指标**: 注入后输出 delta 字节数（cb=64 在 offset=0 应触发 DispatchWheel → Scroll Mode → 状态栏渲染，产生大量输出）
- **对照**: `ESC[5~`（PgUp 键盘序列，已知能工作）

**结果**:
| 注入序列 | delta 字节 | 结论 |
|---|---|---|
| `ESC[<64;1;1M`（SGR mouse） | **0** | 字节未到达应用 |
| `ESC[5~`（PgUp 键盘） | **621** | 正常工作，状态栏出现 |

### A2：pywinpty winpty backend

- **工具**: `PtyProcess.spawn(backend=1)`（winpty 旧 backend）
- **其他同 A1**

**结果**:
| 注入序列 | delta 字节 | 结论 |
|---|---|---|
| `ESC[<64;1;1M`（SGR mouse） | **0** | 字节未到达应用 |
| `ESC[5~`（PgUp 键盘） | **621** | 正常工作 |

**A 阶段结论**: 两个 PTY backend 都拦截 SGR mouse 输入字节，但不拦截键盘 CSI 序列。这是 PTY 层的通用行为，与 backend 选择无关。

## 实验 B：最小 ConPTY host + EchoMouse（排除 pywinpty 中间层）

### 目标

排除 pywinpty 中间层，直接用 ConPTY 验证是否是 ConPTY 本身拦截。用最小 .NET 程序 EchoMouse（设 `ENABLE_VIRTUAL_TERMINAL_INPUT` + raw `ReadFile` 读字节，写文件）作为子进程，避免 Emuera.Headless 的 `Console.IsInputRedirected` 检查干扰。

### B1：SteamCMD.ConPTY NuGet 包 + EchoMouse

- **工具**: `SteamCD.ConPTY.WindowsPseudoConsole`（.NET ConPTY 封装）
- **子进程**: EchoMouse.dll（设 `ENABLE_VIRTUAL_TERMINAL_INPUT` + raw ReadFile）
- **注入**: `ESC[5~` / `ESC[<64;1;1M` / `ESC[<0;1;1M`
- **验证**: 读 EchoMouse 写的文件，检查是否包含 `1B 5B 3C`（ESC[<）

**结果**: SteamCD.ConPTY 的 `Write` 方法未投递字节（可能 API 用法问题，未深究），转 B2。

### B2：pywinpty + EchoMouse（关键实验）

- **工具**: `PtyProcess.spawn`（pywinpty，已知能让键盘序列到达 Emuera.Headless）
- **子进程**: EchoMouse.dll（设 `ENABLE_VIRTUAL_TERMINAL_INPUT` + raw ReadFile + 写文件）
- **注入**: `ESC[5~` / `ESC[<64;1;1M` / `ESC[<0;1;1M`
- **验证**: 读 EchoMouse 写的文件

**结果**:
```
READ 4 bytes: 1B 5B 35 7E
```

| 注入序列 | EchoMouse 收到 | 结论 |
|---|---|---|
| `ESC[5~`（键盘） | `1B 5B 35 7E`（4 字节）✓ | 正常透传 |
| `ESC[<64;1;1M`（SGR mouse 滚轮上） | **无** | 被拦截 |
| `ESC[<0;1;1M`（SGR mouse 左键） | **无** | 被拦截 |

## 最终结论

**原注释是对的。** ConPTY/pywinpty 在输入侧拦截了 SGR mouse 序列（`ESC[<...M/m`），但不拦截键盘 CSI 序列。

### 证据链

1. **A1/A2**: pywinpty 两个 backend（ConPTY + winpty）都拦截 SGR mouse，说明与 backend 无关。
2. **B2**: 用最小 EchoMouse（设 `ENABLE_VIRTUAL_TERMINAL_INPUT` + raw `ReadFile`）排除应用逻辑干扰——键盘字节到达，SGR mouse 字节不到达。说明拦截发生在 PTY host 层，不是应用端 VtParser。
3. **VtParser 无 ?1000h 检查**: [VtParser.cs](../../EraCore.Core/Terminal/VtParser.cs) 在 CSI 状态下遇到 `<` 就无条件切到 `SGR_MOUSE` 状态——如果字节到达，必然解析。B2 证明字节没到达。
4. **同质 CSI 序列已透传**: `ESC[5~`/`ESC[H`/`ESC[F` 等 CSI 序列已通过 PTY 注入并被 VtParser 解析成功，`ESC[<0;1;1M` 是同质字节流，但被 PTY host 特殊处理。

### 拦截机制推测

ConPTY host 在输入侧对 SGR mouse 做特殊处理（可能是 VT→Win32 INPUT_RECORD 转换时丢弃无法对应硬件事件的字节，或 host 不主动生成 SGR mouse 序列导致应用永远等不到）。这与 ADR-0007 发现的 `WaitForSingleObject` 误报 signaled（phantom 事件）是同一类 ConPTY 输入侧异常行为。

### 推翻的假设

调查报告（search subagent）曾推断"原注释是错误归因，可能是 HitTest 命中区 Generation 过期或 console.State != WaitInput 导致 DispatchMouseClick 提前 return"。**此推断错误**——B2 实验用 EchoMouse 完全绕过应用逻辑，仍观察不到 SGR mouse 字节，证明拦截发生在 PTY 层而非应用层。

## 已知不一致

[docs/2026.7.9.cli-vt-scroll/PRD.md:232-234](../2026.7.9.cli-vt-scroll/PRD.md#L232-L234) 写道"SGR mouse 输入通过 PTY 写入是字节级透传，不受此限制"——**此设计前提与实验结果矛盾**。PRD L190-207 计划的 11 个 PTY 鼠标测试用例无法通过 PTY 注入实现。

## 参考资料

- [test_cli_scroll.py:11-12](../../tests/test_cli_scroll.py#L11-L12) — 原注释
- [VtParser.cs](../../EraCore.Core/Terminal/VtParser.cs) — SGR mouse 解析状态机（无条件解析 `ESC[<`）
- [WindowsTerminalInput.cs](../../EraCore.Core/Terminal/Platform/WindowsTerminalInput.cs) — `EnableSgrMouse`（`?1000h` + `?1006h`）、raw VT 输入模式
- [docs/2026.6.23.vt-input/spike-results.md](../2026.6.23.vt-input/spike-results.md) — 真实终端下 SGR mouse 验证通过（Windows Terminal 7.6.3 + conhost）
- [docs/2026.6.16.mouse-input/validation-results.md](../2026.6.16.mouse-input/validation-results.md) — raw stdin SGR mouse 验证通过
