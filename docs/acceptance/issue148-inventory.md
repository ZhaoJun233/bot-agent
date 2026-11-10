# Issue #148 — source inventory and candidate handoff

## Status and limits

This is a **source inventory and implementation handoff**, not an accessibility conformance certificate. The inventory covers all six application pages, authentication, and every Vue subpanel/dialog in the active source tree. Only the existing light and dark themes are present; no new theme or framework was added. Source inspection and focused component tests cannot establish rendered contrast, absence of overflow, native text-only zoom support, or human visual approval.

The Lead owns the serialized candidate build and browser evidence. Native 200% text-only zoom and user visual approval remain **pending**. The authored root-font enlargement test is a useful reflow proxy, explicitly **not** native text-only zoom. No production/chat/profile/secret data was used; tests use synthetic identities and example.com.

## Complete page and subpanel inventory

All paths below refer to the active repository, not published copies or the immutable acceptance baseline.

| Surface | Source | Inspected states and interactions / implementation disposition |
| --- | --- | --- |
| Shared application shell | [App.vue](../../src/web/src/App.vue) | Six navigation buttons, theme/help controls, connection status, read/admin enablement and locking. Configuration/capability navigation uses the owner leave contract. Unsaved configuration navigation requires explicit confirmation. Save/discard/continue remain available; continuing keeps the draft and stays on the current page. An editable, settled navigation-only action can explicitly retain ordinary drafts while leaving, without saving, while clearing all secret inputs and secret-change intents. It is not offered for close, lock, downgrade, saving, or unconfirmed outcomes. Other retained page drafts survive v-show changes; secret inputs clear when their owner becomes inactive. Explicit locking and expired authorization bypass draft vetoes and clear secrets immediately. |
| Authentication | [AuthGate.vue](../../src/web/src/features/auth/AuthGate.vue) | Token entry, mode explanation, submit validation, waiting/failure, theme control. Shared rem typography, wrapping, focus ring, and mobile target styles apply. Repeated authentication failure/native text zoom require browser evidence. |
| Overview | [OverviewPage.vue](../../src/web/src/features/overview/OverviewPage.vue) | Hero, connection/read state, four KPI tiles, component table with mobile details, current error/service information, quick actions, loading/empty/error states. Shared grids, wrapping and typography apply; no separate page rewrite. |
| Configuration | [ConfigurationPage.vue](../../src/web/src/features/configuration/ConfigurationPage.vue) | Basic configuration, whitelist, platform policy, capability-owned groups; sticky save bar; save receipt, version conflict, restart/reconciliation and leave/save/discard flows. Settings frontend owner implements field/runtime copy, rem typography, reload protection and custom leave-dialog isolation integration. |
| Configuration field renderer | [ConfigurationPage.vue inline field renderer](../../src/web/src/features/configuration/ConfigurationPage.vue) | Text/number/select/switch/secret/read-only values, default/source, draft and saved values, runtime effect, field validation. Settings owner scope; shared inputs/focus/target styles apply. |
| Capability list | [CapabilitiesPage.vue](../../src/web/src/features/capabilities/CapabilitiesPage.vue) | Platform/plugin/provider/MCP categories, search/filter, selected inspection drawer, configuration actions, enable/delete confirmation, empty/loading/error/read-only states. Action confirmation uses shared background isolation. Dialog draft counts are aggregated. |
| Capability configuration wrapper | [CapabilityConfigurationDialog.vue](../../src/web/src/features/capabilities/CapabilityConfigurationDialog.vue) | Embedded configuration with title, group selection, admin gating and leave delegation. Forwards navigation reason without discarding ordinary fields. |
| Provider create/edit | [ProviderEditorDialog.vue](../../src/web/src/features/capabilities/ProviderEditorDialog.vue) | Name/endpoint/model/protocol/priority/enabled and secret action, validation, save conflict, uncertain receipt/check, reload, close/discard. Repeated close/Escape only arms confirmation: only explicit discard clears a changed draft. Reload warning covers changed or unsettled work. Saved desired state and current baseline runtime state are separate from unsaved form values; primary copy uses 凭据名称 with technical key details retained. |
| Plugin catalog | [PluginCatalogBrowser.vue](../../src/web/src/features/capabilities/PluginCatalogBrowser.vue) | Search/category, catalog list, installed/available/version/status/trust details, installation/update/removal and configuration affordances. Rem typography, wrapping and mobile action targets. |
| Plugin marketplace | [PluginMarketplaceDialog.vue](../../src/web/src/features/capabilities/PluginMarketplaceDialog.vue) | Catalog/source trust, source resolution, install/update/uninstall confirmation and uncertain-operation recovery; selected plugin settings, save/reload and explicit discard. Page navigation preserves ordinary settings but clears secret input. Dirty reload is confirmation-gated; browser reload warns about dirty/unsettled work. |
| Declared plugin fields | [DeclaredPluginSettings.vue](../../src/web/src/features/capabilities/DeclaredPluginSettings.vue) | String/number/boolean/select and keep/set/clear secret modes; validation/errors, read-only gating. Rem typography and associated labels; mobile target rules apply. |
| Registered plugin switch | [RegisteredPluginSwitch.vue](../../src/web/src/features/capabilities/RegisteredPluginSwitch.vue) | Checked/off/disabled/pending states and label. Switch track increased to 38×24 px (18 px thumb); mobile label minimum 44 px. State is not communicated by color alone. |
| Token quota dialog | [TokenQuotaDialog.vue](../../src/web/src/features/capabilities/TokenQuotaDialog.vue) | Quota enable/limits/period, usage, source quota list/editor, per-source mutation/recovery, conflict, save/discard/reload/close. Ordinary navigation drafts retained; dirty/unsettled beforeunload protection; rem typography. |
| Tasks | [TasksPage.vue](../../src/web/src/features/tasks/TasksPage.vue) | Task composer, prompt/session/source/timeout, history/state filters, error/empty/loading, detail selection, connection state. Browser reload warns on changed composer or a pending operation. Page switches preserve composer state. |
| Task composer | [TasksPage.vue inline section](../../src/web/src/features/tasks/TasksPage.vue) | Associated form controls, submitting/read-only/validation states. Shared typography, wrapping, focus and target rules. |
| Task details | [TasksPage.vue inline section](../../src/web/src/features/tasks/TasksPage.vue) | Progress/result/error/event detail, approve/deny/cancel/replay controls and operation feedback. Shared overflow and mobile heading rules; rendered long synthetic content remains a browser case. |
| Task approval | [TasksPage.vue inline section](../../src/web/src/features/tasks/TasksPage.vue) | Pending/approved/denied/expired approval, action controls and state badges. Shared target/text/focus rules; no automatic approval. |
| Logs | [RuntimeLogsPage.vue](../../src/web/src/features/logs/RuntimeLogsPage.vue) | Severity/content filters, loading/empty/error/retry, live connection, following/paused/new rows, bounded terminal region. Log frontend owner implements ordered/reconnect feed and scroll semantics. Coordinate keyboard-focusable named region, min-width:0 and no per-row live announcements. |
| Traces | [TracesPage.vue](../../src/web/src/features/traces/TracesPage.vue) | Loading/empty/error and multi-column trace table. Wide table remains a named focusable local scroll region instead of page overflow. Rem typography. |

