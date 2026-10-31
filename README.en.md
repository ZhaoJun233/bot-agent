# Bot Agent

English | [简体中文](README.md)

[![.NET](https://img.shields.io/badge/.NET-8.0-blue.svg)](https://dotnet.microsoft.com/)
[![Platform](https://img.shields.io/badge/Platform-Linux%20(Ubuntu%20%7C%20Debian%20%7C%20CentOS%20%7C%20Arch)%20%7C%20Docker-green.svg)](#quick-start)
[![Protocol](https://img.shields.io/badge/Protocol-OneBot%20v11%20%7C%20QQ%20Official%20%7C%20Feishu-purple.svg)](#system-architecture)
[![License](https://img.shields.io/badge/License-MIT-orange.svg)](LICENSE)

Bot Agent is a headless, high-performance, pluggable multi-chat-platform Agent daemon built for Linux environments. It supports QQ Private Chats ([NapCat](https://github.com/NapNeko/NapCatQQ) / OneBot v11), QQ Official Open Platform, Feishu (Lark) applications, and a zero-dependency local playground channel. Interacting with OpenAI-compatible Large Language Models (including DeepSeek, Qwen, OpenAI, local Ollama, etc.), it delivers autonomous judgment and lifelike replies across group chats, direct messages (DMs), and channels, complete with a built-in lightweight Web management console.

The project natively supports running directly from source code across standard Linux distributions (Ubuntu, Debian, CentOS, Arch, etc.) without requiring a container environment, while preserving standard Docker Compose containerized deployment support.

---

> [!CAUTION]
> ### Account Safety and Risk Control Warning
>
> Due to strict risk control and anti-automation policies enforced by instant messaging platforms, and based on real ban experiences reported by project contributors, please take note of the following operating environment risks:
>
> 1. **Avoid Running on Mobile Devices or Volatile Networks**: Do NOT run the protocol client locally on Android phones, inside Termux terminals, or across cellular base station networks that switch frequently. High-frequency API invocations in such environments are easily flagged as abnormal clients by anti-abuse systems, leading to permanent bans or frequent account freezes.
> 2. **Recommended Deployment Architecture**: Deploy the service in trusted remote data centers (cloud VPS / dedicated servers), on home mini-PCs with stable static/broadband IP addresses, or on dedicated devices running standard Linux (Ubuntu / Debian, etc.), keeping network egress clean and stable.
> 3. **Isolate Production from Testing**: Never use your primary personal or work accounts as the bot endpoint. Use a dedicated auxiliary account during testing, or prioritize using the [QQ Official Open Platform Channel](#qq-official-open-platform-optional) for compliant and stable operations.

---

## Key Features

### Intelligent Judgment and Natural Interaction
- **Autonomous Speaking Opportunity Decisions**: The model evaluates the necessity of speaking based on conversation context and configured suitability thresholds, remaining silent when below the threshold; combined with rate-limiting cooldown algorithms to prevent spamming and mechanical interruptions.
- **Atmosphere Awareness & Fatigue Damping**: Dynamically analyzes message velocity and room sentiment; applies non-linear desire damping after consecutive bot turns and enforces cooldown periods, reducing interruptions in quiet groups while practicing restraint during heated arguments.
- **User Profiles & Long-Term Memory**: Constructs short-term conversation stacks and long-term user personas based on sender identifiers (with evidence-backed traceability and manual overrides), persisted to an embedded SQLite database.
- **Action Narration Semantic Filtering**: Supports filtering stage-direction narration (e.g., bracketed actions) from model outputs, verified by a secondary semantic check while strictly preserving math formulas, code snippets, links, and genuine explanations.
- **Multimodal Visual Perception**: Automatically downloads group images and passes them to multimodal vision models; features internal image byte caching and automatic URL re-signing for expiring temporary URLs.
- **Deep Adaptation to Native Interactions**:
  - **Poke Responses**: Detects bidirectional poke events, equipped with cooldown gates and autonomous poke-back logic.
  - **Sticker Coordination & Security Guard**: Automatically ingests and reviews stickers, extracts semantic tags, and retrieves candidates by context; features built-in `StickerSafetyGuard` for path traversal prevention and binary magic-byte spoofing verification.
  - **Precise Context Quoting**: Automatically resolves quote targets from received replies, crediting the original message when the model replies.
  - **Audio Analysis & Topic Continuation**: When music cards are shared, fetches lyrics and analyzes waveform acoustic features (tempo, dynamics, loudness, and section structure), ensuring responses are grounded in objective facts.
- **Lifelike Typing Cadence**: Splits long replies at punctuation boundaries and sends them in batches simulating realistic typing delays.

### Multi-Platform Support & Instance Policies
- **Unified Platform Policy Governance**: Supports QQ Private, QQ Official Open Platform, Feishu Bot, and Local channel. The Web Console's "Platform Access & Policies" under "Capabilities" is the single entry point for each platform's enable and chat switches, group/private whitelists, and feature overrides. Chat mute preserves connectivity; disabling a platform blocks chat. Credentials, connection state, whitelists, and the global AI switch remain independent safety gates.
- **Daily Token Quota Ledger**: Runs as the preset plugin `preset.feature.token-quota`, managing UTC daily budgets isolated by platform and conversation tenant (`SourceKey`). Supports tenant filtering, real-time usage monitoring, and automatic power-saving silence when quotas are exhausted; supports hot tuning and persistence directly from the Capabilities page.
- **Zero-Dependency Local Test Channel**: an in-process standalone channel (no external long connection). Register virtual group IDs in the panel under "Capabilities → Platform Access Policies → Local Channel", then inject test messages with `POST /api/local/message`; they traverse the full model decision pipeline and the six-stage governance timeline.

---

## Quick Start

### Method 1: Native Linux Execution (Recommended, No Docker Required)

#### 1. System Requirements & Environment Setup
- **Operating System**: Standard Linux distribution (Ubuntu 20.04+, Debian 11+, CentOS 8+, Arch Linux, etc.)
- **Runtime Dependencies**: .NET 10.0 SDK / Runtime, Git, curl, openssl
- **OneBot Protocol Client**: [NapCat Linux Shell](https://github.com/NapNeko/NapCatQQ) (if using QQ Private Channel)

Install base dependencies on Debian / Ubuntu:

```bash
# Update package lists and install prerequisites
sudo apt-get update
sudo apt-get install -y dotnet-sdk-10.0 git curl openssl
```

#### 2. Obtain Source Code & Build

```bash
# Clone the main repository (main branch)
git clone -b main https://github.com/ZhaoJun233/bot-agent.git
cd bot-agent

# Generate and edit configuration file
cp .env.example .env
vim .env
```

#### 3. Operations & Daemon Management

The project includes cross-distribution compatible control scripts:

- **Start in background**:
  ```bash
  ./start.sh
  ```
- **Start in foreground (debug console output)**:
  ```bash
  ./start.sh -f
  ```
- **Check status and health probes**:
  ```bash
  ./status.sh
  ```
- **Safely stop service**:
  ```bash
  ./stop.sh
  ```
- **View live log output**:
  ```bash
  tail -f runtime/logs/bot-agent.log
  ```

Once the service starts: health probe at `http://127.0.0.1:18245/healthz` (port per the configuration reference below; 8080 inside the container image); **the host ships no pages at all** — `/` and `/web/` are served by the reverse proxy. See [`src/web/README.md`](src/web/README.md) for frontend deployment.

---

### Method 2: Docker Compose Containerized Execution

On hosts with Docker installed, you can use container orchestration:

```bash
cp .env.example .env
vim .env

# Build and start services
docker compose up -d

# View protocol client login QR code
docker compose logs -f napcat
```

---

## Configuration Reference

Configuration is not "one `.env` file rules everything". It is **two environment-variable layers plus one source of truth**. Mixing them up is the most common cause of "I changed it but nothing happened".

### The three layers

| Layer | Location | Owner | When it takes effect |
| :--- | :--- | :--- | :--- |
| 1. compose layer | `.env` in the repo root | deployer | only feeds `${VAR}` substitution in `docker-compose.yml`; **never reaches the container by itself** |
| 2. host layer (environment) | the container's `environment:` / process env | read once at startup | secrets and infrastructure: **always override**, restart to apply |
| 3. source of truth (panel + SQLite) | `data/botagent.db` | owned by the panel after a save | behavioural settings: applied live and persisted |

Three ownership rules (same origin as the class comment in `src/BotAgent.Core/Contracts/AppSettings.Part1.cs`):

1. **Secrets are read from the environment only** (`BOTAGENT_API_KEY` / `QQCHAT_API_KEY`, `BOTAGENT_QQ_ONEBOT_TOKEN`, `BOTAGENT_PANEL_TOKEN`, `BOTAGENT_AGENT_TOKEN`…). They are **never persisted**, so config files stay safe to back up or paste into a log.
2. **Infrastructure** (model base URL, model name, protocol endpoint, listening port) is always overridden by the environment.
3. **Behaviour** (persona, whitelists, thresholds, cooldowns, voice, search…) is owned solely by the panel and the database; environment variables are only a **seed for the first deployment**. Once the panel has saved a value, the same-named environment variable no longer applies — the startup log says "the following environment variables are overridden by the panel settings".

### Layer 1: compose (`.env`)

`.env` only serves the `${VAR}` placeholders in `docker-compose.yml`. Two traps:

- **A new variable must also be declared in the compose `environment:` block**, otherwise the container never sees it.
- Do not copy the repository `docker-compose.yml` over the deployment copy — the deployment one may carry hand edits (lose NapCat's `ACCOUNT` and you have to scan the QR code again).

The key list lives in [`.env.example`](.env.example) (commented, the only maintained template). **Bare names without the `BOTAGENT_` / `QQCHAT_` prefix only work for compose substitution** — the host does not read them.

### Layer 2: host (environment variables)

The host reads prefixed names, **canonical `BOTAGENT_*` first, legacy `QQCHAT_*` as fallback** (`EnvironmentAliases.Get` falls through in order). The most common ones:

| Canonical (wins) | Legacy aliases | Meaning |
| :--- | :--- | :--- |
| `BOTAGENT_API_KEY` | `QQCHAT_API_KEY`, `OPENAI_API_KEY` | model key (required, never persisted) |
| `BOTAGENT_BASE_URL` | `QQCHAT_BASE_URL`, `OPENAI_BASE_URL` | model base URL |
| `BOTAGENT_MODEL` | `QQCHAT_MODEL`, `OPENAI_MODEL` | model identifier |
| `BOTAGENT_QQ_ONEBOT_PROTOCOL` | `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` / `ReverseWebSocket` |
| `BOTAGENT_QQ_ONEBOT_URL` | `QQCHAT_ONEBOT_URL` | protocol client WebSocket URL |
| `BOTAGENT_QQ_ONEBOT_TOKEN` | `QQCHAT_ONEBOT_TOKEN` | protocol client auth token |
| `BOTAGENT_QQ_UIN` | `QQCHAT_UIN` | bot account (auto-detected on connect when empty) |
| `BOTAGENT_HEALTH_PORT` | `QQCHAT_HEALTH_PORT`, `HEALTH_PORT` | panel and health-probe port (**18245** native default, 8080 inside the image) |
| `BOTAGENT_HEALTH_BIND` | `QQCHAT_HEALTH_BIND` | bind address (`127.0.0.1` by default, loopback only) |
| `BOTAGENT_PANEL_TOKEN` | `QQCHAT_PANEL_TOKEN` | panel API token |
| — | `QQCHAT_PANEL_PASSWORD` | **initial** panel password: written to the database on first start, changed in the panel afterwards |
| `BOTAGENT_AGENT_TOKEN` | `QQCHAT_AGENT_TOKEN` | server agent token |
| `BOTAGENT_NAPCAT_WEBUI_URL` | `QQCHAT_NAPCAT_WEBUI_URL` | NapCat WebUI URL (QR login in the panel) |

> Panel authentication has exactly two forms — a password and a token. **There is no "disable authentication" switch.**

### Layer 3: source of truth (panel + database)

After initialisation almost every runtime behaviour is changed in the Web panel; saving persists it, applies it live and needs **no restart**: persona and the AI switch, group/private whitelists, reply thresholds and cooldowns, splitting and delays, stickers and pokes, voice and TTS, search, platform instance policies, plugins and MCP, model providers.

Those keys still exist as environment variables in old deployments (`QQCHAT_WHITELIST`, `QQCHAT_PERSONA`, `QQCHAT_GROUP_COOLDOWN`…); they are now only a **first-deployment seed**.

### QQ Official Open Platform (optional)

The official channel's switch and credentials are also host-layer variables (AppId only answers to `QQCHAT_OFFICIAL_APP_ID`, there is no `BOTAGENT_` alias):

| Key | Meaning |
| :--- | :--- |
| `BOTAGENT_OFFICIAL` / `QQCHAT_OFFICIAL` | `1` = enable the official channel |
| `QQCHAT_OFFICIAL_APP_ID` | open-platform AppId |
| `BOTAGENT_OFFICIAL_APP_SECRET` / `QQCHAT_OFFICIAL_APP_SECRET` | AppSecret (environment only, never echoed by the panel) |

Once enabled it is **fully isolated** from the private channel: conversation keys carry an `official:` prefix and whitelists are independent.

The full interface map is in [`docs/api-surface.md`](docs/api-surface.md).

---

## Web Management Console

A high-performance modern Single-Page Application (SPA) built with Vue 3 and TypeScript, communicating with the core service via the ZBA protocol with decoupled static deployment:

- **Consolidated Configuration & Capability Ownership**: The "Basic Configuration" page retains only 7 essential parameters (AI switch, persona, cooldowns, concurrency, context limits); model services, server agent, platform access policies, health reports, and plugin settings are neatly consolidated into corresponding cards and dialogs under "Capabilities", supporting dynamic extension registration.
- **Security Boundaries & In-Memory Sessions**: Bearer tokens are kept strictly in browser memory, cleared upon reload or session lock; secret slots are never echoed back; all mutation paths are protected by CAS versioning, single-flight locks, and unconfirmed operation key reconciliations.
- **Integrated Capability & Plugin Ecosystem**: Visually inspect drivers, plugins, model providers, and MCP; install plugins from the catalog, configure custom OpenAI-compatible providers, test connectivity, and adjust Token Quotas.
- **Dark/Light Theme & Accessibility**: Instant dark/light theme switching with system preference detection; full keyboard navigation support (focus ring trapping, Escape handling), high contrast, and responsive layout for mobile screens.
- **Task Approvals & Real-Time Observability**: Submit and review background Agent tasks with interactive approval gates; observe component health and errors in real-time, with QR code display for easy login when protocol clients disconnect.

---

## SSL/TLS and Reverse Proxy

The host itself **serves plain HTTP only** (`HttpListener`); it **does not terminate TLS and does not generate certificates**. HTTPS in production is the reverse proxy's job (Caddy / Nginx), which also serves the panel frontend under `/web/`.

- **Local self-signed certificate**: `./tools/gencert.sh [domain] [IP]` uses openssl to write a 1095-day self-signed set into `runtime/certs/` (`.crt` / `.key` / `.pfx`) for the proxy or a debug client.
- **Production certificates**: issue them with certbot or acme.sh and reference them from the proxy; certificate paths are **proxy configuration**, not host environment variables.
- **Common misconception**: older docs mentioned two environment variables pointing at certificate paths — the host no longer reads them, and setting them has no effect.

---

## System Architecture

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot Client (NapCat)  | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 10)  |
+-------------------------+                                 |   +-- OneBot Gateway & Dispatch  |
                                                            |   +-- Prompt Pipeline & Decision |
+-------------------------+          Official Open API      |   +-- Profiles & SQLite Storage  |
| QQ Official Platform    | <----------------------------> |   +-- Feishu Webhook Gateway     |
+-------------------------+                                 |   +-- Local Playground Channel   |
                                                            |   +-- Built-in Web Console       |
+-------------------------+          Platform Webhook       |   +-- Platform Policy & Quota    |
| Feishu (Lark) Platform  | <----------------------------> |   +-- Circuit Breaker & Queues   |
+-------------------------+                                 +-----------------+----------------+
                                                                              |
                                                                              v OpenAI-Compatible API
                                                            +----------------------------------+
                                                            | LLM (DeepSeek / OpenAI / Qwen)   |
                                                            +----------------------------------+
```

---

## Compatibility and Data Migration

- **Feishu Identity Mapping**: Runtime persistence maintains `data/feishu-ids-v2.json`, using a distinct ID range (`FeishuBase + 1e12` to `FeishuBase + 2e12`) separated from legacy 32-bit aliases. Back up this file alongside data when enabling Feishu. Legacy conversations are preserved but require reconfiguring native Feishu ID whitelists.
- **OwnMessage Ledger Isolation**: Outgoing messages are partitioned by platform, account, conversation, and native message ID into `own_messages_scoped`. Legacy bare-numeric ID rows remain archived; older versions cannot read scoped entries backwards. Always create a full backup of `data/botagent.db` (legacy `data/qqchat.db` remains supported) prior to upgrading.
- **Tool Call Hard Timeouts**: Hard timeouts enforce the maximum wait duration for the caller and send cancellation signals; they do not guarantee instantaneous termination of underlying external operations, which remain governed by host invocation policies.

---

## Testing and Quality Assurance

The project includes an end-to-end integration test suite and high-frequency safety constraint probes:

```bash
# Run automated unit tests and architectural probes
dotnet test

# Run synthetic integration test suite
dotnet build tests/BotAgent.IntegrationHarness -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net10.0/BotAgent.IntegrationHarness.dll

# Run frontend type checks, unit tests and browser regression (under src/web)
npm run typecheck && npm run test:unit
npm run test:e2e
```

The test suite covers S1–S55 synthetic end-to-end scenarios (whitelists, silent decisions, sentence splitting, long-term memory & profiles, stickers & magic-byte validation, context quoting, hot model reconfiguration, speech synthesis, recall awareness, web search, QR login, Feishu channel isolation, multi-platform policies, daily token quota ledgers, and the official channel through a local fake gateway; S2 is covered inside S1, S33 retired with the pi bridge, and S42 uses local mocks/synthetic credentials only), as well as sub-second architectural ratchets (`ArchitectureProbe`), safety boundaries (`SafetyProbe`), and production specification probes (`ProductionSpecProbe`).

---

## License

This project is licensed under the [MIT License](LICENSE). Third-party protocol clients (such as NapCat) are governed by their respective code licenses and platform policies.