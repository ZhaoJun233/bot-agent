# Quota public contract ownership

BotAgent.Quota.Contracts has its own version 1.0.0 and depends only on BotAgent.Plugin.Contracts. Its source is contracts/quota. It owns the unchanged public names and signatures of TenantQuotaSnapshot, TenantQuotaPolicy, TenantQuotaManagementSnapshot, TenantQuotaManagementException, ITenantQuotaLedger and ITenantQuotaManagement. Core forwards their previous assembly-qualified identities; there is no reverse dependency on Core.

The quota implementation has an independent version. Its binary SDK build supplies Core, Plugin.Contracts and Quota.Contracts. The contract project can itself be built with only the generic Plugin.Contracts binary and cleared package feeds. Neither a private host project nor quota implementation is needed to build that contract.

## Host and package identity

The package-owned distribution template declares the contract name and owner. The bundle producer reads the actual adjacent contract DLL without executing it, supplies its assembly version and SHA256, and includes the exact bytes alongside the implementation in a deterministic ZIP. The generated index is compiled into the host. Template inputs cannot supply their own contract version or digest.

The shared registry checks that a host-owned declaration matches this compiled owner/name/version/hash pin before permanent loading. A preloaded Default assembly is accepted only at the expected host path, with matching verified file bytes and loaded module identity. An arbitrary preloaded assembly is still rejected. New direct references require a declaration; a bundled duplicate does not create a second identity. Shared contract dependencies still obey the runtime's exact provider readiness and invocation leases.

A different contract version or different bytes at the same version cannot replace the host pin. Such a release needs an approved host/contract combination and a cold process boundary. Updating a manifest or placing another DLL in the package directory is insufficient. The generic SDK special case does not include quota business types.

## Compatibility and verification

An actual preceding quota package, compiled when all six types lived in Core, runs through the old Core forwards on the current host. Its returned snapshots, management interface and typed conflict exception resolve to the same new contract identities. The original receipt and conflict behavior is retained. This legacy forward path is intentional binary compatibility; new direct contract references use the explicit declaration path.

Synthetic checks exercise default disabled control management, the packaged duplicate, independent contract versions 1 and 2, wrong owner/hash/version rejection, undeclared direct references and the actual old package. Existing HTTP and lifecycle regression checks still apply. These are type and lifecycle guarantees, not a managed-code sandbox or whole-host downgrade proof.

Quota HTTP declarations, DTO projection and UI sources have since moved to the package or its independent contract. The two implementation source links and their unused host classes are retired. Quota schema/migration ownership and D2 runtime route/UI adoption remain open; this does not complete P08/R2 acceptance.
