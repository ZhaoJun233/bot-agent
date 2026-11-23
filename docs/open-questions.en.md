# Consolidated Open Questions

English | [简体中文](open-questions.md)

This document centralizes every open policy question, unverified operational SLA, and governance rule across the core documentation set. Individual domain, storage, and operations docs no longer keep separate open-question sections; check off items below as decisions are finalized and record the outcome in the corresponding guide or a new ADR.

> [!NOTE]
> The previously open item regarding synchronizing `README.md` changes into `README.en.md` and English documentation mirrors has been completed and closed in this release.

---

## 1. Core Business Rules ([core-business-rules.en.md](domain/core-business-rules.en.md))

- [ ] **Model Provider Circuit-Breaker Threshold & Cooldown**: Confirm the exact consecutive hard-failure count that trips `model_providers` into `open` state and the default cooldown duration, and decide whether these thresholds should become console-configurable settings.
- [ ] **Enterprise Channel Rate-Limit (HTTP 429) Retry & Alert Policy**: Define retry caps and human-alert escalation rules when Feishu or QQ Official tenants hit platform-enforced throttling (`429`).
- [ ] **Unified Memory Admin Key Rotation & Handover**: Define rotation intervals and team handover governance for `BOTAGENT_UNIFIED_MEMORY_ADMIN` in multi-operator environments.

## 2. Database Design & Persistence ([database-design.en.md](storage/database-design.en.md))

- [ ] **Retention / Pruning Policy for Append-Only Ledgers**: Confirm whether `trace_archive` and `security_audit_log` require scheduled pruning caps or daily TTL cleanup (currently `AppDatabase.cs` defines no automatic deletion).
- [ ] **Off-Site Backup Sync & Retention Window**: Confirm whether external jobs replicate `data/backups/qqchat_daily_*.db` and `data/feishu-ids-v2.json` to off-site object storage and what retention period applies.
- [ ] **Retirement Timeline for `data/settings.json`**: Confirm whether `data/settings.json` will be fully retired in favor of the SQLite `settings` table in a future version.

## 3. Operations & Incident Response ([runbook.en.md](operations/runbook.en.md))

- [ ] **Production RTO / RPO SLAs**: Confirm concrete Recovery Time Objective (RTO) and Recovery Point Objective (RPO) targets (currently defaults only guarantee one daily local `VACUUM INTO` hot backup without continuous off-site WAL PITR).
- [ ] **External Real-Time Alerting Integration**: Confirm whether spikes in `restarts`, backup failures, or gateway disconnects integrate with external alerting channels (e.g., Uptime Kuma / Webhook / email) beyond the scheduled private health report.
- [ ] **Maintenance Approval & Rollback Sign-Off Ownership**: Clarify on-call upgrade window approvers, notification channels, and rollback sign-off roles in multi-operator setups.

## 4. Engineering Standards & Plugin Governance ([engineering-standards-and-adr.en.md](decisions/engineering-standards-and-adr.en.md))

- [ ] **Ratchet Evolution & Large-File Splitting Roadmap**: Confirm whether `BotAgent.ArchitectureProbe` method-length ratchets (`198` lines) and large-file baselines will be tightened or decomposed in future `v1.4+` / `v2.0` milestones.
- [ ] **Community Package Signing & Review Governance**: Formalize key issuance, revocation, and third-party publisher review policies for external marketplace packages.

## 5. Local Dev & Startup Scripts ([README.en.md](../README.en.md))

- [ ] **Legacy `start.sh` Env-Var Compatibility Cleanup**: Confirm when remaining legacy reply-desire/cooldown compatibility mappings inside `start.sh` should be retired in favor of `ReplyPacing` names.