Configuration ownership groups were inventoried as basic/whitelist/platform policy plus capability groups for quota, music, voice, stickers, poke, vibes, profiles, and web search; platform/provider/MCP editing remains in the owner registry rather than duplicated controls.

## Shared primitives (remaining Vue inventory)

| Primitive | Source | Disposition |
| --- | --- | --- |
| Modal | [ManagementDialog.vue](../../src/web/src/shared/ui/ManagementDialog.vue) | Visible focus targets only, Tab containment, Escape delegation, heading focus on open, background isolation and safe return focus. |
| Error notice | [ErrorNotice.vue](../../src/web/src/shared/ui/ErrorNotice.vue) | Title/message/action states, shared text/wrapping. No unverified claim of a pre-existing inline-notice contrast defect. |
| Breakable identifier | [BreakableId.vue](../../src/web/src/shared/ui/BreakableId.vue) | Breakable ID and copy affordance; copy target 24 px desktop/44 px mobile. |
| Icons | [AppIcon.vue](../../src/web/src/shared/ui/AppIcon.vue) | Shared SVG icon vocabulary. Interactive host button supplies the name/target/focus treatment. |
| State badge | [StatusBadge.vue](../../src/web/src/shared/ui/StatusBadge.vue) | Textual status plus tonal styling, not color alone. |
| Unsaved indicators (inline) | [ConfigurationPage.vue](../../src/web/src/features/configuration/ConfigurationPage.vue), [CapabilitiesPage.vue](../../src/web/src/features/capabilities/CapabilitiesPage.vue) | Owner-rendered draft counts and retained-state indicators; no separate UnsavedBadge component exists. |

## Cohesive implementation seams

