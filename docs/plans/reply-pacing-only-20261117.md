# One Reply Pacing Configuration

## Scope

Use the existing reply pacing coordinator for all chat admission. Remove the legacy scheduling configuration from both API projections and the existing panel. No deployment, live configuration change, live chat access, or provider change is part of this task.

Development branch: `refactor/reply-pacing-only-20261117`; base: `66149bf3bcbd3d61742485b5cf31c1524219f75f`; implementation commit: `b54dc42eec0f3fc045cb7b4c6ddfdaa682f53475`. Publication is prepared on a separate worktree based on the latest public `dev`, preserving later official-channel and dependency changes. The original investigation worktree is preserved.

Keep persona, AI enablement, suitability checks, proactive quiet detection, splitting, media, tools, access policies and concurrency. Keep the existing four pacing defaults (20 percent, 30 seconds, 3 seconds and 1.5 seconds) and existing saved parameter values.

## Steps

1. Remove retired reply settings from active views, edits, environment seeds and panel projections. Reserve their existing field IDs; reject retired legacy API input without partial writes.
2. Route inbound, fallback, proactive and accepted-result requests through the existing coordinator. Remove the legacy queue/catch-up/mode handoff path; retain source, stop, permission and final-send validation.
3. Expose the four pacing parameters in basic configuration, remove old controls and their draft-lock behavior, and update module ownership.
4. Verify clean/legacy configuration loading, saved parameter round trips, probability rejection, cooldown after confirmed completion, continuation, accepted results and stop/source retirement using synthetic probes.
5. Rebuild Headless separately. Run frontend type checking, unit/component tests, configuration browser workflows, and desktop/mobile screenshots. Keep original failures when verification reveals a defect.

## Compatibility

Historical field IDs and receipts remain decodable and are not renumbered. Unknown old stored reply keys are ignored; no startup write removes user data. New saves no longer create those fields. A former false mode flag cannot restore the old scheduler. Probability zero is the supported way to suppress ordinary group interjections; direct/private requests retain existing explicit-request semantics.

## Progress

- [x] Retired 13 active fields, their environment seeds and edit/read projections; reserved historical IDs.
- [x] Removed legacy queues, catch-up and mode handoff from the host reply pipeline.
- [x] Moved the four pacing parameters into basic configuration; preserved conversation features.
- [x] Frontend type checking and 1093 unit/component tests passed.
- [x] Fresh/legacy configuration (103/104 checks), coordinator (42 scenarios), scheduler (13 scenarios), pipeline (28 scenarios / 85 assertions), generation (10 scenarios), delivery (61 checks), and lifecycle (17 scenarios) passed.
- [x] Standalone prompt (619 checks) and differential prompt (873 checks) passed, including stable cache prefixes and independent legacy baseline validation.
- [x] Backend regressions passed: safety 817, protocol 408, Engine 65, ProductionSpec 115, remediation 1710, architecture 221; settings scope passed with no failures.
- [x] Two synthetic Headless selections passed: 348 and 323 assertions. Coverage includes quotes, recall, music, poke, voice, continuation, silence, migration, permissions, saved configuration, backup and task recovery.
- [x] Chromium configuration/recovery/capability workflows passed 64/64; final pacing/layout rerun passed 7/7 after the final Headless rebuild. Desktop 1850/1280/1024 and mobile 390 layouts were checked, with screenshots retained.
- [x] Solution build and separate forced Headless rebuild passed. CLI help and both Headless READMEs now use the four pacing settings. Active source scan found no legacy mode handoff, catch-up bypass or retired environment seeds.
- [x] Final smyw spec is `stage: done`; its generated offline preview renders without script errors. The isolated local panel at `http://127.0.0.1:4292/web/` uses temporary synthetic storage and no production route; 1280/390 browser checks confirmed four defaults and no retired controls.

## Verification Record

Verification evidence is retained outside the published repository. Original failing runs remain alongside successful reruns; assertions were not weakened. Fixture changes wait for actual model/idle boundaries and initialize shared-host pacing values before checking defaults.

The confirmed context fix retains completed bot replies and later contributions from other speakers, while committed effects and independent task results keep their same-speaker sequence boundary. Recall notices, deferred music input and accepted callbacks retain distinct source and stop ownership.

The solution retains existing test-fixture warnings; the final forced Headless build reported zero warnings and zero errors. No full all-platform live-account or upstream-provider run was attempted.

## Publication Integration

The publication candidate incorporates the current public official-channel implementation and dependency updates. Both historical mode-flag values are negative compatibility controls: neither may restore the legacy queue or emit a superseded generation. Official segmented delivery, fixed reply credentials, durable receipts and replay refusal remain covered.

Paced generations retain the existing shape-only trace lifecycle, including silence and cancellation. Actual pipeline regressions cover 30 scenarios and 95 assertions; the real-host unified-memory scenario retains its isolation and natural-task checks. Integration fixtures use exclusive free-port probes, distinct post-restart replies and actual model/send boundaries. Original failures are retained separately from successful reruns. These checks do not establish live-account acceptance or deployment.

## Completion

The source and panel expose one scheduling policy, the four parameters persist with existing validation/atomicity, and synthetic checks cover real admission and delivery boundaries. Local verification is not production deployment or real-account delivery acceptance.
