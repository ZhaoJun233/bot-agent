# Bot Agent Headless (container edition)

[简体中文](README.md) | English

A **headless, cross-platform, container-deployable** QQ chat bot service: it connects to an OneBot v11 protocol side (NapCat and friends) and uses an OpenAI-compatible model to reply automatically in QQ private chats and group chats, with a web panel built in.

> A note on Chinese literals: some inline examples below are Chinese-language strings — prompt templates, in-chat markers such as `[已撤回]` / `〔旁白：…〕`, and settings values — because that is what the bot actually emits. They are kept verbatim so they can be copied as-is.


```
QQ client (Linux)  ←injected—  NapCat (container)  ←—OneBot v11 WS——  Bot Agent (container)  ——→  model API
```

- **No UI, no Windows dependency**: `net8.0`, Docker image around 80MB (the runtime stage is based on `dotnet/runtime:8.0`)
- **Runs as non-root**, keeps all data in the `/data` volume, and shuts down gracefully (SIGTERM / `docker stop`)
- **Health checks built in**: `/healthz` (liveness), `/readyz` (is the protocol side connected), `/status` (runtime state as JSON)

---

## Quick start (Docker Compose)

```bash
# 1. Prepare configuration
cp .env.example .env
vim .env                      # at minimum fill in MODEL_API_KEY and WHITELIST

# 2. Build + start
docker compose up -d

# 3. First login: scan the QR code
#    Recommended: open the bot panel (http://<host>:8080/) — when not logged in, the QR code appears at the top
#    Fallback: read the ASCII QR code in the NapCat log
docker compose logs -f napcat
```

After scanning the QR code you still need to tell NapCat to forward messages (**one-time setup**):

1. Open `http://<host-ip>:6099` in a browser → NapCat WebUI
2. **Network configuration → add a "WebSocket client" (forward WS)**
   - URL: `ws://qqchat:3001`
   - Token: keep it identical to `ONEBOT_TOKEN` in `.env` (if left empty, leave both empty)

   > Containers resolve each other by service name, so this must be `qqchat` (the compose service name), not `127.0.0.1`.
3. Back on the host, verify:

```bash
curl -s http://127.0.0.1:8080/readyz       # {"ready":true,...} means it is connected
curl -s http://127.0.0.1:8080/status       # detailed runtime state
```

Then send an `@bot hello` into a whitelisted group.

---

## Environment variables

Configuration is either **changed in the panel** (stored in `data/qqchat.db`, see below) or supplied through environment variables — environment variables act as a seed **on first start only**. After that, anything changed in the panel wins (further changes to the environment variable have no effect, and the bot lists them at startup to remind you).
Variables with a `_FILE` suffix read their value from a file, which suits Docker secrets:

```yaml
environment:
  QQCHAT_API_KEY_FILE: /run/secrets/model_key
secrets:
  model_key:
    file: ./model_key.txt
```

### Model (OpenAI-compatible)

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_API_KEY` / `OPENAI_API_KEY` | — | **Required.** `QQCHAT_API_KEY_FILE` is also supported; it can also be filled in from the panel (stored in the `secrets` table, file mode 600); once set in the panel it wins |
| `QQCHAT_BASE_URL` / `OPENAI_BASE_URL` | `https://api.openai.com/v1` | Endpoint (DeepSeek / Qwen / Ollama / a gateway …); editable under "Agent brain" in the panel and effective immediately; once changed in the panel, the environment variable no longer overrides it |
| `QQCHAT_MODEL` / `OPENAI_MODEL` | `gpt-4o-mini` | Model name; editable in the panel |
| `QQCHAT_MAX_TOKENS` | `2048` | Upper bound for a single reply |

### QQ channel

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_ONEBOT_PROTOCOL` | `ForwardWebSocket` | `ForwardWebSocket` / `ReverseWebSocket` / `Http` |
| `QQCHAT_ONEBOT_URL` | `ws://127.0.0.1:3001` | Forward: `ws://napcat:3001`; reverse: `http://0.0.0.0:3001`; HTTP: `http://napcat:3000` |
| `QQCHAT_ONEBOT_TOKEN` | empty | Protocol-side access token |
| `QQCHAT_UIN` | empty | The bot's QQ number (used to recognise `@bot`; if empty it is fetched automatically once connected) |

