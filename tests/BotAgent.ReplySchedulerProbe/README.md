# Reply scheduler regression probe

Executable, source-linked scheduler isolation tests for reply pacing. No network, provider, database, persisted conversations, or production source lease is used. All message content and identifiers are synthetic.

## Run

From the issue worktree root:

```sh
dotnet build tests/BotAgent.ReplySchedulerProbe/BotAgent.ReplySchedulerProbe.csproj -c Release -t:Rebuild --artifacts-path .tmp/scheduler-probe-artifacts
dotnet .tmp/scheduler-probe-artifacts/bin/BotAgent.ReplySchedulerProbe/release/BotAgent.ReplySchedulerProbe.dll
```

Always force a rebuild before executing the generated DLL: file edits may preserve timestamps, so an incremental build can otherwise reuse stale code. Build artifacts stay separate from other probe workers.

## Boundaries covered

The production pacing enqueue/worker/attempt/coordinator, engine clock, and resizable gate are source-linked unchanged. The existing fixture replaces host ownership, permissions, conversation storage, generation, and delivery. An injected manual clock drives all business deadlines; event-gated generation and prepare/send callbacks control races. Short wall-clock waits only dispatch asynchronous continuations or enforce a five-second test watchdog.

1. Group quiet restarts after same-human input and snapshots the latest sequence.
2. Private quiet admits at 1,500 ms, not 1,499 ms, independent of group probability.
3. Context-only input never creates a worker but renews an already admitted turn's quiet deadline.
4. Quiet work cannot occupy the single global generation slot and starve a ready conversation.
5. An explicit request cancels a preemptible ordinary generation before its send.
6. A stale generated draft is discarded and only the latest recomputation sends.
7. Freshness is checked again between asynchronous send preparation and the final synchronous send boundary.
8. Committed effects retain their generation, run once, wait for same-human quiet, and ignore another human's quiet window.
9. Stop cancels active generation and fences subsequent ingress and sends.
10. Disable transfers queued explicit work exactly once, drops ordinary work, and permits fresh work after reenable.
11. Disable does not duplicate or lose already committed effects and their final result.
12. Revoked permission fences the final boundary of an existing generation.
13. Failed delivery does not start cooldown; a complete delivery does.

## Evidence and limits

The original project failed forced compilation with CS5001 because no executable entry existed. Restoring the synthetic entry and harness produces a Release build with warnings treated as errors and **13/13 scenario groups passing**. Five consecutive repeat executions also passed 13/13; a final forced rebuild and run matched unchanged enqueue/worker/attempt/coordinator/commit-boundary source fingerprints before and after execution. No production code or architectural thresholds were changed for this probe.

These tests prove scheduler state/resource/commit-boundary behavior through the fixture. They do not exercise the real provider, source gateway, messaging transport, image/TTS preparation, or persistence, and do not replace the real pipeline probe or live product acceptance. On failure the executable returns a nonzero exit code and reports the synthetic scenario and assertion only.
