# ReplyPipeline pacing integration probe

Synthetic, in-process acceptance of the actual `ReplyPipeline` ingress, model, tool and transport path. This is not a mock of the coordinator and does not use live QQ traffic, an HTTP host, external model/research services or persistence.

## Run

From the repository root, with .NET 10 SDK:

```sh
dotnet build tests/BotAgent.ReplyPipelinePacingProbe/BotAgent.ReplyPipelinePacingProbe.csproj -c Release -t:Rebuild --artifacts-path .tmp/pipeline-probe-artifacts
dotnet .tmp/pipeline-probe-artifacts/bin/BotAgent.ReplyPipelinePacingProbe/release/BotAgent.ReplyPipelinePacingProbe.dll
```

Optional positional arguments select exact scenario names. With no arguments all scenarios run, fail-fast.

```sh
bash tests/BotAgent.ReplyPipelinePacingProbe/run-evidence.sh
```

The evidence command builds once, fingerprints the three relevant DLLs, runs three fresh probe processes, verifies that those binaries did not change, and retains native output under a timestamped `.tmp/pipeline-probe-artifacts/evidence/` directory. Its `pipefail` behavior preserves a nonzero native exit. It does not commit, deploy, or enable the feature.

## Observable acceptance matrix

| Scenario | Observable invariant |
| --- | --- |
| continuation-context | Three same-speaker inputs wait for quiet, reach the final context, and produce one reply. |
| first-send-stale | Input arriving during source preparation invalidates the draft at the real commit boundary; replacement sees newest input. |
| explicit-preemption | Another speaker's explicit request cancels an ordinary model attempt; a provider ignoring cancellation still cannot send its late result. |
| same-speaker-promotion | Same-speaker explicit continuation upgrades the request without self-cancellation; stale output is still rejected. |
| effects-not-preempted | A committed transport waiting for ACK is not canceled or replayed by an explicit successor. |
| inline-effects-protected | A real inline research effect is not canceled by explicit preemption; both requests finish once. |
| stale-tool-draft | An obsolete draft executes no research; a fresh draft positively reaches the actual research port. |
| accepted-result-bypass | A real background research completion reaches the model at the same virtual instant despite 0% ordinary admission and an active full-reply cooldown; the research note is preserved. |
| soft-deadline | Continuous input may evaluate a tool-disabled draft at 12 seconds, not before; typed timer registration proves retirement at the original quiet deadline, with no research or send for a silent tool-like result. |
| global-slot | A waiting group does not hold the sole global model slot needed by another conversation. |
| full-ack-cooldown | A successful explicit chat reply creates cooldown; rejected continuous input cannot resurrect at expiry. |
| failed-ack-no-cooldown | Failed transport ACK does not create a full-reply cooldown. |
| last-segment-cooldown | Both real text fragments send; the final ACK, not first send, anchors cooldown. |
| partial-segment-no-cooldown | The second fragment's failed ACK makes the logical reply incomplete, so it does not start full-reply cooldown. |
| mode-off | Waiting ordinary work is canceled; waiting explicit work transfers once to the legacy path. |
| mode-off-generating | Mode-off rejects a late ordinary result and transfers a generating explicit request once. |
| mode-off-emitting | Mode-off preserves an already committed send without replay. |
| shutdown-late-result | Shutdown rejects late model output and does not create a legacy handoff. |

## Fixture controls and limits

- A manually advanced `IClock` controls policy time. The process-global engine clock means scenarios must run sequentially. Wall-clock waits only bound test failure.
- `NextModel` allows one 50 ms scheduler poll after a policy deadline. Negative assertions are made before that helper. Exact zero-wait/bypass tests use the raw model channel and do not advance time.
- `SoftDeadline` arms a typed 50ms timer-registration observation before returning model output, then waits for the exact final 1ms quiet poll before advancing the last millisecond. It never treats a generic pending timer or a released model slot as proof that this boundary timer exists.
- Task-completion barriers pause model completion, transport preparation, ACKs and real research-port calls. The model intentionally ignores cancellation so the product must enforce stale-result rejection.
- The fixture wires real `PlainSender`, `ReplyDelivery`, `AgentTurnLoop`, background queue, approval and participation components. Unused storage/panel/service ports are neutral proxies.
- Reflection is limited to installing the clock and reading the private pending-work count as a scheduler barrier. It does not inject outcomes or invoke the private accepted-result callback; research results arrive through the real queue.
- Probability tests here use deterministic endpoints (0%/100%). Statistical/monotonic sampling, queue overload, numeric configuration persistence, browser UI, all platform implementations, voice/image transport, permission revocation and live-host behavior need their own probes. This suite is not whole-feature or deployment sign-off.

Current suite: **18 scenarios / 62 assertions**. The [soft-deadline diagnosis](<SOFT-DEADLINE-DIAGNOSIS.md>) preserves the deterministic fixture counterexample, exact-deadline repair, 100 targeted / 50 full fresh-process repeats, and controlled RED/GREEN comparison. Always force `Rebuild` because source-writing tools may preserve timestamps.

The original acquired-send lease failure remains recorded in [COUNTEREXAMPLE.md](<COUNTEREXAMPLE.md>) and its two regression scenarios must remain intact.
