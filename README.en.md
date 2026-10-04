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
- **Unified Platform Policy Governance**: Supports QQ Private, QQ Official Open Platform, Feishu Bot, and Local channel. The settings "Platform Instance Policy" section is the single entry point for each platform's enable and chat switches, group/private whitelists, and feature overrides. Chat mute preserves connectivity; disabling a platform blocks chat. Credentials, connection state, whitelists, and the global AI switch remain independent safety gates.
- **Daily Token Quota Ledger**: Manages UTC daily budgets isolated by platform and conversation tenant (`SourceKey`), supporting platform-based tenant filtering, real-time usage monitoring, and automatic power-saving silence when quotas are exhausted.
- **Zero-Dependency Local Playground Channel**: Features an in-process standalone testing channel. Inject test messages via the Web interactive playground (`/playground.html`) or `POST /api/local/message`, traversing the full model decision pipeline and six-stage governance timeline.

---

## Quick Start

### Method 1: Native Linux Execution (Recommended, No Docker Required)

#### 1. System Requirements & Environment Setup
- **Operating System**: Standard Linux distribution (Ubuntu 20.04+, Debian 11+, CentOS 8+, Arch Linux, etc.)
- **Runtime Dependencies**: .NET 8.0 SDK / Runtime, Git, curl, openssl
- **OneBot Protocol Client**: [NapCat Linux Shell](https://github.com/NapNeko/NapCatQQ) (if using QQ Private Channel)

Install base dependencies on Debian / Ubuntu:

```bash
# Update package lists and install prerequisites
sudo apt-get update
sudo apt-get install -y dotnet-sdk-8.0 git curl openssl
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

Once the service starts, open `http://127.0.0.1:8080/` in your browser to access the Web management panel.

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

Core configuration is stored in the `.env` file in the project root.

### Core Parameters

| Variable | Example | Description |
| :--- | :--- | :--- |
| `MODEL_API_KEY` | `sk-...` | API Key for the OpenAI-compatible endpoint (Required) |
| `MODEL_BASE_URL` | `https://api.deepseek.com/v1` | Base URL for model invocations |
| `MODEL_NAME` | `deepseek-chat` | Model identifier |
| `MAX_TOKENS` | `2048` | Max output tokens per reply |
| `PANEL_PASSWORD` | `your_password_here` | Web console access password (Optional; leave empty for password-free access; can be changed/cleared anytime in panel) |
| `DISABLE_PANEL_AUTH` | `1` | Force disable web console authentication (Optional, default `0`) |
| `HEALTH_PORT` | `8080` | Listening port for web console and health endpoints |
| `WHITELIST` | `*` or `123456,789012` | QQ groups or users permitted to receive replies (comma-separated, `*` for all) |
| `ONEBOT_PROTOCOL` | `ForwardWebSocket` | Protocol client communication mode (`ForwardWebSocket` / `ReverseWebSocket`) |
| `ONEBOT_URL` | `ws://127.0.0.1:3001` | Protocol client WebSocket connection URL |
| `ONEBOT_TOKEN` | `your_token` | Protocol client auth token (if configured) |
| `BOT_UIN` | `10001` | Bot QQ number (auto-detected on connection if left empty) |
| `QQCHAT_TLS_CERT` | `/etc/letsencrypt/live/.../fullchain.pem` | Custom TLS/HTTPS certificate chain path (Optional, supports Let's Encrypt) |
| `QQCHAT_TLS_KEY` | `/etc/letsencrypt/live/.../privkey.pem` | Custom TLS/HTTPS certificate private key path (Optional) |

### QQ Official Open Platform (Optional)

To enable the official Bot API:

```ini
OFFICIAL_ENABLED=1
OFFICIAL_APP_ID=your_app_id
OFFICIAL_APP_SECRET=your_app_secret
```

> [!NOTE]
> After process initialization, most runtime behaviors (such as bot persona, reply thresholds, cooldowns, sticker toggles, etc.) can be dynamically adjusted and persisted online via the Web console without restarting the service.

---

## Web Control Panel & Mobile Experience

Access `http://<Server_IP>:8080/` to directly open the control panel:

- **Password-Free Access & Security Governance**: The console can be used immediately without forcing password setup; users can set a protection password or clear an existing password anytime on the Settings page.
- **Two-Level Categorized Navigation**: Settings are aggregated into 5 major categories (Channels, Models & Agent, Chat & Interaction, Voice & Search, Other), featuring collapsible accordion sections on desktop and linked dual-row pills on mobile.
- **Mobile Adaptation & Liquid Glass Navigation**: Deeply optimized for smartphone screens and touch controls. The bottom navigation bar employs an Apple-inspired Liquid Glass floating capsule Dock with full support for iOS/Android Safe Areas. Dedicated avoidance save bars in settings and auto-hiding during keyboard input prevent touch obstructions.
- **Full-Stack Performance Optimization**:
  - **In-Memory Asset Cache**: Static web assets are cached in memory to eliminate filesystem and assembly reflection overhead.
  - **Dynamic GZip Compression**: Dynamically applies GZip compression to text assets (HTML, JS, CSS, JSON, SVG), reducing payload size by ~70%.
  - **Hardware Acceleration & Render Containment**: Utilizes CSS Containment, GPU layer promotion (`translateZ`), and native momentum scrolling for smooth 60fps lists.
- **Session Monitoring & Context Stacks**: Visualizes group and private chat message flows, user profiles, and context stacks in dialogue bubbles in real time.
- **Hot Parameter Tuning & Model Switching**: Adjust interaction eagerness, response thresholds, cooldown intervals, API endpoints, and model configurations on the fly with immediate effect.
- **Observability & QR Login**: Real-time status inspection via `/healthz`, `/readyz`, and metrics; renders QR login codes directly when the protocol client is offline.
- **Live SSE Log Streaming**: Server-Sent Events (SSE) provide continuous log streaming with instant historical backfilling on initial load.

---

## SSL/TLS Security & Certificate Management

The service includes built-in X.509 certificate management for Linux platforms, supporting out-of-the-box self-signed certificates and standard Let's Encrypt certificate chains:

- **Automatic Self-Signed Certificate Generation**: If no certificate is detected at startup, the service automatically generates a standard self-signed certificate valid for 3 years, outputting to `runtime/certs/`:
  - `runtime/certs/botagent.crt`: X.509 Public Certificate (PEM)
  - `runtime/certs/botagent.key`: RSA-2048 Private Key (PEM)
  - `runtime/certs/botagent.pfx`: PKCS#12 Keystore
  You can also run `./tools/gencert.sh [domain] [IP]` to manually generate certificates with custom SAN extensions.
- **Let's Encrypt / Production Certificate Loading**: If certificates are issued via certbot or acme.sh, mount them by specifying paths in `.env`:
  ```ini
  QQCHAT_TLS_CERT=/etc/letsencrypt/live/example.com/fullchain.pem
  QQCHAT_TLS_KEY=/etc/letsencrypt/live/example.com/privkey.pem
  ```

---

## System Architecture

```text
+-------------------------+            OneBot v11            +----------------------------------+
| OneBot Client (NapCat)  | <---- [Forward WS / WS] ----> | BotAgent.Headless (C# / .NET 8)  |
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
- **OwnMessage Ledger Isolation**: Outgoing messages are partitioned by platform, account, conversation, and native message ID into `own_messages_scoped`. Legacy bare-numeric ID rows remain archived; older versions cannot read scoped entries backwards. Always create a full backup of `data/qqchat.db` prior to upgrading.
- **Tool Call Hard Timeouts**: Hard timeouts enforce the maximum wait duration for the caller and send cancellation signals; they do not guarantee instantaneous termination of underlying external operations, which remain governed by host invocation policies.

---

## Testing and Quality Assurance

The project includes an end-to-end integration test suite and high-frequency safety constraint probes:

```bash
# Run automated unit tests and architectural probes
dotnet test

# Run synthetic integration test suite
dotnet build tests/BotAgent.IntegrationHarness -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll

# Run frontend static and runtime probes
node tests/BotAgent.FrontendProbe/probe.mjs
```

The test suite covers S1–S51 real end-to-end scenarios (whitelists, silent decisions, sentence splitting, long-term memory & profiles, stickers & magic-byte validation, context quoting, hot model reconfiguration, speech synthesis, recall awareness, web search, QR login, Feishu channel isolation, multi-platform policies, daily token quota ledgers, etc.), as well as sub-second architectural ratchets (`ArchitectureProbe`), safety boundaries (`SafetyProbe`), and production specification probes (`ProductionSpecProbe`).

---

## License

This project is licensed under the [MIT License](LICENSE). Third-party protocol clients (such as NapCat) are governed by their respective code licenses and platform policies.
