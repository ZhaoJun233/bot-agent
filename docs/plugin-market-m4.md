# Plugin market M4: host governance

## Authority and meaning

The host reads only the configured official catalog's sibling `governance.json` as authority. Repository descriptors and imported JSON are not governance authorities. Records use exact lower-camel fields `id, version, sha256, status, reason, updatedAt`; the root uses `schemaVersion: 1, generatedAt, entries`. Unknown/duplicate fields, duplicate tuples, wrong versions, invalid UTC timestamps, uppercase hashes and unsupported statuses are rejected. Feed limits: 2 MiB, 1024 entries, plugin ID up to 80 characters, semantic version up to 96 characters, nonempty reason up to 500 UTF-16 units. Reasons reject Unicode control characters; ordinary Unicode is preserved. UTC RFC3339 timestamps accept Z with optional 1–7 fractional digits.

- Deprecated and ordinary withdrawal warn only; explicit acquisition remains allowed.
- Revoked denies every acquisition. Identity is (id, version, SHA256); revoked SHA256 additionally matches globally across source or ID changes.
- Existing installations keep state/configuration and enable intent: no silent removal, disable or unload.
- No known match is active only with a fresh snapshot; otherwise unknown. Neither means a security endorsement.

## Freshness and durable safety

Successful checks remain fresh for **15 minutes**, only while generatedAt is no older than **7 days**. The exact 15-minute boundary, or exceeding the 7-day budget, downgrades local reads to cached with `plugin_governance_expired`. Reads never refresh implicitly. Feed times more than 5 minutes in the future are invalid. GeneratedAt cannot roll back; changes at the same timestamp are rejected. Failure outputs contain bounded codes, not upstream response bodies or exception details.

A validated revoked union is atomically cached below the package root in `.governance.json`. Later omissions, warning replacements, failed/invalid/stale refreshes and restart cannot clear known revoked tuples. Age-stale feeds preserve all prior records while merging reported records and are never fresh. The retained local union has a separate 64-MiB/131072-entry bound; write failures retain the in-memory deny set and expose failure rather than fresh. The cache is trusted host state, not repository input. Installed-only reads load it without network.

## Acquisition gates

Import/catalog previews expose governance checks. Confirmation refreshes and checks independently of preview tokens. Update and rollback apply the same target rule. Central stage checks before download, after download before configuration, and before commit. Rollback checks before cached archive access and before staging. A shared governance publication/commit gate serializes the final deny check and package publication, including refreshes during the released configuration gate.

Additive Panel DTOs include item governance, installed governance when present, root governanceSource, import preview governance/source and lifecycle current/target governance/source. Nullable governance fields are serialized explicitly.

## Synthetic verification

Run locally with .NET 10. If the OS default temporary directory is inaccessible, set TMP and TEMP to a writable, isolated test directory (the development run used `tests/BotAgent.PluginMarketProbe/bin/test-temp`); do not read or repair unrelated data roots:

```sh
dotnet build src/BotAgent.Headless/BotAgent.Headless.csproj -c Release
dotnet run --project tests/BotAgent.PluginMarketProbe/BotAgent.PluginMarketProbe.csproj -c Release
```

The probe uses fake HTTP and temporary package roots: malformed/schema/bounds/duplicate feeds, failures, stale times, expiry, omission/restart, rename, all import origins, central stage, update/rollback, download revocation and installed preservation. No live catalogs, databases, chats, profiles or logs. Record native results against the final candidate; this document is not acceptance evidence.
