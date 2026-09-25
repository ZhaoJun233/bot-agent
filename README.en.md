# Bot Agent

[简体中文](README.md) | English

**A headless, cross-platform QQ chat bot that runs as a long-lived service**: it attaches to the official QQ client through [NapCat](https://github.com/NapNeko/NapCatQQ) (OneBot v11) and lets any OpenAI-compatible model (DeepSeek / OpenAI / Qwen / Ollama / a self-hosted gateway …) reply automatically in private chats and group chats. A web panel is built in.

> A note on Chinese literals: some inline examples below are Chinese-language strings — prompt templates, in-chat markers such as `[已撤回]` / `〔旁白：…〕`, and settings values — because that is what the bot actually emits. They are kept verbatim so they can be copied as-is.


![.NET](https://img.shields.io/badge/.NET-8.0-blue) ![Platform](https://img.shields.io/badge/Platform-Docker%20%7C%20Linux-green) ![License](https://img.shields.io/badge/License-MIT-orange)

```bash
cp .env.example .env && vim .env    # fill in MODEL_API_KEY and WHITELIST
docker compose up -d                # start napcat + the bot
docker compose logs -f napcat       # first-time QR login (or scan it in the bot panel)
```

Open `http://<host>:8080/` for the control panel: chat history, conversation management, settings, live logs.

## ✨ Features

**Conversation**

- **Automatic AI replies**: the model decides for itself whether to speak, from a "speaking suitability" score (below the threshold → stay silent). Two triggers are supported — "speak on request" and the silent fallback — with a per-conversation serial request queue and parallelism across conversations. The speaking decision has explicit **bottom lines** (no insults, no personal attacks, don't push people out on someone else's behalf, don't punish a whole group for one message).
- **Member profiles**: every speaker gets a profile keyed by QQ number (recent messages plus a long-term persona), so forms of address, tone and running jokes land on the right person.
- **Image understanding**: when someone posts an image it is downloaded, converted to base64 and handed to a multimodal model. QQ image URLs are **temporary links carrying an expiring rkey** (once expired they return 400), so the downloader **caches bytes keyed by URL** (each image is fetched once) and, when a URL has expired, asks the protocol side to **re-issue** it via `get_msg` and fetches the same image again — no more "retry every turn, flood the log, model never sees the image".
- **QQ "reply" quoting**:
  - **Outgoing**: the model itself points at which message it is answering (recent messages are numbered in the prompt). The semantics are pinned to "who are you talking to" — **not** "the material / source you mentioned". When someone repeats or imitates a line (including imitating the bot), the code falls back: if the model points somewhere else, no quote is attached, and with no triggering message there is no quote either — it never credits the wrong person. The log records the quote target (who, plus a snippet) for later review.
  - **Incoming**: when a group member uses QQ's "reply" to quote a message it is recognised — the context gets tagged `[回复 老王「原话」]` (`[回复 你「…」]` when the bot's own line is quoted), so the model no longer has to guess what a bare "me too" is answering. If the quoted target cannot be resolved right now, it is only labelled "an earlier message" — **content is never invented**. A "reply" with an empty body still counts as a message instead of being silently dropped.
- **Listening to music**: when someone shares a track (a NetEase card, a `music` segment or a bare link all work), the bot looks up the title and lyrics, downloads a **low-bitrate** audio file, decodes the waveform itself (loudness / dynamics / tempo / section changes) and then comments using those measured facts — instead of making things up from the title. The audio source is a configurable template string (public sources go down; swap the line). If the audio cannot be fetched it reads the lyrics only and says so honestly.
- **Sentence-by-sentence pacing**: long replies are split on sentence-ending punctuation and sent in batches at typing speed, so they read like a person typing (it will not split `3.14`, `v1.2`, `github.com`, `！！！` or a closing quote).
- **Group member roles**: it knows who the owner is, who the admins are and who has a custom group title (titles are backfilled from the protocol side and cached per person). These enter the prompt as facts — but not for flattery: the prompt explicitly says "don't suck up to rank, and don't use it to push people around".
- **Noise guard**: when the upstream model returns a single character, or parrots its own previous line, the code blocks it outright instead of spamming the group.
- **Optional parenthetical narration** (now called "tag as 〔narration: …〕"): one switch in the panel marks **parenthetical narration** such as 「（笑）」「（bushi）」「行（端在桌上）」. Not a character is lost — it still flows into the chat log and the model context, and the model understands it as a stage direction or expression rather than something the person said. When a whole message is narration it does not trigger a reply on its own (the next real message carries it along). Parentheses in the middle of a sentence, and records of someone posting an emoji (`[表情:斜眼笑]`), are untouched; messages with images, an @-mention of the bot, and private chats are never touched at all.

**QQ-native interactions**

- **Native emoji recognition**: `[表情:微笑]`, `[表情:抠脸]` — the name table is generated from the protocol side's own `face_config.json` (`tools/gen_face_catalog.py`, covering classic and newer emoji), plus animated / super emoji, dice and rock-paper-scissors.
- **Poke**: when poked it answers in context and in character, and can poke back; it does not butt in when other people poke each other (those only enter context); repeated poking from the same person is rate-limited; and poke targets are validated (numbers the model invents are not trusted).
- **Current mood**: mood = "recent poke count (objective) + one line the model writes itself (subjective)". It enters the prompt and colours the tone. When pokes get too frequent the code blocks the poke-back outright, and a mood left stale past a configured age (2 hours by default) expires and falls back.
- **Sticker library (one shared library)**: images posted by members are collected automatically (deduplicated by content hash) → the model generates "a one-line description + emotion / scene keywords" → at reply time candidates are retrieved by context and the model picks one. Ingestion is **moderated** (chat screenshots, ads and text-only images are rejected); sending has a **rate gate** (per-conversation interval, no repeat of the same image); overflow evicts by "least used + longest idle"; the bot periodically self-audits to decide what to delete; and stickers can be imported from the QQ favourites of the logged-in account.
- **Voice messages (optional)**: the model can **occasionally** say a line out loud (the `speak` field in its JSON), which arrives in QQ as a **native voice clip** (with duration, tap to play). Synthesis runs in a separate **cloud TTS sidecar container** (`tools/tts-cloud-server.py`, forwarding to MiniMax or any OpenAI-compatible speech endpoint — no local model, ~20MB RAM). The bot **only hands the `/speak?text=…` URL to the protocol side**; NapCat downloads it, converts it to silk and uploads it — so we touch no audio encoding and push no audio through the WebSocket. Identical text is cached on disk (no paying twice), and a disabled switch, too many characters or a dead TTS all degrade to plain text (nothing is lost).
- **Two inbound channels** (2026-09-21): alongside self-hosted NapCat (private deployment) you can attach the **QQ Open Platform** at the same time (`QQCHAT_OFFICIAL=1` plus appid / secret). The two worlds are **fully isolated** in conversations, context, persona and whitelist (conversation keys carry an `official:` prefix, and Open Platform openids map to alias numbers starting at 8e15). The panel shows them under three tabs — All / Private / Official — so they never bleed together.
- **Recall awareness**: when a member recalls a message (`group_recall` / `friend_recall`), that entry in the context is marked **`[已撤回] original content`** — the content is kept but visibly withdrawn, it can no longer be chosen as a quote target (even if the model insists, the code refuses), and it is not treated as public information to keep discussing. The bot may also gently ask "what did you recall?" (90-second per-conversation cooldown) — but if the other person looks like they merely corrected a typo (recall followed immediately by a new message), it lets it go: no comment, no calling it out.
- **It can see "now"**: the current time (date + weekday + timezone) is injected into the prompt **unconditionally** — the model has no clock of its own, so "what time is it / what's today's date" is answered straight from it. Things that need the network (news / weather / prices / fixtures / release dates / someone's latest status …) get searched, **stable general knowledge does not**, and search terms carry the time information too.
- **Web search**: when the model fills in `search` (what to look up) or `read` (which page to read), the bot actually searches or reads, and hands the results to the next turn as **facts** — rather than improvising from memory. It prefers the **model's own search** (retrieval happens on the provider side and the results carry their sources) and falls back to pluggable search-source templates (SearxNG / MediaWiki / generic HTML). Two searches in the same conversation have a minimum interval (30 seconds by default, adjustable in the panel, `0` = unlimited), and retrieved material is **spoken in its own voice, continuing the thread** rather than read out as a report. If the search or read fails, it says so honestly.
- **Participation gate / asking questions / human approval (three switches, all off by default)**: with the participation gate on, when the state machine says "not this round" (observing / exiting / cooling down) the model is not called and nothing is said — it is **receive-only** (an @-mention is still answered as usual), and the panel shows each conversation's state and reason live. With asking enabled, the model may ask a question carrying a **one-time code** (the code and its short expiry are issued server-side and expire unusable — asking grants no permissions). With human approval on, when the model "wants to call a tool" it first posts a **pending confirmation** in the group, and only executes it after the owner / an admin (or someone named in the panel) replies "同意 XXXX" — identity / conversation / expiry / one-time use / policy version are all checked server-side, and it **only executes a fixed server-side fake tool** (no shell / file / process control; the receipt says so explicitly). All three off = byte-for-byte the previous behaviour.
- **Scenario presets**: `on-demand` (reply only to explicit mentions) / `research` (allow restricted web access) / `social` (allow extra actions: voice / stickers / poke) — expressed as configuration differences that **only tighten, never loosen** (they are intersected with the existing switches); an unrecognised name gets the lowest permissions. Left empty = the capability allowlist is decided entirely by the existing switches (exactly as before).

**Operations**

- **Web panel**: chat history and conversation management, settings, live logs (**refreshing the page does not clear them**: the first screen backfills the last 300 lines from `/api/logs`, after which SSE appends in real time; logs from before a restart are still visible after the process restarts; there are **↑ top / ↓ bottom** one-click jump buttons above the log box), plus mobile support. The settings page has a **section rail on the left and shows one section at a time** (a dozen cards no longer pile up; a final "show all" lists them in a single column; refreshing or sharing a link returns to the same section; card descriptions collapse to one line by default, with "expand" for the full text). **QR login inside the panel** (when the account is not logged in the QR code appears directly — no need to find NapCat's own entry point).
- **Three new read-only panel blocks**: **tool catalogue** (`GET /api/tools`: one catalogue shared by three channels — chat 10 / QQ actions 10 / server 6 = **26 entries** — plus four self-checks; `healthy:true` means nothing is inconsistent);
  **trace page** (`GET /api/traces`: one trace per turn with **six nodes** 〈participation decision / context assembly / model decision / tool gate / tool execution / sanitised send〉, exposing only **shape** 〈status code / reason code / duration / count / tool name〉 and **no message text**, keeping the last 50 turns in memory);
  **health dashboard** (`GET /api/dashboard`: active conversations / in-flight and queued / average latency / memory and load / tool count / trace count — a screenful of numbers, inventing no new statistics). The only write path on the trace page is an **approval decision** (two fail-closed preconditions; the verdict reuses the very same validation as in-group approval, relaxed nowhere).
- **Model endpoint editable in the panel**: Base URL / model name / API key take effect the moment they are saved in settings — no `.env` edit, no restart. The key is stored separately in the `secrets` table of `data/qqchat.db` (database file mode 600) instead of being mixed with the rest of the configuration, and the UI only echoes a mask.
- **Observability**: `/healthz` `/readyz` `/status` plus a container `HEALTHCHECK`; a QQ disconnect is reported proactively (a live WebSocket alone cannot detect an invalidated session); occasional upstream 5xx (`No capacity` / `auth_unavailable`) triggers a **2-second backoff and one retry** instead of dropping that turn's reply.
- **Data in SQLite**: conversations / messages / member profiles / personas / mood / songs heard / sticker index / secrets all live in a single `data/qqchat.db` (WAL).
  Benefits: a crash can no longer write "the conversation but not the profile" (same database, same transaction); messages are append-only data rather than a full JSON rewrite on every change; and the panel's "earlier messages in this group / one person's persona in one group / dig through the archive" are each one SQL statement.
  Old JSON data is **imported automatically** on first start and moved to `legacy-json/` for the record (not deleted).
- **`//` tasks (server agent)**: dispatch work in a group or private chat with the `//` prefix (the prefix defaults to `//`; an empty user allowlist means nobody can use it; the token is configured in the panel). Results return to the conversation they came from. The server side has built-in tools (bash / file read-write / search / docker / QQ actions — **docker and dangerous actions are off by default**), and the server agent can have its own model endpoint and key. Context is **isolated per task** by default (`//接着` continues the previous one). **Every `//` step can also pass through the unified tool gate** (`QQCHAT_AGENT_SERVER_GATE`, off by default; once on, the verdict matches the old allowlist, high-risk exceptions can only be named by tool name, and the approval branch still does not accept high-risk ones).
- **A bounded stepping loop on the chat side** (`QQCHAT_MAX_AGENT_STEPS`, default `1` = byte-for-byte the previous behaviour): raised, the model can "run read-only tools (web search / page read) **on the spot**, then ask once more", capped at 3 — if a tool yields nothing it does not spin. Voice / stickers / pokes / sharing do not run inside the loop (they disturb other people and still go through the unified verdict at the send stage), and the group only ever receives that final line.
- **Local HTTP channel** (`QQCHAT_LOCAL_CHANNEL_IDS`, empty by default = off): besides private and official there is a third `IQqChatSource` (`POST /api/local/message` injects a message; replies land in an in-memory outbox and never touch the network). An **empty list blocks everything** (deliberately unlike the official channel's "empty = accept all"); you write short ids (`1`, `2`, `1001`) and the server maps them to a dedicated **7e15-onward** number range — used to prove that "**the access layer is swappable while the core and governance stay untouched**".
- **Daily health report**: once a day (18:00 by default) a private status line (memory / load / conversation count / queues / voice connectivity), **purely server-side**, depending on no external device.
- **One-click deploy from the panel**: upload an artefact or paste a URL → the server rebuilds the image and replaces the container itself; a rollback point (`qqchat-agent:prev`) is taken automatically before deploying, and high-privilege switches are off by default.
- **Masking on by default**: group names / nicknames / QQ numbers keep only the first 3 and last 2 characters in the panel and conversation lists (`QQCHAT_AGENT_MASK`, **on by default**); **storage and keys are untouched**, so commands and panel buttons all keep working.
- **Layered configuration**: environment variables cover deployment (protocol address, token, mount points); the panel owns behaviour (persona, whitelist, cooldowns, thresholds …) and is its single owner (stored in `data/qqchat.db`).

## 🏗️ Architecture

```
QQ client (QQNT, the official Linux build inside a container)
   ▲ injected into
NapCat container ── OneBot v11 forward WS ──┐
                                            ▼
                              this service (BotAgent.Headless)
                              ├── OneBot gateway (messages / actions / events)
                              ├── Agent (prompt assembly, model calls, speaking decisions)
                              ├── member profiles / conversation persistence (one SQLite DB, qqchat.db)
                              └── web panel + health checks (minimal HttpListener, no ASP.NET)
                                            │
                                            ▼
                              OpenAI-compatible API (DeepSeek / Qwen / self-hosted gateway …)
```

| Module | Approach |
| --- | --- |
| QQ channel | [NapCat](https://github.com/NapNeko/NapCatQQ) → OneBot v11 (forward / reverse WebSocket, or HTTP) |
| AI brain | OpenAI-compatible Chat Completions (including multimodal image understanding) |
| Persistence | `/data/qqchat.db`: a single **SQLite** database (settings, conversations, messages + archive, member profiles and personas, mood, songs heard, sticker index, secrets); sticker images themselves stay in `stickers/` |
| Speech synthesis | A separate container (a **cloud TTS proxy**: `tools/tts-cloud-server.py` + `tools/tts-cloud.Dockerfile`, ~60MB image / ~20MB RAM, **no local model**) — if it dies only voice is affected and the bot degrades to text |
| Web search | Prefers the model provider's own web retrieval (`/v1beta/…:generateContent` with a search tool, results carrying sources); falls back to pluggable sources (SearxNG JSON / MediaWiki JSON / generic HTML) |
| Health checks | A built-in minimal HTTP service: `/healthz` `/readyz` `/status` |

## 📚 Documentation

| Document | Contents |
| --- | --- |
| [src/BotAgent.Headless/README.en.md](src/BotAgent.Headless/README.en.md) ([中文](src/BotAgent.Headless/README.md)) | **Deployment and operations**: the full environment-variable reference, data directory, panel usage, troubleshooting, design trade-offs and operating boundaries |
| [.env.example](.env.example) | Every configurable option, with commentary (including Docker secrets usage; comments are in Chinese) |

## 🧪 Tests

The repo ships a **genuinely end-to-end** integration suite (it starts a real bot process, a real-WebSocket fake protocol side and a real-HTTP fake model):

```bash
dotnet build src/BotAgent.Headless -c Release
dotnet build tests/BotAgent.IntegrationHarness -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
```

It covers 48 scenario blocks (S1–S48: whitelist / silence / sentence splitting / memory / profiles / hot settings reload / disconnect and QR login / stickers / quoting in both directions / parenthetical narration / small emoji and pokes / hot model reconfiguration / music / links and forwards / voice / recall / web search and time / panel logs / image download (rkey expiry and caching) / burst re-evaluation / data migration / member roles / emotional companionship and proactive openers / server agent (`//` tasks · context hygiene · docker permissions · daily health report) / human approval (including approval and rejection from the panel) / participation state machine and questions / one-click panel deploy / panel tool catalogue and traces / bounded stepping loop / third channel),
plus a set of **sub-second probes** (no database, no network): `ArchitectureProbe` (architecture ratchet **92/0**), `SafetyProbe` (mechanisms and safety boundaries **341/0**),
`ParticipationProbe` (48/0), `PipelineEval` (isolated evaluation 68/68), `FrontendProbe` (panel static + runtime **230/0**).
The full harness has **729** assertions (measured 2026-09-24: **728 pass / 1 pre-existing soft warning line** — the "system prompt < 4000 characters" warning line, measuring ~4364 characters; **it is a sentinel, not a target, so don't move the threshold**). A single scenario can be run alone with `QQCHAT_IT_ONLY=s32`.

> Note: the test project does not reference the bot project, so **after changing bot code you must build it separately**, otherwise the old DLL is what runs.

## 📄 License

The source code of this program is MIT. NapCat itself is under its own licence (non-commercial) — please respect it when using it.
