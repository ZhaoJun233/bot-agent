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
dotnet run --project tests/BotAgent.ProductionSpecProbe/BotAgent.ProductionSpecProbe.csproj -c Release
node tests/BotAgent.FrontendProbe/probe.mjs
dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release
QQCHAT_IT_ONLY=s1 dotnet tests/BotAgent.IntegrationHarness/bin/Release/net8.0/BotAgent.IntegrationHarness.dll
```

The integration harness starts a separate bot process; build the core project first. The synthetic `s1` scenario is a smoke test, not a substitute for the full integration suite. PRs must report skipped or environment-limited checks. `.github/workflows/ci.yml` runs this subset on `dev`, `main`, and PRs to either branch; inspect the actual run before calling it green.

## Publishing limitation

The private workspace's legacy desensitized publisher currently writes a generated snapshot directly to public `main`, may rewrite commit subjects to a generic name, and can force-push on divergence. It **does not** implement the reviewed `dev` → `main` process above. Until that publisher is updated and its privacy checks are preserved, maintainers must not use it as evidence that a PR or CI gate passed. Keep the existing two independent privacy scans and remote re-verification as mandatory release checks; never publish the private workspace directly or disable a scan to make a release pass. The mismatch is tracked in issue #2.