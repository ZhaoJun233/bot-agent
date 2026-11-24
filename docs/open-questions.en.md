# Consolidated Open Questions

English | [简体中文](open-questions.md)

This document centralizes every policy decision, operational SLA, and governance rule across the core documentation set. **All 12 checklist items have now been finalized, executed in code/scripts where applicable, and written back into their target specifications.**

> [!NOTE]
> The previously open `README.en.md` synchronization item and all 12 items below are now resolved and closed.

---

## 1. Core Business Rules ([core-business-rules.en.md](domain/core-business-rules.en.md))

- [x] **Model Provider Circuit-Breaker Threshold & Cooldown**: **Confirmed** — `ProviderCircuitBreaker` / `ProviderFailoverRunner` defaults stay fixed at **`3`** consecutive hard failures and **`120s`** cooldown with single-probe `half_open`, intentionally omitted from console-editable settings.
- [x] **Enterprise Channel Rate-Limit (HTTP 429) Retry & Alert Policy**: **Confirmed** — Outbound HTTP `429` on Feishu (`5s` default hint) and QQ Official (`40034100` / `Retry-After`) enforces Fail-Closed zero blind automatic retries via `ReplyDeliveryResults.StopsFurtherSending`, emitting masked logs and `/zba/v1/events` signals for external alerting.
- [x] **Unified Memory Admin Key Rotation & Handover**: **Confirmed** — `BOTAGENT_UNIFIED_MEMORY_ADMIN` host JSON identity bindings are reviewed quarterly (**90 days**) and rotated immediately on handover/offboarding via container recreation with audit trail.

## 2. Database Design & Persistence ([database-design.en.md](storage/database-design.en.md))

- [x] **Retention / Pruning Policy for Append-Only Ledgers**: **Confirmed** — `trace_archive` already enforces an automatic **7-day rolling TTL** on every write (up to 500 rows/batch in `TraceArchiveStore.Append`); `security_audit_log` remains strictly append-only (no in-place deletion) to preserve SHA-256 chain integrity, archived only via offline DB snapshots.
- [x] **Off-Site Backup Sync & Retention Window**: **Confirmed** — External encrypted daily host jobs replicate `data/backups/qqchat_daily_*.db`, `data/feishu-ids-v2.json`, and `data/plugins/` with **30-day** daily retention (**>= 14 days** for pre-upgrade snapshots).
- [x] **Retirement Timeline for `data/settings.json`**: **Confirmed & Docs Corrected** — SQLite `settings` table is the sole runtime source of truth; `data/settings.json` is legacy import-only (archived to `legacy-json/` by `LegacyJsonImporter`) and scheduled for importer removal in **`v2.0`**.

## 3. Operations & Incident Response ([runbook.en.md](operations/runbook.en.md))

- [x] **Production RTO / RPO SLAs**: **Confirmed** — Baseline SLAs set to **RPO <= 24h** (**RPO ~= 0** on planned upgrades via pre-upgrade snapshots), **RTO <= 30m** for container rollback, and **RTO <= 2h** for off-site host rebuild; continuous WAL PITR stays out of scope for 1 GB hosts.
- [x] **External Real-Time Alerting Integration**: **Confirmed** — External pull-based probes monitor `/healthz` & `/readyz` (60s) and `/zba/v1/status` via `BOTAGENT_ZBA_READ_TOKEN` (5m), alerting via Webhook/email/DM on consecutive probe failures, `stale`/`faulted` backups, or `restarts >= 2` within 15 min.
- [x] **Maintenance Approval & Rollback Sign-Off Ownership**: **Confirmed** — `BotOwner` approves maintenance windows, `dev -> main` PRs, and tags; on-call operators hold pre-authorized unilateral authority to trigger emergency rollback on health or integrity failure.

## 4. Engineering Standards & Plugin Governance ([engineering-standards-and-adr.en.md](decisions/engineering-standards-and-adr.en.md))

- [x] **Ratchet Evolution & Large-File Splitting Roadmap**: **Confirmed & Executed** — Tightened `tests/BotAgent.ArchitectureProbe/Baseline.cs` (`MaxFileLines`: `1643 -> 1591`, `LongestMethodAnywhere`: `198 -> 193`) and updated `large-file-policy.md`; targeting `LongestMethodAnywhere <= 180` in `v2.x` via responsibility-first decomposition without cosmetic `partial` splits.
- [x] **Community Package Signing & Review Governance**: **Confirmed** — Bundled packages check host-pinned SHA-256 (`HostPluginBundle`); external imports require manifest + SHA-256 checks, `TrustAcknowledged`, and maintainer security review with offline distribution keys rotated annually, plus sticky Fail-Closed revocation via `governance.json` in `PluginGovernanceService` (`409 plugin_governance_revoked`).

## 5. Local Dev & Startup Scripts ([README.en.md](../README.en.md))

- [x] **Legacy `start.sh` Env-Var Compatibility Cleanup**: **Confirmed & Executed** — Updated [`start.sh`](../start.sh) to replace retired `AI_DESIRE` / `GROUP_COOLDOWN` / `PRIVATE_COOLDOWN` / `IDLE_FALLBACK` mappings with current `ReplyPacing` variables (`GROUP_INTERJECTION_PROBABILITY`, `GROUP_INTERJECTION_COOLDOWN`, `GROUP_CONTINUATION_WAIT`, `PRIVATE_CONTINUATION_WAIT`).