### Behaviour

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_WHITELIST` | empty | ⚠️ **Empty = strict mode, all messages ignored.** Put group / QQ numbers (comma- or newline-separated), or `*` to accept everything |
| `QQCHAT_PERSONA` | empty | The bot's persona (personality / speaking style), injected into every request |
| `QQCHAT_AI_DESIRE` | `50` | Conversational desire, 0-100; higher means it joins in more readily |
| `QQCHAT_SUITABILITY_THRESHOLD` | `10` | Speaking-suitability threshold; below it the bot stays silent |
| `QQCHAT_AI_MODE` | `1` | `0` = receive messages without replying (for debugging) |

### Reply pacing

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_GROUP_COOLDOWN` | `8` | Minimum interval between replies in the same group (seconds) |
| `QQCHAT_PRIVATE_COOLDOWN` | `3` | Minimum interval between replies in the same private chat (seconds) |
| `QQCHAT_IDLE_FALLBACK` | `60` | Silent fallback: re-evaluate once after this many seconds without an explicit request; `0` disables it |
| `QQCHAT_SPLIT_REPLIES` | `1` | Split long replies on sentence boundaries (at most 4 parts, nothing lost; it will not split decimals / domain names / repeated punctuation / closing quotes) |
| `QQCHAT_IGNORE_BRACKETS` | `0` | `1` = **tag** parenthetical narration in members' messages as `〔旁白：…〕` (this is tagging, not ignoring): a message that is entirely narration (「（笑）」→`〔旁白：笑〕`) still enters the chat log and the model context but **does not trigger a reply on its own** (the next real message carries it); with narration on both sides (「行（端在桌上）」→「行〔旁白：端在桌上〕」) the body is used as usual. Parentheses in the middle of a sentence and pure numbers such as 「（2026）」 are untouched; records of members posting emoji (`[表情:斜眼笑]` / `[图片]`) are not treated as narration; messages with images, an @-mention of the bot, and private chats are never touched |
| `QQCHAT_SEGMENT_DELAY_MS` | `700` | Delay between split sentences |
| `QQCHAT_MAX_CONTEXT` | `200` | Maximum number of context entries fed to the model |
| `QQCHAT_PROFILE_LOOKUP` | `8` | Upper bound on the number of member profiles attached |

### Operations

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_DATA_DIR` | `/data` (inside the image) | Data directory; mounting a volume is recommended |
| `QQCHAT_HEALTH_PORT` | `8080` | Health-check port; `0` disables it |
| `QQCHAT_HEALTH_BIND` | `+` | Bind address; falls back to `127.0.0.1` automatically on Windows without administrator rights |
| `QQCHAT_VERBOSE` | `1` | `0` = log only the essentials |
| `QQCHAT_LOG_FILE` | `1` | `0` = do not write a log file, stdout only |
| `QQCHAT_NAPCAT_WEBUI_URL` | `http://napcat:6099` | NapCat WebUI address (used for QR login from the panel) |
| `QQCHAT_NAPCAT_WEBUI_TOKEN` | empty | NapCat WebUI token (the `token` in `napcat/config/webui.json`). Once set, the panel shows the login QR code directly while the account is logged out |
| `QQCHAT_STICKERS` | `1` | Master switch for stickers (automatic collection + context-based sending + self-audit) |
| `QQCHAT_STICKER_MAX` | `120` | Sticker library size limit (images) |
| `QQCHAT_STICKER_CANDIDATES` | `6` | How many candidates are shown to the model each time |
| `QQCHAT_STICKER_CURATE_INTERVAL` | `3600` | Self-audit interval (seconds); 0 = off |
| `QQCHAT_STICKER_COOLDOWN` | `120` | Minimum interval between two stickers in the same conversation (seconds); 0 = unlimited |
| `QQCHAT_ENABLE_POKE` | `1` | Master switch for poke: answer / poke back in context when poked (other people poking each other only enters context, no butting in) |
| `QQCHAT_POKE_COOLDOWN` | `45` | Poke cooldown (seconds): repeated pokes from the same person are answered once; also the minimum interval for poking someone proactively |
| `QQCHAT_MOOD_TTL` | `7200` | How long a mood written by the model is kept (seconds): when stale it falls back to "described automatically from the poke count"; 0 = never expires |
| `QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS` | `0` | `1` = allow downloading images from private / loopback addresses. **Self-hosted / testing only** — do not enable this on a public deployment |
| `QQCHAT_ENABLE_VOICE` | `0` | Master switch for voice messages (sent only when the model fills in the `speak` field) — the `tts` sidecar container must be running first |
| `QQCHAT_VOICE` | `zh_CN-huayan-medium` | Voice (`tts/<name>.onnx`): `huayan-medium/x_low`, `xiao_ya-medium`, `chaowen-medium` |
| `QQCHAT_VOICE_SPEED` | `100` | Speaking rate as a percentage (100 = normal, higher is faster) |
| `QQCHAT_VOICE_MAX_CHARS` | `80` | Maximum characters per voice message (beyond this it sends text instead) |
| `QQCHAT_TTS_URL` | `http://tts:5000` | TTS sidecar address (the bot composes `/speak?text=…`; NapCat downloads it) |
| `QQCHAT_WEB_SEARCH` | `1` | Master switch for web search (used only when the model fills in `search` / `read`) |
| `QQCHAT_SEARCH_USE_MODEL` | `1` | Prefer "the model's own search" (retrieval happens on the provider side and results carry sources); set to `0` to use only the search-source templates below |
| `QQCHAT_SEARCH_SOURCES` | Wikipedia API | Fallback search-source templates (one `name\|url` per line, `{q}` is the query; `searx*` / `wiki*` get dedicated parsers) |
| `QQCHAT_SEARCH_MAX_RESULTS` | `5` | How many results are shown to the model each time |
| `QQCHAT_SEARCH_COOLDOWN` | `30` | Minimum interval between two web searches in the same conversation (seconds); `0` = unlimited |
| `QQCHAT_SEARCH_TIMEOUT` | `20` | Search / page-read timeout (seconds) |
| `QQCHAT_SEARCH_READ_CHARS` | `1800` | Truncation length for text fetched by `read` (characters) |
| `TZ` | `Asia/Shanghai` | Affects message timestamps and the "what time is it now" the model sees |

