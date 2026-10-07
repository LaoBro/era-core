# 使用与运维指南

本目录收纳面向任务的操作性文档，回答"**要怎么做**"。

与另外两类文档分工：

| 目录 | 回答的问题 |
|---|---|
| `docs/guides/`（本目录） | 要做某件事，具体怎么操作 |
| [`docs/LESSONS/`](../LESSONS/README.md) | 曾经踩过什么坑、为什么 |
| [`ARCHITECTURE.md`](../../ARCHITECTURE.md) | 系统长什么样、模块怎么连 |

## 指南索引

| 指南 | 一句话摘要 |
|---|---|
| [build-and-publish.md](build-and-publish.md) | 构建与发布：`scripts/publish.ps1` 各目标、原生 dotnet 命令、Android NativeAOT 六条约束、前端与 MAUI 资源同步 |
| [performance-tuning.md](performance-tuning.md) | 性能与加载开关：ERB 并行加载、未调用函数延迟解析、关联警告开关、实测数据与验收门禁 |
| [agent-integration.md](agent-integration.md) | Agent 集成：调用链、`start` 的 server 复用优先级、运行期产物、回合协议速览 |
| [turn-protocol.md](turn-protocol.md) | **Turn 协议规格（权威）**：`TurnRecord` 全字段、`diff` 四种行操作、状态与输入类型、帧产出时机、版本演进史 |

## 相关入口

- 项目速览与快速开始：[`README.md`](../../README.md)
- 架构总览：[`ARCHITECTURE.md`](../../ARCHITECTURE.md)
- 测试套件：[`tests/README.md`](../../tests/README.md)
- Web 前端：[`EraCore.Web/README.md`](../../EraCore.Web/README.md)
- 开发环境与常用命令（agent 视角）：[`CLAUDE.md`](../../CLAUDE.md)

> 约定：新增指南按主题建独立文件并登记到上方索引；文档内容需自包含，不要引用仓库中未纳入版本控制的目录。
