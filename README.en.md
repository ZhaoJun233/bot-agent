# Bot Agent

English | [简体中文](README.md)

[![.NET](https://img.shields.io/badge/.NET-10.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20(Ubuntu%20%7C%20Debian%20%7C%20CentOS%20%7C%20Arch)%20%7C%20Docker-green.svg)](#quick-start)
[![Protocol](https://img.shields.io/badge/Protocol-OneBot%20v11%20%7C%20QQ%20Official%20%7C%20Feishu-purple.svg)](#system-architecture)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

Bot Agent is a headless, high-performance, pluggable multi-chat-platform Agent daemon built for Linux environments. It supports QQ Private Chats ([NapCat](https://github.com/NapNeko/NapCatQQ) / OneBot v11), QQ Official Open Platform, Feishu (Lark) applications (WebSocket & Webhook), a zero-dependency local playground channel, and an isolated Personal Workspace (`workspace:owner`). Interacting with OpenAI / Anthropic / Gemini compatible LLMs, it provides autonomous speaking judgment, governed memory, and lifelike replies across group chats, direct messages, and workspace sessions, paired with a standalone static Web Management Console.

The project supports running natively from source or artifacts on standard Linux distributions, orchestrating with Docker Compose, or deploying on memory-constrained (`1 GB` RAM) hosts via the artifact-only runtime image [`Dockerfile.app`](Dockerfile.app).

---

## Core Documentation Hub

| Document | Summary |
| :--- | :--- |
| [Documentation Hub (`/docs`)](docs/index.en.md) | Unified entry point to business rules, database design, operations runbook, architecture specs, ADRs, and glossary |
| [Core Business Rules & Domain Guide](docs/domain/core-business-rules.en.md) | Five-channel trust boundaries, 3-layer config & ConversationProfile resolution, reply pacing & silent short-circuit, memory taint matrix, Agent task state machine, scheduler/todo/identity-linking rules |
| [Database Design](docs/storage/database-design.en.md) | SQLite WAL runtime settings, Schema v1–v15 evolution, ER diagram, all 31 physical business tables, Schema-v15 `meta` JSON ledgers, and on-disk layout |
| [Operations & Incident Runbook](docs/operations/runbook.en.md) | Core observability metrics, privacy-safe masked log inspection rules, 12-scenario triage table, and dual-backup rollback flowchart |
| [Engineering Standards & ADR Index](docs/decisions/engineering-standards-and-adr.en.md) | Repository layout, coding standards & `ArchitectureProbe` ratchets, Git & release pipeline, key technology trade-offs, and ADR 0001–0006 index |
| [API Surface Map](docs/api-surface.md) | Complete legacy panel, ZBA v1, and package HTTP routes (enforced by `ArchitectureProbe` against `PanelRoutes.cs`) |
| [Glossary](docs/glossary.md) | Canonical domain terminology, security invariants, and module naming conventions |
| [Consolidated Open Questions](docs/open-questions.en.md) | Single centralized checklist of undecided thresholds, SLAs, and governance policies across all docs |
| [Linux Docker First-Run Guide](docs/operations/linux-docker-first-run.md) | Prebuilt artifact workflow for 1 GB hosts, standalone Caddy frontend, and controlled acceptance |

---

> [!CAUTION]
> ### Account Safety and Risk Control Warning
>
> Due to strict anti-automation and risk-control policies enforced by instant messaging platforms, and based on real ban experiences reported by project contributors, note the following environment rules:
>
> 1. **Avoid Mobile Devices or Volatile Networks**: Do NOT run the protocol client locally on Android phones, inside Termux terminals, or over switching cellular networks. High-frequency API calls on such egress paths are easily flagged, leading to permanent bans or frequent account freezes.
> 2. **Recommended Deployment Architecture**: Deploy in trusted data centers (cloud VPS / dedicated servers), on home mini-PCs with stable broadband IP addresses, or on dedicated Linux devices with clean, stable network egress.
> 3. **Isolate Production from Testing**: Never bind primary personal or work accounts as bot endpoints. Use dedicated test accounts or prioritize the [QQ Official Open Platform Channel](#qq-official-open-platform-optional) for compliant operation.

---

## Key Features

### Intelligent Judgment & Natural Interaction
- **Unified Reply Pacing**: [`Reply Pacing`](docs/reply-pacing.md) governs `GroupInterjectionProbability`, `GroupInterjectionCooldownSeconds`, group/DM quiet continuation windows, and `SuitabilityThreshold`. Explicit requests bypass group throttling while silent decisions short-circuit before outbound side effects.
- **Tiered Memory & Scoped Knowledge Base**: [ADR-0004](docs/decisions/0004-unified-memory-authorization.md) and [ADR-0005](docs/decisions/0005-memory-tool-coexistence-taint-tiers.md) enforce per-fact, per-version, destination-bound user authorization and a fail-closed `MemorySensitivityTier` × `ToolEgressClass` matrix; includes isolated per-scope BM25 knowledge bases (`<= 256` docs / `<= 4096` chunks / `<= 1 MiB` per scope).
- **Action Narration Filtering & Multimodal Perception**: Strips stage-direction narration while preserving math, code, and links; includes vision byte caching and expiring URL re-signing, bidirectional pokes, magic-byte-verified sticker vaults, precise reply quoting, music waveform/lyric analysis, lifelike sentence splitting, and cloud TTS voice bars without duplicate text broadcasts.

### Multi-Platform, Package Decoupling & Console Workspace
- **22 Decoupled Feature & Platform Packages**: 4 platform drivers (QQ Private, QQ Official, Feishu, Local Playground), system capability packages (Model Services, Built-in Agent, MCP Tools, Conversation Orchestration, Health Reporting), and conversation packages (Token Quota, Web Search, Poke, Vibes, Stickers, Music, Voice, Profiles, Documents, Digest, Knowledge, Scheduler, Personal Todo), supporting hot reload/toggle with old-generation preservation on failure.
- **Conversation Profiles & Cross-Platform RBAC / Approvals**: Per-conversation conversation/tool-tier overrides with live capability fingerprinting, cross-platform `WorkspaceRole` enforcement, human approval gates for non-read-only plugin tools, and opt-in cross-platform identity linking via 120s in-memory HMAC challenge ([ADR-0006](docs/decisions/0006-optional-cross-platform-identity-linking.md)).
- **Personal Workspace & Sandboxed Plugin UIs**: Isolated Personal Workspace (`workspace:owner`) plus scheduler and personal-todo UIs rendered inside `origin === "null"` sandboxed iframes with synchronized dark/light themes and admin-mode checks.

---

## Quick Start

For first-time production Linux Docker setups, read [Linux Docker First-Run Guide](docs/operations/linux-docker-first-run.md). Before upgrading, always preserve dual rollback anchors per the [Rollback Playbook](docs/operations/rollback-playbook.md); check ambiguous task submissions via the [Task Recovery Guide](docs/operations/task-recovery-guide.md).

### Prerequisites

| Component | Local Dev / Build Machine | Production Server Runtime | Purpose |
| :--- | :--- | :--- | :--- |
| .NET SDK / Runtime | **.NET 10.0 SDK** | Bundled in Docker image (or host .NET 10 Runtime) | Build & run `BotAgent.slnx` and `BotAgent.Headless` |
| Node.js & npm | **Node.js `22.22.2+` / `24.15.0+` / `26+`** | Not required (static web server only) | Build & verify the `src/web` Vue 3 console |
| Python | **Python 3.12+** | Local diagnostic/audit scripts only | Run `BotAgent.ServerFilesProbe`, `mask-cjk.py`, `push-desensitized.py` |
| Container & Reverse Proxy | Docker (optional) | Docker Compose + Caddy / Nginx | Run containers and serve standalone `/web/` assets |
| Protocol Client (optional) | — | [NapCat](https://github.com/NapNeko/NapCatQQ) (for QQ Private) | Provides OneBot v11 Forward/Reverse WS connection |

### Method 1: Local Development & Source Debugging (Decoupled Frontend/Backend)

```bash
# 1. Restore and build the full solution (includes NuGet high/critical audit gate)
dotnet restore BotAgent.slnx
dotnet build BotAgent.slnx -c Release

# 2. Copy and configure environment variables (local dev can enable loopback ZBA with BOTAGENT_ZBA_ALLOW_LOOPBACK=1)
cp .env.example .env

# 3. Start headless backend host (defaults to 127.0.0.1:18245; ships no embedded HTML)
dotnet run --project src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# 4. In another terminal, launch frontend dev server (proxies /api and /zba to backend port)
cd src/web
npm ci
npm run dev
```

To build only static production frontend assets, run `npm run build` inside `src/web`; files are emitted to `src/web/dist` for Caddy/Nginx hosting under `/web/` (see [`src/web/README.md`](src/web/README.md)).

### Method 2: Native Linux Scripts (No Docker)

```bash
cp .env.example .env
# Edit .env with model keys and whitelists

./start.sh        # Start in background (writes to runtime/logs/bot-agent.log)
./start.sh -f     # Start in foreground for debugging
./status.sh       # Inspect PID, /healthz, /readyz, and last 10 log lines
./stop.sh         # Gracefully stop service
```

Once started, `http://127.0.0.1:18245/healthz` returns `{"status":"ok","version":"x.y.z"}` sourced from `Directory.Build.props` (container images typically use port 8080).

### Method 3: Docker Compose / Low-Memory Artifact Image Deployment

- **Standard Compose Deployment** (includes NapCat and source build):
  ```bash
  cp .env.example .env
  # Edit .env, then launch
  docker compose up -d
  docker compose logs -f napcat   # Scan QR code on first login
  ```
- **Recommended Artifact Route for 1 GB Hosts**: Publish `linux-x64` artifacts (`app.tar.gz`) on a dev/build machine and build the runtime image on the server via [`Dockerfile.app`](Dockerfile.app) so `dotnet publish` never exhausts swap on small hosts. Full steps are documented in [Linux Docker First-Run Guide](docs/operations/linux-docker-first-run.md).

---

## Configuration Reference

Configuration uses a **three-layer ownership model** (`compose .env` + `host environment` + `SQLite database`). Confusing these layers is the most common reason changes appear ignored.

| Layer | Location | Owner | Precedence & Lifecycle |
| :--- | :--- | :--- | :--- |
| 1. Compose layer | Root `.env` | Deployer | Only substitutes `${VAR}` in `docker-compose.yml`; **never reaches the container unless listed under `environment:`** |
| 2. Host environment | Container `environment:` / process env | Read at startup | Canonical `BOTAGENT_*` wins over `QQCHAT_*` / `OPENAI_*` (with `{NAME}_FILE` secret support); **secrets are never written to `settings.json`**; infrastructure values always override |
| 3. Database source of truth | `data/botagent.db` (& `data/settings.json`) | Console after save | Behavioral settings are seeded from env on first boot only; once saved in the panel (CAS + singleflight), database values take over |

> [!WARNING]
> Never overwrite a live deployment's `docker-compose.yml` with the repo default—doing so can wipe manual edits such as NapCat's `ACCOUNT` and force a fresh QR login. See [`.env.example`](.env.example) for the complete key template.

### Core Host Environment Variables

| Canonical Key (Preferred) | Legacy Aliases | Default / Example | Scope & Behavior |
| :--- | :--- | :--- | :--- |
| `BOTAGENT_DATA_DIR` | `QQCHAT_DATA_DIR` | `./runtime` or `/data` | Single-bound runtime data root (`data/botagent.db`, logs, plugins) |
| `BOTAGENT_API_KEY` | `QQCHAT_API_KEY`, `OPENAI_API_KEY` | Required (supports `_FILE`) | Primary model API key; never persisted into `settings.json` |
| `BOTAGENT_BASE_URL` | `QQCHAT_BASE_URL`, `OPENAI_BASE_URL` | `https://api.openai.com/v1` | Primary OpenAI-compatible model endpoint |
| `BOTAGENT_MODEL` | `QQCHAT_MODEL`, `OPENAI_MODEL` | `gpt-4o-mini` | Primary model identifier |
| `BOTAGENT_FAST_MODEL` / `BOTAGENT_FAST_REPLY` | `QQCHAT_FAST_MODEL` / `QQCHAT_FAST_REPLY` | Empty / `false` | Lightweight model override for interactive chat replies |
| `BOTAGENT_QQ_ONEBOT_PROTOCOL` | `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` | OneBot mode: `ForwardWebSocket` / `ReverseWebSocket` |
| `BOTAGENT_QQ_ONEBOT_URL` | `QQCHAT_ONEBOT_URL`, `QQCHAT_ONEBOT_ADDRESS` | `ws://127.0.0.1:3001` | OneBot WebSocket endpoint |
| `BOTAGENT_QQ_ONEBOT_TOKEN` | `QQCHAT_ONEBOT_TOKEN` | Empty (supports `_FILE`) | OneBot authentication token |
| `BOTAGENT_QQ_UIN` | `QQCHAT_UIN`, `QQCHAT_QUICK_LOGIN_UIN` | Empty | Bot QQ account (auto-detected after connect if empty) |
| `BOTAGENT_HEALTH_PORT` | `QQCHAT_HEALTH_PORT`, `HEALTH_PORT` | `18245` native / `8080` container | HTTP port for panel API, ZBA, and `/healthz` (`0` = disabled) |
| `BOTAGENT_HEALTH_BIND` | `QQCHAT_HEALTH_BIND` | `127.0.0.1` | Bind address (`+` for proxy bridge; forced loopback without auth) |
| `BOTAGENT_PANEL_TOKEN` | `QQCHAT_PANEL_TOKEN` | Empty | Legacy `/api/*` panel token |
| — | `QQCHAT_PANEL_PASSWORD` | Empty | Initial panel password seed on first boot (managed in panel afterward) |
| `BOTAGENT_ZBA_TOKEN` | `QQCHAT_ZBA_TOKEN` | Empty | ZBA admin Bearer token (required for writes; management endpoints return `503` when unset) |
| `BOTAGENT_ZBA_READ_TOKEN` | `QQCHAT_ZBA_READ_TOKEN` | Empty | ZBA read-only Bearer token (for read views & CLI checks) |
| `BOTAGENT_ZBA_ALLOW_LOOPBACK` | `QQCHAT_ZBA_ALLOW_LOOPBACK` | `0` (off) | `1` allows tokenless loopback ZBA access (**local dev only**) |
| `BOTAGENT_ZBA_URL` | — | `http://127.0.0.1:<port>/zba/v1` | Default base URL for ZBA read-only CLI commands (`--status`, etc.) |
| `BOTAGENT_AGENT_TOKEN` | `QQCHAT_AGENT_TOKEN` | Empty (supports `_FILE`) | Built-in server Agent token |
| `BOTAGENT_AGENT_SERVER_KEY` | `QQCHAT_AGENT_SERVER_KEY` | Empty (supports `_FILE`) | Dedicated model API key override for Agent sub-tasks |
| `BOTAGENT_NAPCAT_WEBUI_URL` / `BOTAGENT_NAPCAT_WEBUI_TOKEN` | `QQCHAT_NAPCAT_WEBUI_*` | `http://127.0.0.1:6099` / Empty | Embedded NapCat QR login proxy URL & token |
| `BOTAGENT_FEISHU` / `BOTAGENT_FEISHU_APP_ID` / `BOTAGENT_FEISHU_WHITELIST` | `QQCHAT_FEISHU*` | `0` / Empty | Feishu channel toggle, AppId, and initial whitelist seed |
| `BOTAGENT_FEISHU_APP_SECRET` / `BOTAGENT_FEISHU_ENCRYPT_KEY` / `BOTAGENT_FEISHU_VERIFICATION_TOKEN` | `QQCHAT_FEISHU_*` | Empty (supports `_FILE`) | Feishu credentials & event encryption secrets (env-only) |
| `BOTAGENT_VOICE_EMOTION` | `QQCHAT_VOICE_EMOTION` | — | Initial default emotion preset for cloud TTS |
| `BOTAGENT_VERBOSE` / `BOTAGENT_LOG_FILE` | `QQCHAT_VERBOSE` / `QQCHAT_LOG_FILE` | `1` / `1` | Verbose logging and log-file persistence toggle (`0` = stdout only) |
| `TZ` | — | Host local or `Asia/Shanghai` | Default runtime timezone |

### QQ Official Open Platform (Optional)

Fully isolated from private QQ (`official:` conversation key prefix, separate whitelists and `own_messages_scoped` ledger):

| Key | Meaning |
| :--- | :--- |
| `BOTAGENT_OFFICIAL` / `QQCHAT_OFFICIAL` | `1` = enable QQ Official channel |
| `QQCHAT_OFFICIAL_APP_ID` | Official Open Platform AppId |
| `BOTAGENT_OFFICIAL_APP_SECRET` / `QQCHAT_OFFICIAL_APP_SECRET` | Official AppSecret (env-only, never stored in DB or echoed by panel) |

---

## Web Management Console

A standalone Vue 3 + TypeScript SPA communicating with `BotAgent.Headless` via ZBA (`zba-1.0`), served statically under `/web/`:

- **Domain-Separated Views**: **Overview**, **Personal Workspace (`workspace:owner`)**, **Model Providers** (per-provider API Base URL/Key integration, atomic secondary credential saves, routing & circuit breaker status), **Message Platforms** (4 driver cards with isolated Access & Policies drawers), **Plugin Center** (22 bundled + external packages with hot toggle, schema forms, and `origin === "null"` sandbox iframe UIs for Stickers, Token Quotas, Scheduler, and Personal Todos), **System Configuration** (General Behavior, Conversation Profiles, MCP Servers, Cross-Platform RBAC, Agent & Health Reporting), **Agent Tasks**, **Traces & Cache**, and **Runtime Logs**.
- **In-Memory Security Boundaries**: Bearer tokens stay strictly in memory (cleared on reload/lock); writes enforce CAS versioning, `singleflight` locks, and operation/idempotency key recovery; light/dark theme tokens sync live into sandboxed iframes.

---

## SSL/TLS and Reverse Proxy

`BotAgent.Headless` listens over **plain HTTP only** (`HttpListener`) and never terminates TLS or reads certificate env vars. Terminate HTTPS in Caddy/Nginx while serving `/web/` static assets; never allow unauthenticated fallbacks or missing `/web/assets/*.js` paths to return login HTML with `HTTP 200` (causes CDN cache poisoning and blank pages).

- **Local Self-Signed Certs**: `./tools/gencert.sh [domain] [IP]` writes a 1095-day cert bundle to `runtime/certs/`.
- **Production Certs**: Provision with certbot / acme.sh / Cloudflare + Caddy at the proxy layer.

---

## System Architecture

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot Client (NapCat)  | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 10)  |
+-------------------------+                                 |   +-- Multi-Platform Gateways      |
                                                            |   +-- 3-Layer Config & Profiles    |
+-------------------------+          Official Open API      |   +-- Reply Pacing & Memory Matrix |
| QQ Official Platform    | <----------------------------> |   +-- Agent Runtime / RBAC / Gates |
+-------------------------+                                 |   +-- Scheduler / Todos / BM25 KB  |
                                                            |   +-- ZBA v1 & Local / Workspace   |
+-------------------------+        WS / Event Webhook       |   +-- 22 Bundled Packages          |
| Feishu (Lark) Platform  | <----------------------------> +-----------------+----------------+
+-------------------------+                                                   |
                                                                              v OpenAI / Anthropic / Gemini
                                                            +----------------------------------+
                                                            | LLMs & MCP External Tool Servers |
                                                            +----------------------------------+
```

---

## Troubleshooting Local Startup & Builds

| Symptom / Error | Root Cause | Resolution |
| :--- | :--- | :--- |
| Exits on boot with `database_schema_newer` or `configuration_schema_newer` | `botagent.db` has a higher `PRAGMA user_version` / config revision than the binary supports | Never edit `PRAGMA user_version`; upgrade the binary or restore a compatible backup per the [Rollback Playbook](docs/operations/rollback-playbook.md) |
| All `/zba/v1/*` calls return `503` (`/zba/v1/health` works) | `BOTAGENT_ZBA_TOKEN` is unset (fail-closed protection) | Configure `BOTAGENT_ZBA_TOKEN` (or `BOTAGENT_ZBA_ALLOW_LOOPBACK=1` for local dev only) |
| `.env` whitelist / persona changes have no effect | Panel/DB already saved behavioral settings, or var missing from compose `environment:` | Update via Web Console (check startup log for panel-override notices); expose new vars in `docker-compose.yml` |
| `IntegrationHarness` still executes stale host code | Building Harness alone does not rebuild `BotAgent.Headless` launched as child process | Explicitly run `dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild` first |
| `dotnet build` fails with `NU1903` / `NU1904` | `Directory.Build.props` treats high/critical transitive NuGet advisories as build errors | Upgrade vulnerable package versions; never lower `NuGetAuditLevel` |
| Standalone probe exits `-1073741502` or temp-path error | Restricted shell temp path or child process env issues | Build first (`dotnet build -c Release`), ensure writable clean `TEMP`/`TMP`, then run `dotnet <Probe>.dll` |
| 1 GB server hangs or OOMs on source `Dockerfile` | In-container `dotnet publish` exhausts RAM + swap | Publish `app.tar.gz` locally and deploy with [`Dockerfile.app`](Dockerfile.app) |
| Blank `/web/` page with `Unexpected token '<'` | Proxy/CDN served and cached HTML 200 for `/web/assets/*.js` | Return `404` (`no-cache`) for missing `/web/assets/*` and `303` + `Cache-Control: no-store` for unauthenticated redirects |

---

## Testing and Quality Assurance

> [!TIP]
> Clear residual env vars (`BOTAGENT_*`, `QQCHAT_*`, `OPENAI_*`, `HARNESS_*`, `HEALTH_PORT`) before running synthetic probes/suites. Full integration scenario filtering flags and probe matrices are documented in [`CONTRIBUTING.en.md`](CONTRIBUTING.en.md) and [`docs/operations/ci-quality-gates.md`](docs/operations/ci-quality-gates.md).

```bash
# 1. Rebuild Headless host before running IntegrationHarness
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild

# 2. Run key architecture, safety, and ZBA probes
dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net10.0/BotAgent.ArchitectureProbe.dll
dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet tests/BotAgent.SafetyProbe/bin/Release/net10.0/BotAgent.SafetyProbe.dll
dotnet build tests/BotAgent.ZbaProbe/BotAgent.ZbaProbe.csproj -c Release
dotnet tests/BotAgent.ZbaProbe/bin/Release/net10.0/BotAgent.ZbaProbe.dll

# 3. Run full synthetic integration suite (s1-s165; filter options in CONTRIBUTING.en.md)
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll

# 4. Run frontend typecheck, unit tests, and Playwright E2E (under src/web)
cd src/web
npm run typecheck && npm run test:unit
npm run test:e2e -- --project=chromium
```

---

## License

Licensed under the [MIT License](LICENSE). Third-party protocol clients (such as NapCat) remain governed by their own licenses and platform terms.
