# Reply pacing (#151)

## Configuration

Reply pacing is opt-in. Saving the setting is required; enabling it is not part of a code deployment.

| Field | Default | Accepted range | Applies to |
| --- | ---: | --- | --- |
| `replyPacingEnabled` | false | boolean | New reply scheduler |
| `groupInterjectionProbability` | 20 | integer 0–100 | Ordinary group opportunities only |
| `groupInterjectionCooldownSeconds` | 30 | integer 0–3600 | Ordinary group admission |
| `groupContinuationWaitSeconds` | 3 | integer 0–30 | Same-speaker group quiet interval |
| `privateContinuationWaitSeconds` | 1.5 | number 0–30 | Private quiet interval |

Settings and field-mode storage use configuration field IDs 148–152. Physical database schema 14 widens the field-mode constraint from 147 to 152. The upgrade preserves existing field modes and is atomic. Do not use an older host binary against a newer schema or treat a binary-only rollback as a database rollback.

A turn captures its pacing parameters when admitted. Changing the numbers does not rewrite that turn’s parameters. Permissions, source lifetime, stop state and transport capability are still rechecked at commit. While pacing is enabled, the former participation setting is retained but its editor/clear/reset controls are locked; disabling pacing restores the draft rather than erasing it.

## Admission and scheduling

- Each ordinary opportunity has one probability decision. A failed draw or cooldown rejection remains terminal throughout that continuous input interval; continuation, timers and fallback paths do not draw again.
- Direct requests, replies to the bot and private requests bypass probability and proactive cooldown, but never bypass authorization or source lifecycle. Group probability 0 also prevents unsolicited proactive topics.
- Same-speaker continuation moves that speaker’s quiet deadline. Other speakers contribute context without postponing an already admitted speaker. Ready direct requests have priority, with one generation/send owner per conversation and global resource fairness.
- The bounded coordinator holds at most 256 entries. New direct requests that cannot be admitted receive a rate-limited busy indication; accepted direct requests are not silently evicted. Busy indications are limited to once per conversation per 30 seconds.
- After 12 seconds of continuous input, one text-only speculative draft may be prepared. It cannot start tools or media. A stale result may be recomputed once immediately; further work waits for genuine quiet rather than looping on another 12-second timer.
- The first outward send has a staged prepare/validate/commit boundary. Stale text is discarded before that boundary. Once sending or an admitted effect begins, it is not replayed because of later input. Same-speaker input during that work becomes follow-up work; input after full completion does not inherit an exemption.
- Cooldown begins at the final confirmed completion of a chat reply, including direct replies. Partial, failed and unknown delivery outcomes do not count as full completion. Image-only and poke-only activity does not start a chat-completion cooldown.
- Accepted background results have their own identity and do not re-enter ordinary random admission. Independent tasks retain their own source/authentication lifetime, not the disposable generation guard that dispatched them.
- Runtime mode changes must discard unsent ordinary work, transfer direct work once, and preserve already started effects/sends without replay. Pending and active legacy work are part of this boundary, not only paced work.

## Entry points and source coverage

The policy applies to accepted inbound messages, same-speaker continuation, pending/fallback requests, proactive opportunities and accepted background result callbacks. Context-only observation never performs admission. Stop, shutdown and source retirement prevent disposable work from producing a late send.

| Source | Final commit location | Synthetic coverage |
| --- | --- | --- |
| OneBot | Transport send immediately before WebSocket commit; action classification separates chat sends from effects | Delayed transport, final validation, failure acknowledgement and action classification |
| Official | Chat API submission after token/media preparation; preparation itself is not a business effect | Delayed preparation, capability/source recheck and upload vs chat commit |
| Feishu | Chat submission after token preparation | Final validation and capability/source lifetime checks |
| Synthetic/default source | Generic sender boundary immediately before source invocation | Queue, stale result, permission and delivery ordering |

These are local synthetic checks. They do not claim live-account delivery coverage or browser visual signoff. No production chat contents are required.

Per-conversation stop retires captured chat ownership even while a request is detached from a queue during a mode handoff. It does not change the cancellation or recovery rules of existing independent background tasks: their previously accepted producers may still complete and request result delivery after a local conversation stop. Global cancellation, source lifetime and permission checks retain their existing authority. This preserves the plan’s independent-task boundary rather than extending chat-stop semantics.

## Verification

Use the probe projects registered in [the solution](<../BotAgent.slnx>) and [CI](<../.github/workflows/ci.yml>): configuration (fresh and `--legacy-schema`), coordinator, generation, scheduler, delivery, full pipeline and prompt checks. The mode-transition probe separately checks old-path handoff. Existing architecture limits remain unchanged.

Some file-edit tooling preserves modification times. For trustworthy local validation use a clean or forced build with a dedicated artifacts path, then execute that exact DLL:

```sh
dotnet build tests/BotAgent.ReplyPacingProbe/BotAgent.ReplyPacingProbe.csproj -c Release -t:Rebuild --artifacts-path .tmp/pacing-validation
dotnet .tmp/pacing-validation/bin/BotAgent.ReplyPacingProbe/release/BotAgent.ReplyPacingProbe.dll
```

The integration harness launches the standard Headless output, not the probe artifacts directory. Rebuild that host explicitly before harness scenarios. The current host and integration harness target .NET 10; do not execute an older framework output left in another directory. Candidate verification should compare content hashes, not only Git’s timestamp-based status cache.

[The browser regression](<../src/web/tests/e2e/reply-pacing.spec.ts>) uses the existing isolated Playwright host and synthetic configuration. It checks default-off values, four saved parameters, validation, reload, mode changes and retained/locked former participation controls. It does not access a deployed panel or provide human visual signoff. Rebuild the host first, then run:

```sh
cd src/web
npm run test:e2e -- tests/e2e/reply-pacing.spec.ts --project=chromium --headed
```

Queue durability and request intent are separate: retaining an ordinary catch-up request does not turn it into an explicit request during a mode change. Accepted results keep their own identity across handoff; they are not identified solely by a missing trigger message.
