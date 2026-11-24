# 待确认项集中清单

简体中文 | [English](open-questions.en.md)

本文件统一收敛来自各核心文档上下文中尚未显式明确的关键决策、运维阈值与治理规范。各专项业务/设计/运维文档中不再分散保留待确认段落；**下列全部 12 项已拍板确认、完成对应代码/脚本改造并回写至对应专项文档。**

> [!NOTE]
> 先前记录在根 `README.md` 中的“英文镜像 `README.en.md` 同步排期”及本清单全量 12 项均已闭环。

---

## 1. 核心业务规则（[core-business-rules.md](domain/core-business-rules.md)）

- [x] **模型供应商熔断阈值与冷却窗口**：**已确认（代码即规范）** —— `ProviderCircuitBreaker` / `ProviderFailoverRunner` 默认固定为连续 **`3`** 次硬失败触发 `open`、冷却 **`120s`** 后放行单次 `half_open` 探测，故意不对外暴露控制台可调配置。
- [x] **企业通道限流（HTTP 429）退避与告警规范**：**已确认** —— 飞书（5s 默认提示）与 QQ 官方（HTTP `429` / `40034100` 透传 `Retry-After`）均实行 Fail-Closed 零盲目自动重试，由 `ReplyDeliveryResults.StopsFurtherSending` 立即阻断后续分段，记录脱敏日志并写入 `/zba/v1/events` 供外置监控告警。
- [x] **统一记忆管理员密钥交接与轮换**：**已确认** —— `BOTAGENT_UNIFIED_MEMORY_ADMIN` 属宿主注入 JSON 身份绑定，按 **90 天（季度）** 定期复核，人员交接或离职时立即轮换并重建容器生效，全程审计留痕。

## 2. 数据库设计与持久化（[database-design.md](storage/database-design.md)）

- [x] **只增审计/轨迹表定期归档裁剪策略**：**已确认（核对代码已实现分治）** —— `trace_archive` 已自带每次写入时 **7 天滚动 TTL 自动清理**（每批最多 500 条）；`security_audit_log` 作为 SHA-256 哈希链严禁原地删行，长期归档走全库离线快照。
- [x] **异地备份与保留期要求**：**已确认** —— 宿主机外部加密任务每日同步 `data/backups/qqchat_daily_*.db`、`data/feishu-ids-v2.json` 与 `data/plugins/` 至异地存储，日常保留 **30 天**，升级前双快照至少保留 **14 天**。
- [x] **`data/settings.json` 退场路线**：**已确认并修正文档** —— SQLite `settings` 表已是唯一运行时真源，`data/settings.json` 正式弃用（仅作 `LegacyJsonImporter` 一次性旧迁移并转存 `legacy-json/`），计划于 **`v2.0`** 完全移除旧 JSON 导入通道。

## 3. 运维排障与应急保障（[runbook.md](operations/runbook.md)）

- [x] **生产 SLA（RTO / RPO）目标**：**已确认** —— 明确生产基线为 **RPO ≤ 24 小时**（升级前快照场景 **RPO ≈ 0**）、容器回滚 **RTO ≤ 30 分钟**、异地全量重建 **RTO ≤ 2 小时**；维持单节点不做 WAL 连续归档 PITR。
- [x] **外部即时告警通道集成**：**已确认** —— 采用外置探针每 60s 测 `/healthz` & `/readyz`、每 5m 测 `/zba/v1/status`（`BOTAGENT_ZBA_READ_TOKEN`），对连续失败、备份 `stale`/`faulted` 或 15 分钟 `restarts ≥ 2` 经 Webhook/邮件/管理员私聊告警。
- [x] **值守升级与回滚签字分工**：**已确认** —— `BotOwner` 审批发布窗口与 `dev → main` / Tag；值守运维在探活或完整性失败时拥有预先授权的**紧急单方面回滚执行权**。

## 4. 工程规范与插件治理（[engineering-standards-and-adr.md](decisions/engineering-standards-and-adr.md)）

- [x] **架构棘轮演进与大文件拆分计划**：**已确认并执行代码收紧** —— 已在 `tests/BotAgent.ArchitectureProbe/Baseline.cs` 将 `MaxFileLines` 从 1643 下调至 **`1591`**、`LongestMethodAnywhere` 从 198 下调至 **`193`**，并同步 `large-file-policy.md`；`v2.x` 目标将最长方法收紧至 **≤ 180** 行，大文件仅按职责拆解、禁机械切分。
- [x] **社区/外部插件签名密钥治理流程**：**已确认** —— 内置包校验宿主内嵌 SHA-256（`HostPluginBundle`）；外部包须核对清单与 SHA-256、显式 `TrustAcknowledged` 及维护者离线密钥年轮换签审；高危/泄漏包通过 `governance.json`（`status: "revoked"`）交 `PluginGovernanceService` 粘性拦截（`409 plugin_governance_revoked`）。

## 5. 本地开发与启动脚本（[README.md](../README.md)）

- [x] **旧版 `start.sh` 兼容变量清理节奏**：**已确认并执行脚本迁移** —— 已更新根目录 [`start.sh`](../start.sh)，移除已退役的 `AI_DESIRE` / `GROUP_COOLDOWN` / `PRIVATE_COOLDOWN` / `IDLE_FALLBACK` 旧映射，全面切换为新版 `ReplyPacing` 变量（`GROUP_INTERJECTION_PROBABILITY`、`GROUP_INTERJECTION_COOLDOWN`、`GROUP_CONTINUATION_WAIT`、`PRIVATE_CONTINUATION_WAIT`）。
