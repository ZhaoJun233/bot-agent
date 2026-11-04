# Review remediation probe (#59 / #60)

## Scope and implementation plan

1. Reuse the existing SQLite `AppDatabase.Write` transaction for episode insertion IDs and prompt version allocation. Read `last_insert_rowid()` on the inserting connection; allocate the existing per-key `vN` format inside the write transaction before deactivation/insertion. No schema or port changes.
2. Keep the existing Jargon 500-scope, 200-phrase and 15-minute cooldown rules. Serialize buffer admission/eviction, evict the lowest-frequency phrase when full, and remove cooldown entries when their scope is evicted. Keep repository calls outside the buffer lock.
3. Separate health-report current-run success counts from the lifetime `SentCount`. Preserve existing success, failure, partial-skip and empty-target behavior.
4. Synchronize contributor documentation with the harness/CI scenario set and the reviewed snapshot-to-`dev`, PR-to-`main` release path.

## Verification

```sh
dotnet run --project tests/BotAgent.ReviewRemediationProbe/BotAgent.ReviewRemediationProbe.csproj -c Release
```

The standalone console probe sets **both** supported data-root environment variables before database initialization, creates its own unique temporary SQLite database and logs, and clears connection pools before deleting those synthetic artifacts. It never starts a bot, connects to a server, or uses production fixtures.

Assertions cover insertion IDs and deletion, concurrent saves on one prompt key, exactly one active version, cross-key isolation and historical activation, single-message/high-frequency/parallel Jargon phrase limits, scope churn, cooldown eviction and the retained scope's exact 15-minute cooldown boundary, and health-report success followed by all-skipped, partially-skipped, failed and empty-recipient runs. Reflection reads only synthetic in-memory buffer counts/keys; no public diagnostics API is added for tests. Health-report dependencies unused by these idle reports are deliberately left inert; no reply/model/tool operation is executed.

This focused probe is now restored and run by the CI `verify` job alongside the other review-remediation probes. It verifies repository/service behavior, not end-to-end production wiring; local execution does not prove a remote CI run passed.

## Runtime boundary

- The existing panel Jargon routes use `JargonStore` for CRUD. `JargonService` observation/discovery/prompt rendering currently have no runtime call sites.
- `EpisodeStore` and `PromptTemplateStore` currently have no runtime consumers; fixes do not imply automatic episode extraction, recall or versioned prompt use is enabled.
- Health reports are wired at startup and to the panel, but currently receive the OneBot gateway rather than the multi-channel source/router. The synthetic official-alias case verifies existing channel gating only, not official transport delivery.
- S42 is now registered in the integration harness/CI as synthetic fake-gateway coverage; this probe still does not establish real Open Platform interoperability or credentials validation.

No composition root, transport/fetcher, SafeUrl, frontend or integration-harness entry-point changes are made by this scope.
