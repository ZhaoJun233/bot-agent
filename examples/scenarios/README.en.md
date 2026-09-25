# examples/scenarios — three **key-free** synthetic scenario configurations

These three JSON files are the scenario configurations required by V3 §11.1. They **contain no keys, no real group numbers, no real domains and no IPs**,
so they can be filled into the panel by hand for comparison, or sent with `POST /api/settings` (panel values override env; nothing takes effect until saved).

| File | Scenario | What it demonstrates |
| --- | --- | --- |
| `on-demand.json` | On-demand participation | Speaks only when explicitly @-mentioned / replied to (**turns the participation gate on**: ordinary messages do not reach the model); the tool surface is minimal (web / music / voice / stickers / poke all off) |
| `research.json` | Discussing material | Allows **restricted** search and page reading (preset: a budget of 2 per turn); still no extra actions |
| `social.json` | Multi-person conversation | Allows limited participation and extra actions; **turns human approval on** (by default covering only the fixed fake tool `demo.echo`) |

## How to use

```powershell
# For example: apply the social set to a local panel (the panel must be running; this is the one request equivalent to "pressing save")
curl -X POST http://127.0.0.1:8080/api/settings -H "Content-Type: application/json" `
     --data-binary "@examples/scenarios/social.json"
```

- The two underscore-prefixed keys `_comment` / `_howToApply` are documentation only and are ignored by the server (it only recognises names it knows).
- **Restoring the previous state**: clear `scenarioPreset` and turn `enableApprovals` off, then save once more — empty means byte-for-byte the previous behaviour.

## What these three configurations do **not** do (the honest boundary for a demo)

- They do not connect to real QQ, do not connect to a real model and do not use real keys — every demo runs a **fake model + fake protocol side** (see `docs/engineering/demo-script.md`).
- Scenario presets **can only tighten**: they will not turn on a capability that is switched off in the panel; a misspelled name falls back to the **lowest permissions** (no capability enabled at all).
- The approval turned on in `social` **covers only the fixed fake tool** `demo.echo` (one log line plus one receipt) and **does not mean** the system has shell / file / process control.