- [modal-isolation.ts](../../src/web/src/shared/ui/modal-isolation.ts) owns nested modal isolation and body scroll locking. It records/restores original inert values, handles out-of-order parent release and makes release idempotent. The top active dialog remains interactive; background siblings up the ancestor chain become inert. Settings custom leave and capability custom action dialogs use the same seam without duplicating their existing focus traps.
- [focus-restore.ts](../../src/web/src/shared/ui/focus-restore.ts) rejects disconnected, disabled, hidden, inert, aria-hidden, display:none and visibility:hidden return targets, then tries the supplied fallback.
- [draft-protection.ts](../../src/web/src/shared/ui/draft-protection.ts) is a mounted beforeunload warning, removed on unmount. It stores no draft/secret. Browser wording and permission to show a beforeunload prompt are browser-controlled.
- [configuration-commit.ts](../../src/web/src/features/capabilities/configuration-commit.ts) consumes the settings owner's receipt outcome helper instead of conflating a recorded write with runtime success. Provider runtime copy reads the refreshed baseline rather than borrowing the latest receipt as proof.
- [base.css](../../src/web/src/styles/base.css) keeps existing visual language: rem text with root font-size:100%, explicit primary perimeter, 24 px minimum necessary targets, 44 px mobile action targets, wrapping/min-width:0, one-column small-screen content, and reduced-motion rules. It does not hide page overflow to mask defects.

## Findings versus evidence

| Finding | Evidence and status |
| --- | --- |
| Second close/Escape discarded a changed provider draft | Reproduced in source behavior and regression test; fixed by requiring the explicit discard action. |
| Modal background could remain keyboard/pointer interactive; stale/hidden trigger could receive focus | New nested isolation and return-focus tests cover original inert restoration, parent early release, visibility checks and teardown. Real browser focus/pointer checks still required. |
| Forced 320 px body minimum overflowed scrollbar-reduced client width | Lead's baseline observation: 320 px viewport, 305 px client width, 320 px scroll width across all six pages. Removed body/auth min-width:320px in favor of min-width:0. Candidate browser measurements pending. |
| Root-fixed pixels impeded user text scaling | Shared and own scoped text sizes changed to rem; authored tests enlarge the root font. Native browser text-only zoom at 200% remains separate and pending. |
| Light soft-text token was marginal on a used pale surface | Static diagnostic: #60748a on #eff5fc ≈4.39:1; revised #596d82 on that surface ≈4.86:1. Static necessary-boundary diagnostic: #7b90a3 on white ≈3.30:1 and on #eff5fc ≈3.01:1. These are token-pair calculations, not rendered compliance: compositing, actual foreground/background use, disabled states and font geometry still need browser evidence. |
| Potential high-frequency log announcements | Log owner coordinates a named keyboard scroll region and avoids live-announcing every row. Must exercise real paused/following/reconnect behavior in browser. |

Required candidate acceptance: actual rendered normal text ≥4.5:1; large text ≥3:1; necessary control/state boundaries ≥3:1; six pages/auth/dialogs at 320 px with no **page** horizontal overflow; native 200% text-only zoom; keyboard-visible focus, modal background isolation and return focus; reduced-motion mode; 24 px targets (document any legitimate exception) and mobile primary actions ≥44 px. No target exception is claimed by this handoff without a browser measurement.

## Tests and remaining acceptance work

New focused synthetic coverage:

- [modal-isolation.test.ts](../../src/web/tests/unit/issue148/modal-isolation.test.ts): nested/early-release isolation and safe focus return.
- [dialog-ergonomics.test.ts](../../src/web/tests/component/issue148/dialog-ergonomics.test.ts): modal focus/isolation, repeated Escape cannot discard, dirty unload and listener teardown.
- [marketplace-draft.test.ts](../../src/web/tests/component/issue148/marketplace-draft.test.ts): explicit reload consent; navigation retains ordinary draft, clears secret input and preserves unload warning.
- [provider-state.test.ts](../../src/web/tests/component/issue148/provider-state.test.ts): all five baseline runtime states remain distinct from an unsaved enable toggle; navigation preserves ordinary fields and clears secret input.
- [ergonomics.spec.ts](../../src/web/tests/e2e/issue148/ergonomics.spec.ts): authored 320 px light/dark six-page root-font proxy, auth proxy and reduced-motion cases. **Not run by the ergonomics worker**; Lead owns the serialized build/browser run.

Focused command: `npx vitest run tests/component/issue148 tests/unit/issue148 tests/component/provider-editor.test.ts tests/component/token-quota-dialog.test.ts tests/component/plugin-marketplace-dialog.test.ts` from the frontend directory. Results and any integration failures must be recorded in the final handoff rather than inferred from this source inventory. Full frontend build, complete suite, rendered contrast, actual browser zoom, screenshot comparison and user visual approval belong to final candidate evidence.