### Agent runtime (three switches, all off by default)

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_MAX_AGENT_STEPS` | `1` | Upper bound on **the bounded stepping loop on the chat side** (a 1–3 slider in the panel, clamped once more in code). The default of 1 is **byte-for-byte** the previous behaviour; raised, the model can "run read-only tools (web search / page read) on the spot, then ask once more" — still read-only, and voice / stickers / pokes / sharing still go through the unified verdict at the send stage; if a tool yields nothing it does not spin |
| `QQCHAT_AGENT_SERVER_GATE` | `0` | `1` = **every `//` step also passes through the unified tool gate** (registry + this turn's policy snapshot + category denials). Once on, the verdict matches the old allowlist; off = zero behaviour change. High-risk exceptions can only be named by **tool name** (`ToolPolicy.HighRiskExceptions`; listing `bash` does not incidentally open up `docker`), and the approval branch still does not accept high-risk ones |
| `QQCHAT_LOCAL_CHANNEL_IDS` | empty | The roster for the **local HTTP channel** (the third `IQqChatSource` implementation). **Empty = the whole channel is not built** (deliberately unlike the official channel's "empty = accept all"); write **short ids** (`1`, `2`, `1001`) and the server maps them to a dedicated **7e15-onward** number range; anything out of range (below 1 or above 1e12) is rejected outright — better a 400 than a collision with the official number range |

> ⚠ **Current state of the panel controls (recorded honestly)**: of these three, only *the chat step limit* has a slider in the panel; the other two have **no dedicated switch control** — change them through **environment variables** (passed through by the `docker-compose.yml` above) or the panel save endpoint `POST /api/settings`
> (`{"agentServerUseGate":true}` / `{"localChannelIds":"1"}`); the current values are visible in the settings echo in the panel.
> Changing the local channel roster **requires a container restart to take effect** (channels are built from the roster at startup).

> The entry point for the third switch is `POST /api/local/message` (inject a local message; replies land in an in-memory outbox): two **fail-closed** preconditions — an empty roster gives `403 local_channel_disabled`; no panel token configured gives `403 panel_token_required`.

> Why QR login from the panel needs a token: the bot fetches the QR code from NapCat WebUI's public endpoints
> (`/api/auth/login` + `/api/QQLogin/GetQQLoginQrcode`) using the same authentication as NapCat's own frontend,
> so no extra NapCat configuration is needed. Leaving the token empty only hides the QR code in the panel; it does not affect the message channel.

---

## Data directory (`/data`)

| Path | Contents |
| --- | --- |
| `data/qqchat.db` | **SQLite database**: settings, conversations, messages (including the archive), member profiles and personas, mood, songs heard, sticker index, secrets. WAL mode, accompanied by `-wal` / `-shm` |
| `data/legacy-json/` | Archive of old JSON data (moved here after the automatic import on first start; **not deleted**) |
| `stickers/*.png` | **Sticker image data** (the index is in the database; binary data does not belong in there — backup, preview and cleanup all get awkward) |
| `logs/qqchat.log` | Runtime log (the panel log page and `/api/logs` are just its tail; ⚠ it currently **does not rotate**, so it keeps growing) |

> Why SQLite replaced "a pile of JSON": ① conversations / messages / profiles were spread across several files, a single crash could write only half of them, and cross-file transactions were impossible; ② messages are append-only, while JSON rewrote everything each time (noticeably slow at a few thousand entries); ③ the panel's "earlier messages in this group / one person's persona in one group / dig through the archive" could only be done by loading everything into memory and filtering, whereas in SQL it is one statement.
>
> Backup: just copy `data/qqchat.db` (together with `-wal` / `-shm`); to inspect settings with SQL, `json_extract(json,'$.AiDesire')` works.
> Secrets (API keys entered in the panel) live in the `secrets` table, which is why **the database file mode is 600** (kept out of `settings`, so it never gets pasted out along with the configuration).

JSON inside the database never escapes Chinese characters, so `sqlite3 ... "SELECT text FROM messages LIMIT 3"` is readable as-is.

---

## Plain docker commands without Compose

```bash
docker build -f src/BotAgent.Headless/Dockerfile -t qqchat-agent .

docker run -d --name qqchat --restart unless-stopped \
  -e QQCHAT_API_KEY=sk-xxxx \
  -e QQCHAT_BASE_URL=https://api.deepseek.com/v1 \
  -e QQCHAT_MODEL=deepseek-chat \
  -e QQCHAT_ONEBOT_URL=ws://172.17.0.1:3001 \
  -e QQCHAT_ONEBOT_TOKEN=your-token \
  -e QQCHAT_WHITELIST=123456789 \
  -e QQCHAT_UIN=10001 \
  -e QQCHAT_PERSONA='你是群里的老群友，说话简短口语化。' \
  -v ./data:/data \
  -p 127.0.0.1:8080:8080 \
  qqchat-agent
```

> With `--network host`, `QQCHAT_ONEBOT_URL` can simply be `ws://127.0.0.1:3001`;
> on the default bridge network use `host.docker.internal` (Docker Desktop / macOS) or the host's LAN IP (Linux).

---

## Without Docker (running .NET directly)

```bash
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release

# see every option
dotnet run --project src/BotAgent.Headless -- --help

# run it
QQCHAT_API_KEY=sk-xxxx \
QQCHAT_ONEBOT_URL=ws://127.0.0.1:3001 \
QQCHAT_WHITELIST=123456789 \
dotnet run --project src/BotAgent.Headless
```

A typical reverse-WS setup (this program listens on 3001 and NapCat dials in):

```bash
QQCHAT_ONEBOT_PROTOCOL=ReverseWebSocket \
QQCHAT_ONEBOT_URL=http://0.0.0.0:3001 \
QQCHAT_ONEBOT_TOKEN=your-token \
dotnet run --project src/BotAgent.Headless
```

---

## Health checks and observability

```bash
curl -i http://127.0.0.1:8080/healthz    # 200: the process is alive
curl -i http://127.0.0.1:8080/readyz     # 200: connected to the protocol side; 503: not connected
curl -s  http://127.0.0.1:8080/status    # state snapshot
curl -s  http://127.0.0.1:8080/api/logs  # the last 300 runtime log lines (the panel log page uses this for its first screen)
curl -s  http://127.0.0.1:8080/api/qqlogin   # the current login QR code (used by the QR card in the panel)
```

A `/status` sample:

```json
{
  "status": "running",
  "uptimeSeconds": 3721,
  "onebot": { "connected": true, "protocol": "ForwardWebSocket", "address": "ws://napcat:3001" },
  "account": { "uin": "10001", "selfId": 10001, "online": true },
  "login": { "qrAvailable": true, "napcatWebUi": "http://napcat:6099" },
  "agent": { "enabled": true, "model": "deepseek-chat", "desire": 50, "personaConfigured": true },
  "conversations": 7
}
```

The image has a built-in `HEALTHCHECK` (it calls `--health`, so it does not depend on curl), which means `docker ps` shows `healthy` directly:

```bash
docker inspect --format '{{.State.Health.Status}}' qqchat-bot
```

---

## Panel: tool catalogue / trace page / dashboard (read-only)

All three are **read-only**, answering "what can the bot do right now / what did that last turn go through / how loaded is this machine":

| Page / block | Data source | What it shows |
| --- | --- | --- |
| Tool catalogue | `GET /api/tools` | One catalogue shared by three channels (chat 10 / QQ actions 10 / server 6 = **26 entries**), plus four self-checks (duplicate ids / missing executor / missing high-risk exception / superfluous executor) — `healthy:true` means nothing is inconsistent |
| Trace page | `GET /api/traces` | One trace per turn with **six nodes** (participation decision / context assembly / model decision / tool gate / tool execution / sanitised send); only **shape** is exposed (status code / reason code / duration / count / tool name) and **no message text**; the last **50 turns** are kept in memory and a restart clears them |
| Health dashboard | `GET /api/dashboard` | Active conversations / in-flight and queued / average latency / memory and load / current tool count / trace count — a screenful of numbers, inventing no new statistics |

> The only **write path** on the trace page is an approval decision (`POST /api/approvals/decide`): two fail-closed preconditions (refused while the approval switch is off; a 403 whenever no panel token is configured),
> and the verdict reuses the **very same** validation as in-group approval, relaxed nowhere.

## Mobile

The panel is responsive, so a phone browser works as-is (the layout switches at ≤ 760px):

| Desktop | Mobile |
| --- | --- |
| Left-hand navigation rail | Bottom tab bar (within thumb reach, with iOS safe-area padding) |
| Conversation list and chat area side by side | Master–detail: the list comes first, tapping a conversation opens the chat, and the header has a back button |
| Right-click menu | Long-press for 450ms opens the same menu (iOS does not fire contextmenu) |
| 14px input | 16px (anything below 16px makes iOS Safari zoom the page on focus) |
| QR card laid out horizontally | Stacked vertically, the QR code sized as `min(58vw, 26vh, 200px)` so a short screen cannot push the input off-screen |

Details: `viewport-fit=cover` plus `env(safe-area-inset-bottom)` handle the notch and the gesture bar,
`body { height: 100dvh }` stops the input from being clipped when the address bar collapses, and long text uses `word-break` so it does not overflow horizontally.
The desktop layout is completely unchanged.

> Two easy traps when changing this area: on narrow screens **do not auto-open the first conversation**
> (landing straight inside a conversation costs people their bearings), and **do not let JS read `classList` to decide layout** —
> layout comes from CSS media queries only; JS is responsible solely for the single view state `body.m-chat-open`.

## Image understanding (image download)

```
A member posts an image → the url in the event (QQ media CDN, a temporary link **carrying an expiring rkey**)
   └─→ downloader: check the cache first → download → on failure (4xx) ask the protocol side to re-issue the
         address via get_msg, match the same image by fileid and download it again
         └─→ base64 data URL → multimodal model (at most 3 images per message)
```

- **The cache is keyed by URL** (dual limits of 48 entries / 32MB, evicting the oldest): each image is downloaded once.
  Why cache at all: images stay in the conversation context for a long time, and previously **every turn re-downloaded them** — the moment an rkey expired that became one `[Vision] image download failed 400` per turn (measured in production: 80 out of 80 returned 400, flooding the log while the model saw no image).
- **Expiry fallback**: `IQqChatSource.RefreshImageUrlsAsync` (OneBot `get_msg`) — measured: once the same message is re-issued the download succeeds (the old rkey is dead, the new one returns 200). If the protocol side does not support it, this degrades to "this image is not visible this turn", without affecting the reply.
- **Safety**: image URLs come from QQ events and are untrusted input, so they pass an SSRF gate first (loopback / private addresses are blocked by default;
  `QQCHAT_ALLOW_PRIVATE_IMAGE_HOSTS=1` is for self-hosted / testing only).

## Stickers (one shared library)

This is a complete pipeline, and every stage fails in a way that is **quietly unhelpful**, so here is exactly what each stage does:

```
A member posts an image ──download──→ deduplicate by content sha256 and store it (stickers/)
                    └─→ queue it for the model to look at, producing "a one-line description + emotion / scene keywords"
Before replying    ──use the last few messages as the query, pick N candidates (6 by default) from the library into the prompt
              └─→ the model picks one with the sticker field in its JSON; sending an image alone is fine too (reply left empty)
                      └─→ the bot reads the file → base64 segment → sends the image (optionally with a quote) and records one use
Capacity      ──over the limit, evict by "least used ×3 − idle days" (frequently used ones stay; new ones are not deleted immediately)
Moderation    ──at ingestion the model also decides "is this a sticker": chat screenshots, ads and text-only images are dropped outright and never become candidates
Rate gate    ──two stickers in the same conversation are at least StickerCooldownSeconds apart (120 by default),
              and the same image does not repeat within 10 minutes (with a small library the model would "attach the same one to every line", which annoys a group)
Self-audit    ──every N seconds the library (id | description | use count | how long ago it was added) is handed to the model,
              which decides what to delete; at most 1/5 per pass, and anything used within 24 hours is blocked by the code
```

The panel's "Settings → Stickers" section changes the switch / limit / candidate count / audit interval, and lets you open the library to view thumbnails,
delete entries by hand, run an audit immediately, and import from the logged-in account's QQ sticker favourites (NapCat's `fetch_custom_face`;
an empty favourites folder is reported clearly rather than treated as an error).

