# Soft-deadline fixture registration race (2026-11-06)

**Verdict: test synchronization defect; no product change.** The final suite has 18 scenarios / 62 assertions: all original 60 semantic assertions remain, with two additional absolute-deadline checks. This is synthetic in-process acceptance only.

## Preserved failure and falsifiable diagnosis

The original final-run failure (evidence E01) reported:

```text
System.TimeoutException: pipeline idle: active=0 paced=1 delays=2 models=1 sends=0
```

The original binary is preserved; its fingerprint (evidence E02) is `83bc218b6598e63d2f9fcd86b81877abbc0d53447e5fc40a8853fe01685eec7d`. Twenty unchanged full-suite repetitions passed, which did not resolve or disprove the intermittent failure.

Ranked hypotheses were: (1) final virtual-clock advancement races a not-yet-registered quiet poll; (2) product loses a wakeup or uses the wrong quiet deadline; (3) draft completion state does not retire after a timely poll. The first predicts that holding timer registration after resource release reproduces the exact failure, whereas delivering the correctly registered timer at the original deadline completes the work.

### Deterministic RED

A test-only registration gate produced the controlled RED log (evidence E03). At tick 12050, the actual boundary requested its next 50ms poll; resources were already released (`active=0`). A different worker timer was pending at 12149, so the old `ActiveCount == 0 && Clock.Pending > 0` check passed before the boundary timer existed. Advancing 3000ms first, then releasing registration, produced boundary due=15100 at current tick=15050. Another timer was due=15200. `Idle()` does not advance virtual time, reproducing the original `active=0 paced=1 delays=2 models=1 sends=0` timeout.

