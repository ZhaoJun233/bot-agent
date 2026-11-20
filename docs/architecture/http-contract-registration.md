# HTTP contract registration

QuotaHttpContract in contracts/quota owns the existing three quota route descriptions, typed dispatch validation and OpenAPI schemas. The host supplies its authenticated list/save/find callbacks; these still call the package through the current leased control entry point. ZbaEndpoint contains no quota operation switch or schema definitions.

The public plugin SDK describes literal authenticated routes through PluginHttpDescription and PluginHttpBinding<TCaller>. The transport supplies PluginHttpBody, whose typed read uses the existing strict, bounded JSON parser. A host backend implements IZbaHttpContributions to supply explicitly composed bindings when constructing the endpoint. A bare endpoint has no implicit quota routes. This interface is not exposed through HTTP, package context or installed manifest metadata.

ZbaHttpCatalog snapshots route metadata and parses schema JSON during endpoint construction. It rejects duplicate owners, operations, core features, method/path pairs, schema names and cross-owner path sharing. It refuses paths outside /zba/v1, parameterized or encoded extension paths, paths that overlap shell routes, GET body contracts, remote schema references and missing local references. Schema and route counts, text sizes and JSON nesting are bounded. No contributed route is public.

Dispatch, meta features, Allow headers and the generated OpenAPI document use the same immutable catalog. Unknown query fields are rejected. Authentication runs before contributed callbacks; quota request validation still uses the original order and codes. Response serialization and no-store/request/contract headers remain transport-owned. The endpoint retains existing IZbaBackend quota methods as a compatibility surface; current host composition explicitly binds them to the quota-owned contract.

The package-owned contract has no dependency on BotAgent.Panel implementation, Core, Storage or Engine. Its namespace remains BotAgent.Panel to preserve DTO identities, and its sole BotAgent dependency is the public Plugin.Contracts SDK. Changing the contract bytes changes the approved shared-contract digest in the host distribution.

## Adoption boundary

This is a D1 host-composition catalog frozen for the lifetime of its endpoint. The frozen quota compatibility URLs remain registered when the business package is missing and return the established unavailable result; safe generic receipt recovery remains separate. Removing a backend package does not invoke a legacy quota implementation.

This does not implement dynamic installation-time registration of arbitrary new business HTTP endpoints. Dynamic UI session/asset delivery (`/zba/v1/plugins/{id}/ui/...`) and multi-package bundle preview/apply (`/zba/v1/plugins/bundle/...`) are implemented in the frozen catalog and enforce generation leases, SHA-256 asset verification, and strict CSP without exposing Bearer tokens to plugin UI frames. Arbitrary runtime route registration and database migration ownership remain future work; the legacy quota implementation source links have been retired.
