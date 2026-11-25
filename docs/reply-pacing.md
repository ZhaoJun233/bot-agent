# Reply pacing (#151)

## Configuration

Reply pacing is the only chat scheduler. The panel exposes its four parameters in basic configuration; there is no mode switch. Saving changed parameters is still required. The AI mode switch remains available to stop automatic chat replies.

| Field | Default | Accepted range | Applies to |
| --- | ---: | --- | --- |
| `groupInterjectionProbability` | 20 | integer 0–100 | Ordinary group opportunities only |
| `groupInterjectionCooldownSeconds` | 30 | integer 0–3600 | Ordinary group admission |
| `groupContinuationWaitSeconds` | 3 | integer 0–30 | Same-speaker group quiet interval |
| `privateContinuationWaitSeconds` | 1.5 | number 0–30 | Private quiet interval |

Pacing parameters use existing field IDs 149–152. ID 148 and the legacy reply field IDs remain reserved so historical field modes and receipts stay decodable. Physical schema 14 is unchanged. Existing saved pacing parameters are retained; a legacy false mode flag, desire, cooldown or participation setting cannot restore the old scheduler. Old stored keys are ignored without a startup write, and requests that submit retired fields are rejected without partial application.

A turn captures its pacing parameters when admitted. Changing the numbers does not rewrite that turn's parameters. Permissions, source lifetime, stop state and transport capability are still rechecked at commit. The former participation state machine is observation-only, with a fixed diagnostic policy; it does not provide another set of reply limits.

## Admission and scheduling

- Each ordinary opportunity has one probability decision. A failed draw or cooldown rejection remains terminal throughout that continuous input interval; continuation, timers and fallback paths do not draw again.
- Direct requests, replies to the bot and private requests bypass group probability and completion cooldown, but never bypass authorization or source lifecycle. Group probability 0 also prevents unsolicited proactive topics.
- Same-speaker continuation moves that speaker’s quiet deadline. Other speakers contribute context without postponing an already admitted speaker. Ready direct requests have priority, with one generation/send owner per conversation and global resource fairness.
- The bounded coordinator holds at most 256 entries. New direct requests that cannot be admitted receive a rate-limited busy indication; accepted direct requests are not silently evicted. Busy indications are limited to once per conversation per 30 seconds.
- After 12 seconds of continuous input, one text-only speculative draft may be prepared. It cannot start tools or media. A stale result may be recomputed once immediately; further work waits for genuine quiet rather than looping on another 12-second timer.
- The first outward send has a staged prepare/validate/commit boundary. Stale text is discarded before that boundary. Once sending or an admitted effect begins, it is not replayed because of later input. Same-speaker input during that work becomes follow-up work; input after full completion does not inherit an exemption.
- Cooldown begins at the final confirmed completion of a chat reply, including direct replies. Partial, failed and unknown delivery outcomes do not count as full completion. Image-only and poke-only activity does not start a chat-completion cooldown.
- Accepted background results have their own identity and do not re-enter ordinary random admission. Independent tasks retain their own source/authentication lifetime, not the disposable generation guard that dispatched them. Recall notices also have independent ownership, but ordinary group notices still obey probability and completion cooldown.
- There is no legacy mode handoff or cooldown catch-up bypass. AI disablement, stop and source retirement cancel disposable work; changing pacing parameters does not replay an already started effect or send.

## Entry points and source coverage

The policy applies to accepted inbound messages, same-speaker continuation, pending/fallback requests, proactive opportunities and accepted background result callbacks. Context-only observation never performs admission. Stop, shutdown and source retirement prevent disposable work from producing a late send.

| Source | Final commit location | Synthetic coverage |
| --- | --- | --- |
| OneBot | Transport send immediately before WebSocket commit; action classification separates chat sends from effects | Delayed transport, final validation, failure acknowledgement and action classification |
| Official | Chat API submission after token/media preparation; preparation itself is not a business effect | Delayed preparation, capability/source recheck and upload vs chat commit |
| Feishu | Chat submission after token preparation | Final validation and capability/source lifetime checks |
| Synthetic/default source | Generic sender boundary immediately before source invocation | Queue, stale result, permission and delivery ordering |

These are local synthetic checks. They do not claim live-account delivery coverage or browser visual signoff. No production chat contents are required.

Per-conversation stop retires captured chat ownership, including deferred ingress callbacks. It does not change the cancellation or recovery rules of existing independent background tasks: their previously accepted producers may still complete and request result delivery after a local conversation stop. Global cancellation, source lifetime and permission checks retain their existing authority.

## Verification

Use the probe projects registered in [the solution](<../BotAgent.slnx>) and [CI](<../.github/workflows/ci.yml>): configuration (fresh and `--legacy-schema`), coordinator, generation, scheduler, delivery, full pipeline and prompt checks. The former mode-transition probe now checks single-policy adoption, parameter changes and stop boundaries. Existing architecture limits remain unchanged.

Some file-edit tooling preserves modification times. For trustworthy local validation use a clean or forced build with a dedicated artifacts path, then execute that exact DLL:

```sh
dotnet build tests/BotAgent.ReplyPacingProbe/BotAgent.ReplyPacingProbe.csproj -c Release -t:Rebuild --artifacts-path .tmp/pacing-validation
dotnet .tmp/pacing-validation/bin/BotAgent.ReplyPacingProbe/release/BotAgent.ReplyPacingProbe.dll
```

The integration harness launches the standard Headless output, not the probe artifacts directory. Rebuild that host explicitly before harness scenarios. The current host and integration harness target .NET 10; do not execute an older framework output left in another directory. Candidate verification should compare content hashes, not only Git’s timestamp-based status cache.

[The browser regression](<../src/web/tests/e2e/reply-pacing.spec.ts>) uses the existing isolated Playwright host and synthetic configuration. It checks the four parameters, validation, reload, absence of retired controls, and rejection of retired API input. It does not access a deployed panel or provide human visual signoff. Rebuild the host first, then run:

```sh
cd src/web
npm run test:e2e -- tests/e2e/reply-pacing.spec.ts --project=chromium --headed
```

Queued human requests, recall notices and accepted task results retain distinct identities. A missing trigger message alone does not turn an ordinary request into an accepted result or an explicit request.
