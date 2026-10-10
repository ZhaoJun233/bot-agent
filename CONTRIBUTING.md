# Contributing

This repository is a privacy-filtered public mirror of a chat application. Never use production conversations, member records, protocol event bodies, logs, credentials, or screenshots as test fixtures or issue attachments. Use synthetic examples only. Report suspected credential or privacy exposure privately to the maintainer, not in a public issue.

## Branches and review

- Start a short-lived `fix/*`, `feat/*`, or `docs/*` branch from `dev`.
- Open a focused PR into `dev`, link the issue, and describe behavior, risks, tests, migration, and rollback.
- After review and green checks, promote `dev` to `main` with a separate reviewed PR. Do not merge by bypassing required checks, and do not force-push `main`.
- Write descriptive source commit subjects such as `fix(persistence): reuse settings read for presence check` and include the reason and verification in the body when useful. Do not bundle unrelated work in one commit.

## Local verification

Run these commands at the repository root with .NET 8, Node.js 22, and Python 3.12. On Windows, use PowerShell 7 (`pwsh`) and check `$LASTEXITCODE` after every native command; stop the affected verification chain on failure. Do not inherit production configuration or credentials into synthetic runs: clear `BOTAGENT_*`, `QQCHAT_*`, `OPENAI_*`, `MINIMAX_*`, `TTS_*`, `HARNESS_*`, and `HEALTH_PORT` in an isolated verification process. Data-dependent standalone probes must use fresh synthetic temporary roots; the integration scenarios create their own roots, so do not supply a parent data-root alias that overrides them.

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.ArchitectureProbe/BotAgent.ArchitectureProbe.csproj -c Release
dotnet run --project tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release
dotnet run --project tests/BotAgent.ParticipationProbe/BotAgent.ParticipationProbe.csproj -c Release
dotnet run --project tests/BotAgent.BridgeProbe/BotAgent.BridgeProbe.csproj -c Release
python -m unittest discover -s tests/BotAgent.BridgeProbe -p "test_*.py"
dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release
dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release
dotnet run --project tests/BotAgent.ReverseTransportProbe/BotAgent.ReverseTransportProbe.csproj -c Release
dotnet run --project tests/BotAgent.DeadlineGateProbe/BotAgent.DeadlineGateProbe.csproj -c Release
dotnet run --project tests/BotAgent.FeishuRemediationProbe/BotAgent.FeishuRemediationProbe.csproj -c Release
dotnet run --project tests/BotAgent.SettingsScopeProbe/BotAgent.SettingsScopeProbe.csproj -c Release
dotnet run --project tests/BotAgent.SsrfProbe/BotAgent.SsrfProbe.csproj -c Release
dotnet run --project tests/BotAgent.ConcurrencyStressProbe/BotAgent.ConcurrencyStressProbe.csproj -c Release
dotnet run --project tests/BotAgent.ChaosFaultProbe/BotAgent.ChaosFaultProbe.csproj -c Release
dotnet run --project tests/BotAgent.PipelineEval/BotAgent.PipelineEval.csproj -c Release
node tests/BotAgent.FrontendProbe/probe.mjs
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
```

The integration harness starts a separate bot process; rebuild Headless before integration validation (`dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release -t:Rebuild`), then build the harness. Building the harness alone does not rebuild the bot. The synthetic integration suite runs the 50 registered scenarios from S1–S52: S2 is covered inside S1; S42 (official channel) is explicitly excluded from both the harness entry point and the CI matrix because its synthetic gateway timing/timeout and whitelist-reset assertions are not yet reliable. S52 covers unified platform switches, migration, precedence, mute/disable, persistence, and restart with synthetic fixtures; it is not official-channel protocol acceptance. A green run is **not** evidence of S42 end-to-end coverage. `.github/workflows/ci.yml` runs the probes explicitly listed in its `verify` job and deterministic evaluations, and partitions the enabled integration scenarios into parallel matrix slices (`Core & Chat`, `Media & Tools`, `Governance & Multi-Platform`, including S52) under `integration` on `dev`, `main`, and PRs. Integration requires the `verify` job to pass; inspect the actual run before calling it green. A local directed run is not evidence that the entire matrix or GitHub CI passed.

For a directed run in `pwsh`, set `QQCHAT_IT_ONLY` to existing scenario tags (for example, `s1,s36,s52`) and `QQCHAT_BOT_DLL` to the absolute path of this checkout's rebuilt Release Headless DLL. Check for nonzero executed assertions as well as exit code 0, and report exactly which scenarios ran. Do not use `--serve-external` for routine synthetic validation.

The `verify` job also restores and runs `ReviewRemediationProbe`, `ReverseTransportProbe`, `DeadlineGateProbe`, `FeishuRemediationProbe`, `SettingsScopeProbe`, and `SsrfProbe`. These focused synthetic checks do not establish production or excluded official-channel end-to-end coverage. Core and the standalone integration harness each explicitly enable transitive NuGet auditing (`all`, severity threshold `high`) and treat high/critical findings (`NU1903`/`NU1904`) as restore errors; these are per-project gates, not proof that every repository package graph has been audited; `dotnet list package --vulnerable` output is informational and its exit code alone is not an audit gate. The isolated native-browser fixture under `tests/BotAgent.FrontendProbe` remains a manual check, not a browser CI job.

## Local source-image build boundary

`src/BotAgent.Headless/Dockerfile` expects a repository-root-shaped build context. It restores and publishes Headless with its actual Core/Platforms/Storage project references and the two embedded scripts (`tools/pi-bridge.py`, `tools/server-files.py`). Storage is referenced with the explicit `StorageModule` alias: the host's legacy database, store and file types are compatibility entries over Storage implementations. Host-only settings/audit/provider/trace adapters remain; the host passes its original typed `AppSettings` validation into the Storage legacy importer. Model is not yet adopted. The alias does not exclude either source tree from architecture scanning. Use only a screened source context that excludes production data, logs, secrets, and unrelated artifacts. For local verification, assemble a fresh allowlisted temporary context rather than sending an unreviewed private workspace to a Docker engine.

Storage paths are explicitly bound once to the host's resolved runtime root before database/import/store use; same-root initialization is idempotent, another root is rejected, and an unbound path read fails closed. The module does not independently rediscover environment/workspace roots. Host startup binds Storage time and logging to the existing host sources. `ReviewRemediationProbe` checks changed-environment import isolation, original settings-type validation and retry/archive behavior, actual Storage method ownership, and the existing repository behavior probes. Architecture checks retain both source trees and reject SQL/file IO copied back into any migrated host compatibility entry. Public store implementations permit the sealed host compatibility subclasses; this source migration does not establish old-assembly mixing compatibility or complete schema crash recovery.

`ReviewRemediationProbe` includes synthetic database path binding, cross-facade reads/writes and dedup rollback, nested-write rejection, newer-schema rejection without mutation, and backup restoration in an isolated child process. Storage receives the host-resolved database path and stable host clock; default backup paths follow that bound database, not re-read environment roots. Nested writes and backups inside a write callback are rejected explicitly; use the supplied connection for work in the existing transaction. These checks do not establish unified/versioned configuration saves, historical-schema crash recovery, old-binary compatibility, production restore, or whole Wave 1 acceptance. SQLite's existing 30-second wait remains; no bounded queue or cancellation contract is claimed.

The source-image route requires a local Linux Docker engine and compilation resources. Do not start services or alter Docker configuration as an implicit validation step. Keep the existing `Dockerfile.app` artifact-only route for low-memory deployment hosts; do not compile source on those hosts. A successful local publish is not a Linux image-build result, and an image build is not permission to run, deploy, or publish it or proof of resource/rollback acceptance.

## Publishing and release coverage

The private workspace publisher uses `git archive HEAD` to generate a snapshot of the committed source in an independent temporary directory, performs the privacy audits there, and uses an ordinary fast-forward push to public `dev`. It preserves descriptive source commit subjects; it must not directly publish to or force-push `main`. Promotion from `dev` to `main` requires the separate reviewed PR and its required checks described above. Keep the independent blacklist/category and content privacy checks plus remote re-verification mandatory; never publish the private workspace directly or disable a scan to make a release pass.

A successful archive, privacy audit, or snapshot push is not evidence that a PR or CI gate passed. Uncommitted work is not included in `git archive HEAD`. Release verification must identify the published revision and its actual CI/PR checks, and disclose excluded scenarios such as S42 rather than claiming full S1–S52 coverage.
