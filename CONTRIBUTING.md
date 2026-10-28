# Contributing

This repository is a privacy-filtered public mirror of a chat application. Never use production conversations, member records, protocol event bodies, logs, credentials, or screenshots as test fixtures or issue attachments. Use synthetic examples only. Report suspected credential or privacy exposure privately to the maintainer, not in a public issue.

## Branches and review

- Start a short-lived `fix/*`, `feat/*`, or `docs/*` branch from `dev`.
- Open a focused PR into `dev`, link the issue, and describe behavior, risks, tests, migration, and rollback.
- After review and green checks, promote `dev` to `main` with a separate reviewed PR. Do not merge by bypassing required checks, and do not force-push `main`.
- Write descriptive source commit subjects such as `fix(persistence): reuse settings read for presence check` and include the reason and verification in the body when useful. Do not bundle unrelated work in one commit.

## Local verification

Run these commands at the repository root with .NET 8 and Node.js 22:

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet run --project tests/BotAgent.ParticipationProbe/BotAgent.ParticipationProbe.csproj -c Release
dotnet run --project tests/BotAgent.BridgeProbe/BotAgent.BridgeProbe.csproj -c Release
python -m unittest discover -s tests/BotAgent.BridgeProbe -p "test_*.py"
dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release
dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release
dotnet run --project tests/BotAgent.ConcurrencyStressProbe/BotAgent.ConcurrencyStressProbe.csproj -c Release
dotnet run --project tests/BotAgent.ChaosFaultProbe/BotAgent.ChaosFaultProbe.csproj -c Release
dotnet run --project tests/BotAgent.PipelineEval/BotAgent.PipelineEval.csproj -c Release
node tests/BotAgent.WebUiProbe/probe.mjs
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
```

The integration harness starts a separate bot process; build the core project first. The synthetic integration suite runs the enabled scenarios from S1–S51: S2 is covered inside S1; S42 (official channel) is explicitly excluded from both the harness entry point and the CI matrix because its synthetic gateway timing/timeout and whitelist-reset assertions are not yet reliable. A green run is **not** evidence of S42 end-to-end coverage. `.github/workflows/ci.yml` runs the probes explicitly listed in its `verify` job and deterministic evaluations, and partitions the enabled integration scenarios into parallel matrix slices (`Core & Chat`, `Media & Tools`, `Governance & Multi-Platform`) under `integration` on `dev`, `main`, and PRs; inspect the actual run before calling it green.

The `verify` job also restores and runs `ReviewRemediationProbe`, `ReverseTransportProbe`, `DeadlineGateProbe`, `FeishuRemediationProbe`, `SettingsScopeProbe`, and `SsrfProbe`. These focused synthetic checks do not establish production or excluded official-channel end-to-end coverage. Core and the standalone integration harness each explicitly enable transitive NuGet auditing (`all`, severity threshold `high`) and treat high/critical findings (`NU1903`/`NU1904`) as restore errors; these are per-project gates, not proof that every repository package graph has been audited; `dotnet list package --vulnerable` output is informational and its exit code alone is not an audit gate. The Web UI static probe under `tests/BotAgent.WebUiProbe` checks DOM id wiring, auth endpoint alignment and settings-key alignment by reading source text. It does not start a browser or a process, so runtime behaviour remains a manual check.

## Publishing and release coverage

The private workspace publisher uses `git archive HEAD` to generate a snapshot of the committed source in an independent temporary directory, performs the privacy audits there, and uses an ordinary fast-forward push to public `dev`. It preserves descriptive source commit subjects; it must not directly publish to or force-push `main`. Promotion from `dev` to `main` requires the separate reviewed PR and its required checks described above. Keep the independent blacklist/category and content privacy checks plus remote re-verification mandatory; never publish the private workspace directly or disable a scan to make a release pass.

A successful archive, privacy audit, or snapshot push is not evidence that a PR or CI gate passed. Uncommitted work is not included in `git archive HEAD`. Release verification must identify the published revision and its actual CI/PR checks, and disclose excluded scenarios such as S42 rather than claiming full S1–S51 coverage.