Related endpoints: `GET /api/stickers`, `GET /api/stickers/{id}/img`, `POST /api/stickers/{id}/delete`,
`POST /api/stickers/curate`, `POST /api/stickers/import`.

> Two easy traps: image URLs come from QQ events and are untrusted input, so downloads block loopback / private IPs by default (SSRF);
> and the model produces "description + keywords" rather than the image itself, so candidates must be a retrieved handful — the whole library must never be stuffed into the prompt.

## Voice messages (optional: the model occasionally says a line out loud)

```
The model's output JSON carries speak (the words to say out loud; true = say the reply as voice)
   └─→ the bot composes http://tts:5000/speak?text=…&voice=…&speed=…
         └─→ it sends an OneBot record segment whose data.file is that URL
               └─→ NapCat downloads it → converts to silk (native converter) → uploads it as a voice message
On the bot's side: no audio download, no silk handling, no audio pushed through the WebSocket
```

Why it is designed this way (every item is a lesson learned):

- **Audio encoding belongs to the protocol side**: silk is a private QQ format, and wiring up an encoder yourself easily mismatches versions;
  NapCat ships a native converter (`convertToNTSilkTct`) and accepts `http(s)://` / `base64://` / local paths directly.
- **The price is network reachability**: NapCat must be able to reach that URL. With both containers on `qqchat-net` it is `http://tts:5000`;
  if NapCat lives on another machine, use the host IP / a domain name, and do not bind `tts` to `127.0.0.1` only.
