# Bot Agent 文档中心（`/docs`）

简体中文 | [English](index.en.md)

本目录统一收敛 Bot Agent 的业务规则、数据库设计、运维手册、架构边界、ADR 决策记录、接口地图与术语表。仓库根目录保留 GitHub 与质量门禁要求的主文档（[README](../README.md) / [CONTRIBUTING](../CONTRIBUTING.md) / [SECURITY](../SECURITY.md) / [CHANGELOG](../CHANGELOG.md)）及各子模块局部引导说明。

---

## 1. 核心必读文档

| 文档 | 中文版 | 英文镜像 | 核心内容 |
| :--- | :--- | :--- | :--- |
| **核心业务规则与领域说明** | [core-business-rules.md](domain/core-business-rules.md) | [core-business-rules.en.md](domain/core-business-rules.en.md) | 5 通道入站信任边界、三层配置与会话 Profile 裁决、回复节奏、记忆敏感度矩阵、Agent 任务状态机与调度/待办/身份绑定 |
| **数据库设计文档** | [database-design.md](storage/database-design.md) | [database-design.en.md](storage/database-design.en.md) | SQLite WAL 运行参数、Schema v1–v15 演进、ER 关系图、31 张物理表、v15 `meta` JSON 账本与文件布局 |
| **运维排障与应急手册** | [runbook.md](operations/runbook.md) | [runbook.en.md](operations/runbook.en.md) | 监控指标与隐私安全查日志规范、12 类常见故障排查对照表、双份回滚预案决策流 |
| **工程规范与 ADR 索引** | [engineering-standards-and-adr.md](decisions/engineering-standards-and-adr.md) | [engineering-standards-and-adr.en.md](decisions/engineering-standards-and-adr.en.md) | 代码仓目录结构、`ArchitectureProbe` 护栏、Git 协作/发版流、技术选型依据与 ADR 0001–0006 索引 |
| **接口地图** | [api-surface.md](api-surface.md) | — | 面板旧接口、ZBA v1 契约与插件路由全表（受 `ArchitectureProbe` 自动覆盖断言校验） |
| **核心术语表** | [glossary.md](glossary.md) | — | 核心领域词汇、安全不变量与模块命名约定 |

---

## 2. 专题规范、架构边界与运维手册

### 2.1 架构与契约设计（`docs/architecture/` & 专题说明）

- [组合根与领域模块拆分（ADR-0001）](decisions/0001-composition-root-assembly-split.md) / [Agent Task Runtime（ADR-0002）](decisions/0002-agent-task-runtime.md) / [任务持久化边界（ADR-0003）](decisions/0003-agent-task-persistence.md)
- [统一记忆分层授权（ADR-0004）](decisions/0004-unified-memory-authorization.md) / [敏感度与工具流出面矩阵（ADR-0005）](decisions/0005-memory-tool-coexistence-taint-tiers.md) / [可选跨端本人身份绑定（ADR-0006）](decisions/0006-optional-cross-platform-identity-linking.md)
- [Web 管理控制台设计系统](architecture/web-design-system.md)（自 `src/web/docs` 集中迁入） / [配置 UI 归属矩阵](configuration-ui-ownership.md) / [HTTP 契约注册表](architecture/http-contract-registration.md)
- [会话 Profile 解析](architecture/conversation-profile-resolution.md) / [平台策略解析](architecture/platform-policy-resolution.md) / [面板设置边界](architecture/panel-settings-boundaries.md) / [模型配置应用路径](architecture/model-configuration-apply-path.md)
- [插件控制生命周期](architecture/plugin-control-lifecycle.md) / [插件热重载](architecture/plugin-hot-reload.md) / [宿主内置分发](architecture/host-plugin-distribution.md) / [包数据隔离存储](architecture/package-data-store.md) / [插件 SDK 契约](architecture/plugin-sdk-contracts.md)
- [新版回复节奏规范](reply-pacing.md) / [统一记忆授权模型](unified-memory.md) / [Token 配额插件设计](token-quota-plugin.md) / [Python Worker 协议](python-worker-protocol.md) / [Python Agent 切片](python-agent-slice.md)

### 2.2 运维部署、迁移与恢复演练（`docs/operations/`）

- [Linux Docker 首次安装与配置](operations/linux-docker-first-run.md) / [Agent Runtime 部署与验证](operations/agent-runtime-deployment.md) / [发版与 CD 操作检查单](operations/release-checklist.md)
- [回滚预案与实测演练](operations/rollback-playbook.md) / [日志保留、每日热备与恢复演练](operations/data-retention-and-backup.md)
- [任务恢复指南](operations/task-recovery-guide.md) / [操作安全记录查询](operations/operation-receipt-recovery.md) / [Token 用量离线逆迁移](operations/token-usage-recovery.md)

### 2.3 仓库入口与模块局部说明

- 主 README：[简体中文](../README.md) · [English](../README.en.md)
- 贡献与安全：[CONTRIBUTING.md](../CONTRIBUTING.md) · [SECURITY.md](../SECURITY.md) · [CHANGELOG.md](../CHANGELOG.md)
- 子工程引导：[Web 前端开发与验证说明](../src/web/README.md) · [Headless 宿主说明](../src/BotAgent.Headless/README.md) · [合成场景示例](../examples/scenarios/README.md)
