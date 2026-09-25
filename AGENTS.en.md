# AGENTS.md — read this before working in this repository

[简体中文](AGENTS.md) | English

This bot is wired to **real people in real QQ groups**: logs, storage and panel endpoints all contain other people's nicknames and messages.
**The code is free to read; the data must be masked first.**

> The workspace also holds a more detailed version (with local / server paths and deployment scripts): the `AGENTS.md` at the workspace root.
> That one is not part of the public snapshot; this one travels with the code.

---

## 1. Red line: do not read group chat content or member privacy

**Do not read, do not repeat, do not paste**:

- Conversations and messages: `data/conversations.json`, `agent-sessions.json`, and the `messages` / `member_*` tables inside the SQLite database (`qqchat.db`)
- Member profiles: `data/member_profiles/*.json`
- Log bodies: lines such as 「收到 …」/「已回复 …」 in `logs/qqchat.log` (they carry the original message text)
- Protocol-side payloads: NapCat's OneBot event JSON (the `message` / `raw_message` / `post_type` fields)
- Panel / API bubbles: `/api/conversations/{key}/messages`, `/api/profiles/{uid}`
- An unfiltered `docker logs` / `tail -n 200` — the whole block will inevitably contain message text

There is exactly one exception: the user explicitly asks you to look at one specific item, and you look at **only** that one.

---

## 2. The correct way to investigate errors (the only permitted one)

Grep only the error lines, and **read them after replacing Chinese text (messages, nicknames) with placeholders**:

```sh
LOG=/opt/qqchat/data/logs/qqchat.log

grep -aE 'ERROR|Exception|Unhandled|Traceback' "$LOG" | tail -30 \
  | python3 tools/mask-cjk.py        # shipped in the repo: non-ASCII runs → <CJK>
```

- **Never** run an unfiltered `tail` / `docker logs` dump.
- When you need to know "what the data looks like", look only at **shape**: field names, counts, lengths, hashes, status codes.
- When writing conclusions into documents / commits / replies, write only shape and counts, never content.
- Content you have already read **does not leave your hands**: back into the group, into the repository, or out to a third-party API (including an external model you ask to "take a look") — none of it.

---

## 3. Preserve privacy while writing new features

- Any new output that **lists conversations / members / files** must go through the masking switch:
  on the in-group path use `BotAgentHost.MaybeMask` / `ChatLabel`, elsewhere use `AgentMask.Text / ChatLabel / Shorten`.
- **Mask at the display layer while storage and keys stay untouched**: once a key like `group:123` is masked, every command and panel button breaks.
- In the panel, "an editable real name" uses `nameRaw` (the displayed one is `name`); the rename input must contain the real name, otherwise `群友A` gets written back while masking is on.
- Documents / comments / tests / probes must not contain real QQ numbers, group numbers, nicknames, domains, IPs or keys — write `10001`, `群友A`, `example.com` instead.
- Verify privacy behaviour with synthetic data (`tests/BotAgent.IntegrationHarness`, `MockOpenAi`); never experiment on live conversations.

---

## 4. Two switches (two faces of the same feature)

| Switch | Field / environment variable | Default | Effect |
| --- | --- | --- | --- |
| Mask when listing conversations | `enableAgentMask` / `QQCHAT_AGENT_MASK` | **on** | Group names, nicknames and QQ numbers keep only the first 3 and last 2 characters (`940***75`); `//sessions`, `//sessions all`, `//runs`, `//pi` and the panel conversation list all respect it |
| Extra agent prompt | `agentPrompt` / `QQCHAT_AGENT_PROMPT` | the privacy red line | Carried by every `//` task: for an external device (pi) it is prepended to the task, for the built-in server agent it is folded into the **system prompt**; the default value is `AppSettings.DefaultAgentPrompt` (i.e. sections 1 and 2 of this file) |

---

## 5. Common commands

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
node tests/BotAgent.FrontendProbe/probe.mjs                       # panel smoke test (no browser)
QQCHAT_IT_ONLY=s36 dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
# structural guardrails (read source only, no database, no process): baseline lives in ArchitectureProbe/Baseline.cs
dotnet build tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet tests/BotAgent.ArchitectureProbe/bin/Release/net8.0/BotAgent.ArchitectureProbe.dll
```

- The architecture guardrail is a **ratchet**: the thresholds in `Baseline.cs` may only be **lowered**, never raised (raising them = loosening a structural constraint).
  When a number drops, use `--print` to get the new value and update the baseline. The plan and its batches live in `docs/engineering/architecture-optimization.md`.
- Deploying to the server / publishing the public snapshot / historical records (internal material outside the repository) are handled by scripts outside the repository; the workspace `AGENTS.md` has the details.
- The public snapshot runs masking checks (domains, IPs, QQ numbers, nicknames, model names, keys): **commit first, publish second**.
