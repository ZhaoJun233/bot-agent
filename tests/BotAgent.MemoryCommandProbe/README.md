# Synthetic explicit-memory command probe

This executable links the production command-service source and uses the real authorization core with an in-memory persistence adapter, synthetic transport-attested envelopes, source leases, and opaque synthetic identities. It does not start a bot, contact a platform/model, read any history, enumerate members/groups, or inspect production data.

Run from the worktree root after coordinating the shared build slot:

```sh
dotnet build tests/BotAgent.MemoryCommandProbe/BotAgent.MemoryCommandProbe.csproj -c Release
dotnet tests/BotAgent.MemoryCommandProbe/bin/Release/net10.0/BotAgent.MemoryCommandProbe.dll
```

Coverage includes synchronous reserved-command consumption; missing, stale, or mismatched envelope/lease rejection; native opaque identity and reply targeting despite conflicting scalar QQ fields; uniform group nondisclosure; private exact source/target review and explicit source/group-audience confirmation; finite preference vocabulary; independently configured administrator-only global controls without authority over another subject; target revoke, whole-fact rejection/forgetting, version edit/reconfirmation, expiration, disabled-state management, and legacy anti-resurrection suppression.

Scope tokens retain the exact driver platform/account/instance and conversation kind/native target/thread, URI-escaping each field. No directories of possible scopes are queried. Private `//memory help` documents the full command grammar. Integration into the host and outbound/model invalidation guards belongs to their separate probes.

Global `start`/`stop` governance is separately configured by the deployment owner via `BOTAGENT_UNIFIED_MEMORY_ADMIN`: a JSON object with exact case-sensitive keys `PlatformId`, `AccountScope`, `InstanceId`, and `NativeUserId`. The host matches all four values exactly to the attested private sender. Missing or invalid configuration grants nobody this governance role; Agent/tool permissions do not grant it. Actual configured values are never echoed by the command service.
