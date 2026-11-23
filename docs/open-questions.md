# 待确认项集中清单

简体中文 | [English](open-questions.en.md)

本文件统一收敛来自各核心文档上下文中尚未显式明确的关键决策、运维阈值与治理规范。各专项业务/设计/运维文档中不再分散保留待确认段落；任何项拍板后请直接勾选对应复选框，并回写相应专项文档或新增 ADR。

> [!NOTE]
> 先前记录在根 `README.md` 中的“英文镜像 `README.en.md` 同步排期”已随本轮双语文档整理完成并闭环。

---

## 1. 核心业务规则（[core-business-rules.md](domain/core-business-rules.md)）

- [ ] **模型供应商熔断阈值与冷却窗口**：确认 `model_providers` 触发 `open` 熔断状态的连续硬失败精确次数阈值与默认冷却时长，并确认是否对外暴露为控制台可调配置。
- [ ] **企业通道限流（HTTP 429）退避与告警规范**：确认飞书私聊或 QQ 官方通道在真实企业租户下触发平台侧主动限流时的业务重试上限与人工告警通知规则。
- [ ] **统一记忆管理员密钥交接与轮换**：确认 `BOTAGENT_UNIFIED_MEMORY_ADMIN` 在多人协作场景下的密钥轮换周期与交接管理规范。

## 2. 数据库设计与持久化（[database-design.md](storage/database-design.md)）

- [ ] **只增审计/轨迹表定期归档裁剪策略**：确认 `trace_archive` 与 `security_audit_log` 是否需要引入定期归档上限或按天 TTL 裁剪机制（当前 DDL 与 `AppDatabase.cs` 未实现自动清理）。
- [ ] **异地备份与保留期要求**：确认生产环境是否通过外部任务将 `data/backups/qqchat_daily_*.db` 及 `data/feishu-ids-v2.json` 同步至异地对象存储/备份盘及对应保留期限。
- [ ] **`data/settings.json` 退场路线**：确认 `data/settings.json` 与 SQLite `settings` 表在后续版本中是否计划完全下线 JSON 文件并单一收敛至数据库。

## 3. 运维排障与应急保障（[runbook.md](operations/runbook.md)）

- [ ] **生产 SLA（RTO / RPO）目标**：确认生产环境具体的恢复时间目标（RTO）与恢复点目标（RPO）基线（当前默认仅保证每日 1 份本地 `VACUUM INTO` 热备，无连续 WAL 异地归档 PITR）。
- [ ] **外部即时告警通道集成**：确认出现 `restarts` 飙升、`backup` 失败或协议断连时，除每日定时健康日报私聊外，是否对接外部即时告警通道（如 Uptime Kuma / Webhook / 邮件）。
- [ ] **值守升级与回滚签字分工**：确认多人值守场景下的升级维护窗口审批人、通知群及紧急回滚决策签字责任人分工。

## 4. 工程规范与插件治理（[engineering-standards-and-adr.md](decisions/engineering-standards-and-adr.md)）

- [ ] **架构棘轮演进与大文件拆分计划**：确认 `BotAgent.ArchitectureProbe` 中的方法长度棘轮（198 行）与大文件基线在后续向 `v1.4+` / `v2.0` 演进时是否计划收紧/拆分或调整基线。
- [ ] **社区/外部插件签名密钥治理流程**：确认插件市场外部包签名校验密钥的签发、吊销与第三方开发者准入评审流程规范。

## 5. 本地开发与启动脚本（[README.md](../README.md)）

- [ ] **旧版 `start.sh` 兼容变量清理节奏**：确认 `start.sh` 内部早期回复欲望值与冷却时间等旧版兼容环境变量映射是否需在后续版本中随新版 `ReplyPacing` 命名一并清理。
