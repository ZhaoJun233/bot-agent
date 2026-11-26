# Bot Agent Documentation Hub (`/docs`)

English | [简体中文](index.md)

This directory centralizes Bot Agent's domain business rules, database design, operations runbook, architecture boundaries, Architecture Decision Records (ADRs), API surface, and glossary. The repository root keeps top-level GitHub and quality-gate files ([README](../README.en.md) / [CONTRIBUTING](../CONTRIBUTING.en.md) / [SECURITY](../SECURITY.en.md) / [CHANGELOG](../CHANGELOG.md)) along with module-local quick guides.

---

## 1. Core Guides (Bilingual Pairs)

| Document | English | Chinese | Scope |
| :--- | :--- | :--- | :--- |
| **Core Business Rules & Domain Guide** | [core-business-rules.en.md](domain/core-business-rules.en.md) | [core-business-rules.md](domain/core-business-rules.md) | 5-channel inbound trust boundaries, 3-layer config & `ConversationProfile`, reply pacing, memory taint matrix, Agent task state machine, scheduler/todo/identity linking |
| **Database Design** | [database-design.en.md](storage/database-design.en.md) | [database-design.md](storage/database-design.md) | SQLite WAL runtime config, Schema v1–v15 evolution, ER diagram, 31 physical tables, v15 `meta` JSON ledgers, and on-disk layout |
| **Operations & Incident Runbook** | [runbook.en.md](operations/runbook.en.md) | [runbook.md](operations/runbook.md) | Core monitoring metrics, privacy-safe `mask-cjk.py` log rules, 12-scenario troubleshooting table, and rollback decision flowchart |
| **Engineering Standards & ADR Index** | [engineering-standards-and-adr.en.md](decisions/engineering-standards-and-adr.en.md) | [engineering-standards-and-adr.md](decisions/engineering-standards-and-adr.md) | Directory topology, `ArchitectureProbe` ratchets, Git/CI release workflow, technology trade-offs, and ADR 0001–0006 index |
| **API Surface Map** | — | [api-surface.md](api-surface.md) | Complete legacy panel, ZBA v1, and package HTTP routes (enforced by `ArchitectureProbe`) |
| **Glossary** | — | [glossary.md](glossary.md) | Domain terminology, security invariants, and module naming conventions |

---

## 2. Specialized Specs, Architecture & Operations Docs

### 2.1 Architecture, ADRs & Specifications

- ADRs: [ADR-0001 Composition Root Split](decisions/0001-composition-root-assembly-split.md) · [ADR-0002 Agent Task Runtime](decisions/0002-agent-task-runtime.md) · [ADR-0003 Agent Task Persistence](decisions/0003-agent-task-persistence.md) · [ADR-0004 Unified Memory Authorization](decisions/0004-unified-memory-authorization.md) · [ADR-0005 Memory Taint & Tool Egress Matrix](decisions/0005-memory-tool-coexistence-taint-tiers.md) · [ADR-0006 Optional Identity Linking](decisions/0006-optional-cross-platform-identity-linking.md)
- Frontend & Contracts: [Web Console Design System](architecture/web-design-system.md) · [Configuration UI Ownership](configuration-ui-ownership.md) · [HTTP Contract Registration](architecture/http-contract-registration.md)
- Policy & Config Resolution: [Conversation Profile Resolution](architecture/conversation-profile-resolution.md) · [Platform Policy Resolution](architecture/platform-policy-resolution.md) · [Panel Settings Boundaries](architecture/panel-settings-boundaries.md) · [Model Configuration Apply Path](architecture/model-configuration-apply-path.md)
- Plugins & Runtime: [Plugin Control Lifecycle](architecture/plugin-control-lifecycle.md) · [Plugin Hot Reload](architecture/plugin-hot-reload.md) · [Host Plugin Distribution](architecture/host-plugin-distribution.md) · [Package Data Store](architecture/package-data-store.md) · [Plugin SDK Contracts](architecture/plugin-sdk-contracts.md)
- Feature Specs: [Reply Pacing](reply-pacing.md) · [Unified Memory](unified-memory.md) · [Token Quota Plugin](token-quota-plugin.md) · [Python Worker Protocol](python-worker-protocol.md) · [Python Agent Slice](python-agent-slice.md)

### 2.2 Operations, Migrations & Recovery (`docs/operations/`)

- Deployment & Quality: [Linux Docker First Run](operations/linux-docker-first-run.md) · [Agent Runtime Deployment](operations/agent-runtime-deployment.md)
- Recovery & Backups: [Rollback Playbook](operations/rollback-playbook.md) · [Data Retention & Backup](operations/data-retention-and-backup.md) · [Task Recovery Guide](operations/task-recovery-guide.md) · [Operation Receipt Recovery](operations/operation-receipt-recovery.md) · [Token Usage Recovery](operations/token-usage-recovery.md)

### 2.3 Repository & Submodule Entry Guides

- Main README: [English](../README.en.md) · [简体中文](../README.md)
- Governance & Security: [CONTRIBUTING.en.md](../CONTRIBUTING.en.md) · [SECURITY.en.md](../SECURITY.en.md) · [CHANGELOG.md](../CHANGELOG.md)
- Subprojects: [Web Console Dev Guide](../src/web/README.md) · [Headless Host Guide](../src/BotAgent.Headless/README.en.md) · [Example Scenarios](../examples/scenarios/README.en.md)
