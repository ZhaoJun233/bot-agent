# Platform Switch Unification

## Objective and Scope
Centralize Enabled and ChatEnabled in platform instance policies. Keep platform enablement distinct from chat mute. No deployment, production configuration changes or weaker credential/whitelist gates.

## Current Architecture and Impact
PlatformPolicyResolver currently intersects legacy switches with policy overrides. CompositionRoot builds optional adapters from legacy fields. Panel settings saves both independently; HealthReportService checks legacy chat fields. SettingsBox and SettingsHotReload remain the serialized persistence/publication path.

## Contracts and Data Flow
Introduce PlatformSwitchSchemaVersion=1 and Services/Platforms/PlatformSwitchSettings. Startup and panel mutation migrate existing policy switches to the old effective intersection once. Version 1 explicit account-scoped switches become authoritative. Mirror standard-account policy values into legacy fields for adapter compatibility. Missing policies retain legacy fallback, unknown platforms fail closed, and nonstandard accounts never overwrite standard-account fields. GET settings projects normalized standard rows without mutating live settings. POST platformPolicies takes precedence over legacy switch keys; legacy-only clients update matching policy switches. Keep chat preference while platform is disabled. Local whitelist remains a readiness prerequisite.

## Milestones
1. Add migration, precedence, account isolation, hot-reload and panel tests.
2. Add helper/version and startup normalization; update resolver and panel save/read paths.
3. Remove duplicate controls; preserve policy drafts if optional platform status fails.
4. Align adapter construction, local endpoint, status, health-report gate and inbound block logs.
5. Add synthetic settings integration checks for save, mute, disable and restart persistence.
6. Sync bilingual READMEs and run regressions.

## Risks and Boundaries
Migration must never turn an old disabled platform on. Null fields fall back to legacy values. Preserve whitelist, features and action allowlists. Optional adapters absent at startup may need restart; enablement is not connectivity. Global AI behavior remains separate. Keep a configuration backup when downgrading: old binaries may reintroduce legacy intersections.

## Verification
- dotnet build tests/BotAgent.SafetyProbe/BotAgent.SafetyProbe.csproj -c Release --no-restore
- dotnet tests/BotAgent.SafetyProbe/bin/Release/net8.0/BotAgent.SafetyProbe.dll
- node --check src/BotAgent.Headless/wwwroot/app.js
- node tests/BotAgent.FrontendProbe/probe.mjs
- dotnet build tests/BotAgent.IntegrationHarness/BotAgent.IntegrationHarness.csproj -c Release --no-restore
- Run integration s52 (new), s48 (local), s50 (Feishu), s15 (settings), and health-report probes. Existing official s42 is disabled in harness because it can hang; do not claim it passed.
- Run ArchitectureProbe without raising baseline thresholds.