- **Voice must stay restrained**: the prompt repeatedly asks for "occasionally" (thanking, being cute, singing, heavy emotion),
  and the code adds a **45-second per-conversation** gate (`VoiceMinIntervalSeconds`) — so a disobedient model still cannot flood,
  especially as Piper is a serial CPU inference taking seconds per line.
- **Everything degrades** (any failing stage falls back to text, nothing is lost): switch off, over `VoiceMaxChars`,
  the rate gate, `QQCHAT_TTS_URL` unset, the TTS container down, or a protocol-side retcode≠0 (e.g. no record segment support).
- **What is stored is `[语音] the words said`**: so the next turn knows what it just said out loud instead of thinking it never spoke.

The panel's "Settings → Voice messages" section covers: the switch, voice (a datalist offers 4 Chinese voices), rate, character limit, TTS address,
plus **preview a line** (it really goes to `/speak`, gets a wav back and plays it in the browser, so voice and rate can be tuned by ear) and
**check the TTS service** (asks the other side's `/health` and lists available voices). Related endpoints: `POST /api/voice/test`, `GET /api/voice/health`.

> A voice is a model file: changing voices means dropping a `<name>.onnx` (plus a same-named `.json`) into `/opt/qqchat/tts/`.
> Reproducing **a real person's voice** requires permission to use that voice for yourself first, then either dedicated training or a cloud cloning API —
> do not do it without authorisation.

## Recall / web search / parenthetical narration

**Recall (`group_recall` / `friend_recall`)** — a recall carries no body, so it can only be synchronised from the notice:

```
A member recalls a message
  └─→ that entry in the conversation becomes [已撤回] original content (content kept: the bot was there, and erasing it outright would give it amnesia)
        ├─→ it no longer gets a (#id), and can never be selected as replyTo (even if the model insists, the code refuses)
        ├─→ the model gets one chance to speak up ("what did you recall?"), with a 90s per-conversation cooldown
        ├─→ **a typo correction is not commented on**: if the same person sends a new message right after recalling, it is only tagged and no opening is given
        └─→ the prompt is explicit: it may remember, but must not repeat / quote / say things like "I saw everything"
Panel: that message is struck through, noting "what the model sees is「[已撤回] the original text」"
```

**Quoted replies (`[回复 X「…」]`)** — when a member uses QQ's "reply" to quote a message, that reply segment used to be **dropped outright**
(the model only saw a bare "me too" with no idea what it was answering):

```
A reply segment (array) in the message event, or [CQ:reply,id=…] (a CQ code) → extract the quoted message id
  └─→ look up "who said what" in the conversation context (context first, then "messages the bot itself sent")
        → prepend [回复 老王「原话」] to the body, entering both the model context and the chat log
        ├─→ quoting the bot's own line → [回复 你「…」] (so the model knows it is being addressed)
        └─→ the original cannot be found (too old) → only label "an earlier message"; **never invent content**
```

> Why the quoted words are stitched in as well: in a group chat a bare "me too" only makes sense through the quote — and although the quoted message is also in the context, it may be dozens of messages away, so the model does not necessarily match them up (and invents something when it fails). The ids of messages the bot itself sent come from the send response (`SendResult.MessageId`).

**Web search (the model fills in `search` / `read`)** — a two-turn action, the same idea as "listening to music":

```
Model output {"reply":"let me look that up","search":"keywords"}  or  {"read":"https://…"}
  └─→ the search / read really happens in the background (without blocking this turn's reply)
        ├─→ first choice: native /v1beta/models/<model>:generateContent + tools:[{google_search:{}}]
        │        — Google really searches, and the answer carries groundingMetadata (query + source titles)
        └─→ fallback: the WebSearchSources templates (searx* = SearxNG JSON / wiki* = MediaWiki JSON / otherwise extract HTML links)
              └─→ the results enter the **next** turn's prompt as "material just retrieved", and the model speaks from facts
Cooldown: the same conversation searches once per 30 seconds; if the search or read fails, the model is told honestly ("nothing found") rather than left to invent
SSRF: every outbound URL (including what read fetches) passes SafeUrl; search-source addresses are included
```

Why the default is "the model's own search" rather than scraping pages: in many deployment environments the egress IP is a datacentre address,
and Google / Bing / DuckDuckGo / Baidu answer crawlers with a portal page or a CAPTCHA; the model subscription itself can already search —
no extra key, no crawling, and the results carry sources. The search-source templates are there for "self-hosted SearxNG / internal search / clean egress IP" setups.

**"When should it search" is two hard-coded lists in the prompt** (the owner wanted the model to judge for itself, but judging needs a basis):

| | Contents |
| --- | --- |
| Must search | News and current events, weather, prices / exchange rates / stocks, fixtures and scores, event / server-opening / release dates, software and game versions, new anime and work information, someone's latest status — and any question carrying "now / latest / recently / today / this year" (training knowledge has a cutoff) |
| No search needed | Mathematics, idioms, grammar, historical and geographical general knowledge, coding syntax, anything visible from context; "what time is it / what's the date / what day is it" do not need a search either |

In addition, `[现在的时间]` (date + weekday + timezone) is injected into the prompt **unconditionally** — the model has no clock of its own,
so unless it is told, "what time is it" can only be guessed (measured in production: frequently wrong). This is independent of the web-search switch: it is still there with `QQCHAT_WEB_SEARCH` off.

Related endpoints: `POST /api/search/test` (`{query}` to search / `{url}` to read a page), `POST /api/voice/test`, `GET /api/voice/health`.

**Parenthetical narration (`〔旁白：…〕`, one switch in the panel)** — stage directions and expression notes in a group such as 「（笑）」「（bushi）」「行（端在桌上）」:

```
Switch on (QQCHAT_IGNORE_BRACKETS=1)
  └─→ leading / trailing parenthetical groups are rewritten as 〔旁白：…〕 and flow into the chat log, member profiles and model context (not a character lost)
        ├─→ a message that is entirely narration (〔旁白：笑〕) → does not trigger a reply on its own (narration is not addressed to anyone),
        │     gets no (#id) either (it cannot be quoted as "a line"); HasPendingReply=false (so the silent fallback does not take it to the model either)
        ├─→ body plus narration (行〔旁白：端在桌上〕) → triggers as usual, and the body is the line that matters
        └─→ the prompt explains this is a stage direction / expression, not something they said → usable as on-scene information, not as a line to answer
Exceptions (never touched): parentheses in the middle of a sentence (（2026）年的计划), records of members posting emoji ([表情:斜眼笑] / [图片]),
              messages with images, an @-mention of the bot, and private chats
```

> Why tag it inline instead of adding a field: this is the same kind of thing as `[图片]` / `[表情:斜眼笑]` —
> inbound messages are rewritten into a form the model can read, and the panel / archive see the same bytes the model sees, with no schema change.

## Group member roles (owner / admins / group titles)

When the bot speaks in a group it knows "who calls the shots": the prompt gains a block such as

```
[本群身份]
群主：老王（20001）
管理员：小美（20002）
群头衔：小明（20003）=活跃气氛担当
```

- **Two sources**: ① the `sender.role` carried by group message events (free, present on every message);
  ② the protocol-side action `get_group_member_info` — **the group title exists only there**, so it is asked for on demand
  (never twice for the same person within 3 days; the answer is stored, so a restart does not lose it).
- **Storage**: the `member_roles` table (primary key `uid + group_id`, since the same person may be an admin in group A and an ordinary member in group B).
- **Only "worth mentioning" people are listed**: owner / admins / people with a title, at most 14 (beyond that it is just a list of names and wasted tokens);
  ordinary members do not occupy this block, and private chats never get it.
- The prompt also tells the model what these roles mean (an admin can kick and recall — but **do not flatter rank and do not use it to push people around**).

## Participation gate / human approval / questions / scenario presets (all off by default)

All four switches live in the **panel** (no environment variables needed), and once on, the panel honestly echoes "what extra capability exists right now":

| Switch | Default | Once on |
| --- | --- | --- |
| Participation gate | off | When the state machine says "not this round" (observing / exiting / cooling down) the model is not called and nothing is said; **receive-only** — an @-mention is still answered as usual. The read-only endpoint `GET /api/participation` shows each conversation's state and reason (no message text) |
| Allow questions | off | When the model writes `action=ask`, the server wraps a question with a **one-time code** and posts it to the group (code and expiry issued server-side); any single answer counts as answered, and it expires unusable; **asking grants no permissions whatsoever** |
| Human approval | off | When the model "wants to call a tool" it first posts a **pending confirmation** (code / who may approve / expiry); only after the owner, an admin or someone named in the panel replies "同意 XXXX" does it execute — identity / conversation / expiry / one-time use / policy version are all checked server-side. It **only executes the fixed fake tool `demo.echo`** (one log line plus a demo explanation; no shell / file / process control) |
| Scenario preset | empty | Set to one of `on-demand` / `research` / `social`: it only **tightens** the capability allowlist and the per-turn budget; an unrecognised name means the lowest permissions (fail-closed). Empty = exactly the existing switches (as before) |

The participation gate additionally has environment variables (the other three are panel-only):

| Variable | Default | Description |
| --- | --- | --- |
| `QQCHAT_PARTICIPATION_GATING` | `0` | `1` = turn the participation gate on |
| `QQCHAT_PARTICIPATION_MAX_REPLIES` | `3` | Maximum consecutive lines while participating |
| `QQCHAT_PARTICIPATION_COOLDOWN` | `20` | Cooldown after that maximum is reached (seconds) |
| `QQCHAT_PARTICIPATION_PROBING` | `1` | How many probes are allowed while observing |
| `QQCHAT_PARTICIPATION_ACTIVE_LIFE` / `QQCHAT_PARTICIPATION_EXITING_LIFE` | `900` / `180` | Upper bounds for the active / exiting phase (seconds) |

## `//` tasks (server agent) and the daily health report

- **`//` tasks**: dispatch work in a group or private chat with the `//` prefix, and the result returns to **the conversation it came from**. The switch is in the panel:
  the prefix (`//` by default), the user allowlist (**empty = nobody may use it**; permissions land on specific individuals), the token,
  the server-side tool table (bash / file read-write / search / docker / QQ actions — **docker and dangerous actions are off by default**),
  and the server agent's own model endpoint and key (configured separately from the chat path).
- **Context hygiene**: the server agent is **isolated per task** by default (it does not carry the previous turn's history); to continue one, say `//接着` explicitly,
  and `//reset` clears the current conversation completely.
- **Daily health report**: once a day (18:00 by default, can be turned off) a private status line — memory / load / conversation count / queues / voice connectivity.
  **Purely server-side**, never going through an external device (that machine may not even be on).
- **One-click deploy from the panel**: upload an artefact or paste a URL → the server rebuilds the image and replaces the container itself; a rollback point is taken automatically before deploying,
  and high-privilege switches are off by default.

## Design trade-offs and operating boundaries

The core capabilities (the OneBot gateway and its three transports, the member profile store, conversation persistence, the model client including multimodal image understanding,
the whitelist, cooldown rate limiting, the serial request queue, the silent fallback, the quoting rules, plus the later unified tool catalogue / decision traces /
bounded stepping loop / third channel) are all pinned by test suites, see the next section.
The following are **deliberate trade-offs**, not a to-do list:

| Trade-off | Explanation |
| --- | --- |
| Headless | The AI switch is controlled by `QQCHAT_AI_MODE` instead (there is no "AI on / off" button in a container) |
| It only connects, it does not run things | The protocol side (NapCat) downloads, starts and configures itself; this service only connects, sends and receives |
| When group history is fetched | There is no conversation list to click, so it is fetched "the first time a message from that group arrives" |
| Sentence-by-sentence sending | Long replies are split on sentence-ending punctuation and sent in batches at a rhythm (nothing lost; decimals / domain names / repeated punctuation / closing quotes are not split) |
| Hard rules for quote targets | The semantics of replyTo are pinned in the prompt (who you are talking to, not the material); when this turn's trigger is a **repeat / imitation**, a model pointing elsewhere gets no quote; with no triggering message there is no quote either |
| `SenderId` / `ImageUrls` / `QqMessageId` are persisted | So member profiles and image understanding survive a restart |
| The `*` wildcard whitelist, health checks, environment-variable / secret-file configuration, unescaped Chinese in JSON | All aimed at container deployment |
| **The panel frontend has no build step** | Plain JS + CSS (**no npm dependencies, no bundler**): panel assets are embedded in the assembly, so what `git archive` produces is the entire deliverable; adding a page = add a file + one route line in `PanelRoutes` + probe coverage (per the criteria in §7.1: on a ~1GB machine the frontend build must not enter the pipeline) |

---

## Tests

Two layers of verification, both genuinely end-to-end (no mock stubs):

```bash
# ① Local integration tests: a real bot process + a fake protocol side (real WebSocket) + a fake model (real HTTP)
dotnet run --project tests/BotAgent.IntegrationHarness -c Release

# ② Container tests: verify the bot as it really runs in a container (place the fake protocol side / model on the container network)
docker network create qqchat-e2e
docker build -f tests/BotAgent.IntegrationHarness/Dockerfile -t qqchat-harness .
docker run -d --name harness --hostname harness --network qqchat-e2e \
  -e HARNESS_BOT_HEALTH_URL=http://bot:8080 qqchat-harness --serve-external
docker run -d --name bot --hostname bot --network qqchat-e2e \
  -e QQCHAT_API_KEY=sk-mock \
  -e QQCHAT_BASE_URL=http://harness:18899/v1 \
  -e QQCHAT_ONEBOT_URL=ws://harness:13099 \
  -e QQCHAT_WHITELIST=12321 \
  qqchat-agent
docker logs harness      # assertion results
```

Coverage: reverse / forward WebSocket handshake, login-number detection, group and private chat paths, whitelist strict mode and the `*` wildcard,
staying silent when the model is silent, quoted replies (a quote is attached only when someone cut in; targets the model points at are validated; no wrong credit when repeating; **incoming quoted replies are recognised too**),
sentence splitting without losing characters, parenthetical narration tagged as `〔旁白：…〕` without triggering a reply on its own, member profile reads and writes,
prompt assembly (persona / profiles / the `{sender}{content--time}` format), conversation recovery after a restart, and the health endpoints.
The full suite has **729** assertions (measured 2026-09-24: **728 pass / 1 pre-existing soft warning line** — the "system prompt < 4000 characters"
warning line, measuring ~4364 characters; **it is a sentinel, not a target, so don't move the threshold**). Inside the harness, `QQCHAT_IT_ONLY=s19` runs a single scenario.

There are also several **sub-second probes** (no database, no network, each run after changing the corresponding part): `ArchitectureProbe` (architecture ratchet, **92/0**),
`SafetyProbe` (mechanisms and safety boundaries, **341/0**), `ParticipationProbe` (48/0), `PipelineEval` (isolated evaluation, 68/68),
`FrontendProbe` (panel static + runtime, **230/0**).

---

## Troubleshooting

| Symptom | Cause and fix |
| --- | --- |
| `/readyz` keeps returning 503 | Check `docker compose logs qqchat`: wrong address / token, or NapCat has no forward WS configured |
| The log says "whitelist is empty → ignoring all messages" | `QQCHAT_WHITELIST` is unset. Fill in `*` or specific group numbers |
| `/status` shows `connected:true` but the bot never speaks | The model left `reply` empty (this is by design, to avoid spamming). Raise `QQCHAT_AI_DESIRE`, or state clearly in the persona when it should join in |
| The health-check port will not start | On Windows without administrator rights `+` cannot be bound: set `QQCHAT_HEALTH_BIND=127.0.0.1`, or `QQCHAT_HEALTH_PORT=0` to disable it |
| Voice / files are not received | A known limitation of the Docker NapCat build (text and images are fine) |
| Timestamps are 8 hours off | Set `TZ=Asia/Shanghai` and restart the container |
| The session is lost and you must scan the QR code every time | The `./napcat/ntqq` volume is not mounted, or has the wrong owner (`NAPCAT_UID` / `NAPCAT_GID`) |

## License

The source code of this program is MIT. NapCat follows its own licence (non-commercial); see also the repository root `README.md`.