The captured stack follows `ValidateDraftAsync` → [AwaitBoundaryAsync](<../../src/BotAgent.Engine/Reply/ReplyPipeline.Pacing.Attempt.cs#L81-L90>) → [DelayPacingAsync](<../../src/BotAgent.Engine/Reply/ReplyPipeline.Pacing.Worker.cs#L80-L85>). Product [freshness checks](<../../src/BotAgent.Engine/Reply/ReplyPacingCoordinator.cs#L510-L538>) retain the absolute last-input quiet deadline. A real clock would deliver the next bounded poll; the test froze its clock before that newly registered timer.

The model response deliberately has no text and includes `synthetic-search` with tools disabled. Correct behavior is **zero research and zero sends**, even at completion. The failure was failure to retire paced work under a frozen test clock, not a missing outward reply.

- Instrumented RED source (evidence E04) and fake-clock source (evidence E05) remain outside the normal probe.
- RED binary fingerprint (evidence E06): `ad4519cea11a9f453e17c5acdb6fa33a4c13c71d414879a138e7b2c070076d68`.
- An earlier incremental build retained the prior scenario despite changed source; its natural timeout is retained in that separate log (evidence E07). It is not the controlled proof. All decisive builds use `-t:Rebuild`.

## Repair and controlled GREEN

[ManualClock](<ProbeFakes.cs>) now exposes an explicitly armed registration observation. It completes only after the requested timer is stored, outside the clock lock; it never advances time or completes the timer itself. [SoftDeadline](<Scenarios.cs>) arms the 50ms observation before releasing the model response, then advances to original quiet deadline minus 1ms. It waits for the newly registered 1ms poll whose due time equals the original deadline, checks that work is still pending with no output, then advances exactly 1ms and awaits retirement. Final tick is 14999, never beyond the original quiet deadline. No timeout or assertion was relaxed.

The controlled GREEN replay (evidence E08) uses an isolated copy with the same registration pause as RED. The typed observation stays incomplete and virtual time stays at 12050 while registration is held. Releasing it permits the unmodified exact-deadline checks to complete. Three fresh runs passed (1 (evidence E09), 2 (evidence E10), 3 (evidence E11)); each has 1 scenario / 8 assertions. Its binary fingerprint (evidence E12) is `a49fab823aba1587bf9e88a3489c9d68edfe3cc62a1a89a89ba266f0c2753bbe`. Diagnostic gates/logging are only in ignored replay copies, not final source.

This diagnosis is scoped to the terminal soft-boundary registration followed by a non-advancing `Idle()` assertion. The full suite was rerun, but this repair does not claim to eliminate every possible future fixture race.

## Final exact-candidate validation

Runner (evidence E13) forced a full probe rebuild; build output (evidence E14) has 0 warnings / 0 errors. Persistent native output was checked for all 150 fresh processes:

| Loop | Fresh processes | Scenarios / assertions per process | Result | Boundary evidence |
| --- | ---: | --- | --- | --- |
| SoftDeadline | 100 | 1 / 8 | PASS | First (evidence E15), last (evidence E16) |
| Entire probe | 50 | 18 / 62 | PASS | First (evidence E17), last (evidence E18) |

Total: 1000 scenario executions / 3900 assertion executions, excluding the three controlled GREEN runs. The host restarted after the completed runner notification, so its old job record could not be recollected; all 150 persisted pass summaries were independently read, and native SHA-256 checks revalidated the unchanged binaries and original failure binary.

Candidate fingerprints (evidence E19):

```text
Probe  a98b2172e5e8a7227770898a136573424a41c0e6ab532f9e3c5e3422b885a6cc
Engine c55a627f4bea5fc2e802a6a5ce2d56b3f852c54aa00c5e1bc6fea27e7ed21f7d
Core   8ec867765db9ea3edc1610e576ccb7e13233273f4c88b96a59077d938095f553
```

Final code source hashes:

```text
b93cbcad450debcbcf8f22acd7de967059293bf6b916e4189f33ab8c6667d169  tests/BotAgent.ReplyPipelinePacingProbe/Scenarios.cs
4b72f3f8e3b4a4cf8c7967f2dc241097274aeaeeab4dc0bca60b8e05a15520f1  tests/BotAgent.ReplyPipelinePacingProbe/ProbeFakes.cs
```

All original 29 assertion call-sites in the edited scenario file were preserved verbatim; the remaining scenario files, entry point, fixture and product source were not edited. Original scenario copy (evidence E20), fixture copy (evidence E21) and entry-point copy (evidence E22) support the independent comparison. Evidence artifacts are local/ignored, not public deliverables. No production data, network, commit, push or deployment was involved.

## Local evidence index

Machine-local, ignored evidence is not part of a clean checkout. Labels above map to repository-root-relative paths below; original logs and hashes are unchanged. These are evidence locations, not a claim that a clean checkout contains the artifacts.

```text
E01  .tmp/final-verification-4/ReplyPipelinePacingProbe.log
E02  .tmp/pipeline-final-diagnosis/original-binary.sha256
E03  .tmp/pipeline-final-diagnosis/forced-red-registered.log
E04  .tmp/pipeline-final-diagnosis/red-Scenarios.cs
E05  .tmp/pipeline-final-diagnosis/red-ProbeFakes.cs
E06  .tmp/pipeline-final-diagnosis/registered-red-binary.sha256
E07  .tmp/pipeline-final-diagnosis/forced-red.log
E08  .tmp/pipeline-final-diagnosis/run-controlled-green.sh
E09  .tmp/pipeline-final-diagnosis/controlled-green-1.log
E10  .tmp/pipeline-final-diagnosis/controlled-green-2.log
E11  .tmp/pipeline-final-diagnosis/controlled-green-3.log
E12  .tmp/pipeline-final-diagnosis/controlled-green-binary.sha256
E13  .tmp/pipeline-final-diagnosis/run-green.sh
E14  .tmp/pipeline-final-diagnosis/green-build.log
E15  .tmp/pipeline-final-diagnosis/green-soft-1.log
E16  .tmp/pipeline-final-diagnosis/green-soft-100.log
E17  .tmp/pipeline-final-diagnosis/green-full-1.log
E18  .tmp/pipeline-final-diagnosis/green-full-50.log
E19  .tmp/pipeline-final-diagnosis/green-binaries.sha256
E20  .tmp/pipeline-final-diagnosis/original-Scenarios.cs
E21  .tmp/pipeline-final-diagnosis/original-Fixture.cs
E22  .tmp/pipeline-final-diagnosis/original-Program.cs
```
