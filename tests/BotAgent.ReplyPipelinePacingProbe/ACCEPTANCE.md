# Scoped pipeline integration acceptance

**Verdict: PASS for the compiled synthetic integration candidate below.** This is not whole-issue, production, browser/UI or deployment sign-off.

## Latest revalidation — 2026-11-06

A later frozen full-suite run exposed a fixture timer-registration race, not a product change. [The complete diagnosis and evidence](<SOFT-DEADLINE-DIAGNOSIS.md>) preserve the original timeout, deterministic controlled RED, test-only typed registration repair, and controlled GREEN. Final suite: **18 scenarios / 62 assertions**; all original 60 assertions remain, plus two original-quiet-deadline checks.

A forced rebuild passed with 0 warnings / 0 errors. **100 targeted fresh processes** each passed 1 scenario / 8 assertions, and **50 full fresh processes** each passed 18 scenarios / 62 assertions. Three additional controlled-registration-pause runs passed. Persisted logs and binary checks remain available; the original completed job record expired across a host restart. The old results below are retained as historical evidence only, not the final candidate fingerprint.

## Historical reproduction and evidence — 2026-11-05

```sh
bash tests/BotAgent.ReplyPipelinePacingProbe/run-evidence.sh
```

The build completed with **0 warnings and 0 errors**. The evidence script exited **0**. Three independent fresh-process runs passed, and the script's final SHA-256 checks confirmed the probe, Engine and Core DLLs remained unchanged during these runs.

| Repeat | Scenarios | Assertions | Result | Evidence |
| --- | ---: | ---: | --- | --- |
| 1 | 18 | 60 | PASS | Native output (evidence E01) |
| 2 | 18 | 60 | PASS | Native output (evidence E02) |
| 3 | 18 | 60 | PASS | Native output (evidence E03) |

Total: **54 scenario executions, 180 assertion executions**.

- Build output (evidence E04)
- Environment (evidence E05)
- Binary fingerprints (evidence E06)
- [Coverage matrix and fixture limits](<README.md>)
- [Preserved original segmented-send counterexample](<COUNTEREXAMPLE.md>)

## Exact evaluated candidate

The worktree contained concurrent owner changes, so base HEAD is provenance only, not a claim of a clean committed candidate. Acceptance is bound to these compiled DLLs. Later product edits require a fresh build/run.

Environment (UTC timestamp, base HEAD, branch, SDK):

```text
2026-11-05T15:06:14Z
b80d7b271bae58bf7f0b8f4261b03f6c4ffe0199
fix/issue-151-reply-pacing
10.0.101
```

SHA-256:

```text
db79e7b87d3af2138b3a04f1cb1f02908c585cfc5c7d03d298842c0f662f4cfc *.tmp/pipeline-probe-artifacts/bin/BotAgent.ReplyPipelinePacingProbe/release/BotAgent.ReplyPipelinePacingProbe.dll
59b56c14aee027df6ccaa05119b2662c9935115e55bb6a76bb8faadd79e100fe *.tmp/pipeline-probe-artifacts/bin/BotAgent.ReplyPipelinePacingProbe/release/BotAgent.Engine.dll
a8a09a2b66a352ea2f7814a77b0a8c2bc67f87509c451a7b02dfc93db30237f2 *.tmp/pipeline-probe-artifacts/bin/BotAgent.ReplyPipelinePacingProbe/release/BotAgent.Core.dll
```

## Findings and retained regression tests

- **Product defect found and reverified:** only the first fragment of a split reply sent because a later segment revalidated an already-acquired send lease as terminal. The product owner fixed the lease handling. Both unchanged segment scenarios now pass: final-ACK cooldown anchoring and incomplete/partial delivery without cooldown. The RED record is retained separately.
- **Positive controls prevent vacuous tool tests:** a fresh draft demonstrably reaches the real research port; actual background completion bypasses ordinary admission/cooldown and carries its note into model input. Obsolete drafts reach neither research nor transport.
- **Cancellation-insensitive provider:** the fake model deliberately ignores cancellation; late ordinary results are still rejected by the product at outbound/effect boundaries.
- **Fixture corrections, not product concessions:** initialize neutral-proxy out parameters; distinguish same-speaker promotion from cross-speaker preemption; observe the separate model research-note argument; wire the real background queue; wait on timer/model/ACK barriers instead of transient scheduler idleness. Negative safety assertions were preserved.

## Scope and limits

Actual ingress, scheduling, context construction, model calls, inline/background tools, source preparation, segmented sends, ACKs, mode-off handoffs and shutdown are exercised in process. All inputs, IDs and tool results are synthetic. No HTTP/network, real adapters, production storage or live conversations were used.

Sampling endpoints are deterministic (0%/100%); statistical probability, configuration round trips, queue overload, browser UI, permission-revocation matrices, all platform adapters, voice/image sends and whole-host compatibility remain outside this probe. The engine uses a process-global test clock, so scenarios run sequentially.

Only the delegated test directory and its isolated generated artifacts were written by the acceptance agent. No product file edits, commit, push, merge or deployment were performed.

## Local evidence index

Machine-local, ignored evidence is not part of a clean checkout. Labels above map to repository-root-relative paths below; original logs and hashes are unchanged. These are evidence locations, not a claim that a clean checkout contains the artifacts.

```text
E01  .tmp/pipeline-probe-artifacts/evidence/20261105T150614Z/run-1.txt
E02  .tmp/pipeline-probe-artifacts/evidence/20261105T150614Z/run-2.txt
E03  .tmp/pipeline-probe-artifacts/evidence/20261105T150614Z/run-3.txt
E04  .tmp/pipeline-probe-artifacts/evidence/20261105T150614Z/build.txt
E05  .tmp/pipeline-probe-artifacts/evidence/20261105T150614Z/environment.txt
E06  .tmp/pipeline-probe-artifacts/evidence/20261105T150614Z/candidate.sha256
```
