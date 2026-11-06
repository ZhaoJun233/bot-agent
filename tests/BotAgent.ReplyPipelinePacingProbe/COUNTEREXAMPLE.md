# Preserved counterexample: acquired segmented-send lease

## Observed RED (2026-11-05)

Synthetic-only integration run. No production data, network or storage was used. Product source was not modified by the acceptance agent.

```sh
dotnet build tests/BotAgent.ReplyPipelinePacingProbe/BotAgent.ReplyPipelinePacingProbe.csproj -c Release --artifacts-path .tmp/pipeline-probe-artifacts --nologo -v:quiet
dotnet .tmp/pipeline-probe-artifacts/bin/BotAgent.ReplyPipelinePacingProbe/release/BotAgent.ReplyPipelinePacingProbe.dll last-segment-cooldown
```

Build succeeded with 0 warnings and 0 errors. The scenario failed before the final-ACK timing assertions: a two-sentence response completed its pipeline work after only one transport send.

```text
SCENARIO last-segment-cooldown
InvalidOperationException: two transport segments observed (actual=1, preparations=1, lengths=22)
```

Temporary first-chance diagnostics identified the swallowed interruption (diagnostics were removed from the fixture afterward):

```text
ReplyPacingInterruptedException: reply_pacing_interrupted
ReplyPipeline.PacedReplyAttempt.ThrowIfDiscarded -> ReplyPipeline.Pacing.Attempt.cs:104
ReplyPipeline.PacedReplyAttempt.AwaitBoundaryAsync -> ReplyPipeline.Pacing.Attempt.cs:61
ReplyCommitBoundary.BeforePrepareAsync -> ReplyCommitBoundary.cs:52
PlainSender.SendTextAtCommitAsync -> PlainSender.cs:303
```

The second segment rechecked coordinator freshness after `FirstSend` had been set. `AlreadySending` was interpreted as terminal before the attempt recognized its already-acquired send lease. `PlainSender` recovered a partial send, hiding this exception from the pipeline's outer error log. This is why an observable positive assertion on *two transport segments* is necessary; a one-message cooldown test cannot detect the defect.

## Fingerprints observed immediately after diagnosis

These fingerprints describe the inspected post-failure source snapshot, not a clean commit or an immutable full build. The worktree contained concurrent owner changes.

- Base HEAD: `b80d7b271bae58bf7f0b8f4261b03f6c4ffe0199`
- Branch: `fix/issue-151-reply-pacing`
- .NET SDK: `10.0.101`

```text
3ac3c4fc894aa8c8e2400afe3dd2ec5a6441e7282e042dc3e95af90b2fcd1010  tests/BotAgent.ReplyPipelinePacingProbe/SegmentScenarios.cs
04991033e0c07efbcdc1c9977cf34854e27cbf4e0cdae2407a77d6b048760868  src/BotAgent.Engine/Reply/ReplyPipeline.Pacing.Attempt.cs
c2687260e881da87828e27be02c8fbf256fadcd46068f6ab58fb768e98e989f0  src/BotAgent.Engine/Reply/ReplyPacingCoordinator.cs
```

## Regression contract

Keep `last-segment-cooldown` and `partial-segment-no-cooldown`: both must reach the actual second source send. The first withholds its ACK for five virtual seconds, then checks cooldown against the final ACK. The second fails its final ACK and checks that a partial delivery does not create a full-reply cooldown. Preserve this RED record even after the owner fixes the product.
