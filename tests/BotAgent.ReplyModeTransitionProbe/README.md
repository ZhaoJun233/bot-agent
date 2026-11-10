# Reply mode transition probe

Synthetic integration coverage for the actual Engine reply pipeline. The project references Engine and reuses the controlled model, source, clock, and research fixtures; it does not read production state or contact external services.

## Run

From the repository root:

```sh
dotnet build tests/BotAgent.ReplyModeTransitionProbe/BotAgent.ReplyModeTransitionProbe.csproj -c Release -t:Rebuild --artifacts-path .tmp/mode-transition-artifacts
dotnet .tmp/mode-transition-artifacts/bin/BotAgent.ReplyModeTransitionProbe/release/BotAgent.ReplyModeTransitionProbe.dll
```

Always force Rebuild when testing file-tool edits: preserved timestamps can otherwise reuse stale binaries.

## Coverage (17 scenarios)

- OFF-to-ON retires ordinary unsent drafts, queued work, and global-slot waiters.
- Rapid mode toggles never revive retired ordinary work.
- Active and queued explicit requests transfer once and honor the complete group quiet window (2999/3000 ms).
- Already-started sends and inline research effects finish without duplicate work.
- Ordinary deferred catch-up and null-trigger ordinary/proactive catch-up do not gain explicit intent from queue durability, including at 0% interjection probability.
- Accepted detached research results retain their own identity through OFF-to-ON-to-OFF-to-ON. The test advances no virtual time, so accidentally adding human quiet fails.
- Null-trigger direct requests retain human quiet; private input retains explicit intent and its exact 1499/1500 ms boundary.
- Stop during deferred admission cannot recapture a new epoch; the stopped request produces no model call or send, while genuinely new input remains eligible.
- Stop serializes with queue-to-paced migration, and retires a real explicit handoff held outside every queue. Both cases verify that other source keys keep their epoch and that new same-conversation input can still reply.

TransitionClock waits for the real fixture clock's timer registrations and advances to their due times. This avoids advancing virtual time between a worker computing a relative delay and registering that delay. Reflection supplies scheduler/queue observations and the stopped-deferred interleaving. The latter publishes the same flushing marker as the real deferred path and uses the existing disposal hook to trigger ForgetSession after the initial epoch check. No production test hook is added; behavioral assertions observe admission, model calls, cancellation, and synthetic sends.

The Stop migration regression preserves an independent bounded Thread/Monitor interleaving: Stop blocks on the deferred gate while OFF-to-ON tries to transfer the queue. Its corrected-path barrier releases deferred when Stop retains pacing, avoiding a test-induced deadlock. The detached regression holds the real transition result after its queues are drained and calls Stop before dispatch. Queue-transition handoffs and worker-completion handoffs both have detached intervals; retiring matching entries in the existing weak owner registry covers them without introducing strong ownership or moving cancellation callbacks under locks.

The legacy deferred scheduler polls at 150 ms; the catch-up test waits for that registration before advancing. The model fake deliberately ignores cancellation, so a late provider result still exercises the production commit fence.

## Evidence

The intent regressions initially exposed ordinary/proactive null-trigger handoffs being promoted to accepted results (9/12 total, with the first deferred test also needing its legacy timer setup corrected). After separating PendingReplyIntent from MustPreserve, the corrected suite passed. The 14-scenario suite passed a forced Release rebuild and five fresh-process repetitions. Independent review then exposed an old deferred request being readmitted under a fresh epoch after Stop. The preserved counterexample reported stopped=True, epochRetired=True, readmitted=1; the permanent regression reproduced the same failure at 14/15. After propagating the original epoch through paced admission, the 15-scenario suite passed a forced Release rebuild and five fresh-process repetitions, including its new-input positive control. A subsequent independent Stop/transfer race reproduced queued=0, epochRetired=False, pacedSurvivors=1. The permanent queue-migration and detached-handoff cases failed at 15/17 before the atomic Stop fence and weak-owner retirement fix, then passed at 17/17 with five fresh-process repetitions. Source-linked scheduler compatibility passed 13/13; ArchitectureProbe passed without raising any baseline.
