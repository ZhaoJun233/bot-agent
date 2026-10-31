import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Market from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaPluginCatalogItem, type ZbaPluginOperationResult, type ZbaPluginSettings } from '../../src/api/zba'
import { clearPendingOperations, getPendingOperation, pendingOperations } from '../../src/shared/sync/pending-operations'
import { syntheticManifest, syntheticSettings } from '../fixtures/provider-marketplace'

const wrappers: Array<{ unmount(): void }> = []
let scope = 0
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren() })
const A = { ...syntheticManifest, id: 'example.a' }
const B = { ...syntheticManifest, id: 'example.b' }
function item(manifest = A, installed = false): ZbaPluginCatalogItem { return { manifest, installedVersion: installed ? manifest.version : null, enabled: false, status: installed ? 'installed' : 'available', restartRequired: false } }
function state(value: string, revision = 's1'): ZbaPluginSettings { return { revision, schema: syntheticSettings, values: { caption: value }, configuredSecrets: {}, restartRequired: false } }
function receipt(id = A.id): ZbaPluginOperationResult { return { revision: 'p2', operation: { id: 'synthetic-op', pluginId: id, kind: 'install', status: 'restart_required', restartRequired: true } } }
function deferred<T>() { let resolve!: (value: T) => void; let reject!: (cause: unknown) => void; const promise = new Promise<T>((r, j) => { resolve = r; reject = j }); return { promise, resolve, reject } }
function client(overrides: Partial<ZbaClient> = {}): ZbaClient {
  const identity = ++scope
  return { baseUrl: '/synthetic-market-' + identity, identityLabel: 'market-' + identity,
    pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic only', categories: [], plugins: [item(A), item(B)] })),
    installedPlugins: vi.fn(async () => ({ revision: 'p1', plugins: [] })),
    pluginSettings: vi.fn(async id => state(id === A.id ? 'A-old' : 'B-old')),
    installPlugin: vi.fn(async () => receipt()), pluginOperation: vi.fn(async () => receipt()),
    ...overrides } as unknown as ZbaClient
}
async function market(c: ZbaClient) { const w = mount(Market, { props: { client: c, mode: 'admin', writesEnabled: true }, attachTo: document.body, global: { stubs: { teleport: true } } }); wrappers.push(w); await flushPromises(); return w }
type Wrapper = Awaited<ReturnType<typeof market>>
async function install(w: Wrapper) { await w.get('[data-testid="marketplace-trust"]').setValue(true); await w.get('[data-testid="marketplace-install"]').trigger('click'); await flushPromises() }
async function settings(w: Wrapper, id = A.id) { await w.get('[data-testid="marketplace-item-' + id + '"]').trigger('click'); await w.get('[data-testid="marketplace-settings"]').trigger('click'); await flushPromises() }
function caption(w: Wrapper) { return (w.get('[data-testid="plugin-setting-caption"]').element as HTMLInputElement).value }

describe('marketplace authorized findings regressions', () => {
  it('registers before dispatch and retains unknown key through unmount and a same-scope new client', async () => {
    const c = client({ installPlugin: vi.fn(async request => { expect(getPendingOperation(c, 'plugin', A.id)?.key).toBe(request.idempotencyKey); throw new ZbaNetworkError() }) })
    const first = await market(c); await install(first)
    const key = getPendingOperation(c, 'plugin', A.id)?.key
    expect(key).toBeTruthy(); first.unmount()
    const next = { ...c } as ZbaClient
    const second = await market(next)
    expect(second.find('[data-testid="marketplace-recovery"]').exists()).toBe(true)
    await install(second)
    expect(c.installPlugin).toHaveBeenCalledTimes(1)
    await second.get('[data-testid="marketplace-recovery-check"]').trigger('click'); await flushPromises()
    expect(next.pluginOperation).toHaveBeenCalledWith(key)
    expect(getPendingOperation(next, 'plugin', A.id)).toBeNull()
  })
  it('settles the original key when success arrives after unmount', async () => {
    const pending = deferred<ZbaPluginOperationResult>(); const c = client({ installPlugin: vi.fn(() => pending.promise) })
    const first = await market(c); await install(first)
    const key = getPendingOperation(c, 'plugin', A.id)?.key
    first.unmount(); pending.resolve(receipt()); await flushPromises()
    expect(pendingOperations(c, 'plugin').find(record => record.key === key)?.state).toBe('settled')
    const second = await market(c); expect(second.find('[data-testid="marketplace-recovery"]').exists()).toBe(false)
  })
  it('another plugin mutation cannot erase earlier unknown and not-found cannot enable resubmission', async () => {
    const c = client({ installPlugin: vi.fn(async request => { if (request.manifest.id === A.id) throw new ZbaNetworkError(); return receipt(B.id) }), pluginOperation: vi.fn(async () => { throw new ZbaRequestError(404, 'plugin_operation_not_found', 'Synthetic missing') }) })
    const w = await market(c); await install(w); const key = getPendingOperation(c, 'plugin', A.id)?.key
    await w.get('[data-testid="marketplace-item-example.b"]').trigger('click'); await install(w)
    expect(w.find('[data-testid="marketplace-recovery"]').exists()).toBe(true)
    await w.get('[data-testid="marketplace-recovery-check"]').trigger('click'); await flushPromises()
    expect(getPendingOperation(c, 'plugin', A.id)?.key).toBe(key)
    expect(c.installPlugin).toHaveBeenCalledTimes(2)
  })
  it('unions installed-only packages and refreshes a resolved source with real management actions', async () => {
    let installed = false
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic only', categories: [], plugins: [] })), installedPlugins: vi.fn(async () => ({ revision: installed ? 'p2' : 'p1', plugins: installed ? [{ ...item(A), status: 'restart_required', restartRequired: true }] : [item(B, true)] })), resolvePluginSource: vi.fn(async () => ({ manifest: A, trustNotice: 'Synthetic only' })), installPlugin: vi.fn(async () => { installed = true; return receipt() }) })
    const w = await market(c)
    expect(w.find('[data-testid="marketplace-item-example.b"]').exists()).toBe(true)
    await w.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/example-source/releases/tag/v1.0.0')
    await w.get('[data-testid="marketplace-source-resolve"]').trigger('click'); await flushPromises()
    await w.get('[data-testid="marketplace-item-example.a"]').trigger('click'); await install(w)
    expect(w.get('[data-testid="marketplace-package-state"]').text()).toContain('已暂存')
    expect(w.find('[data-testid="marketplace-settings"]').exists()).toBe(true)
    expect(w.find('[data-testid="marketplace-uninstall"]').exists()).toBe(true)
    expect(c.installedPlugins).toHaveBeenCalledTimes(2)
    expect(w.findAll('[data-testid="marketplace-item-example.a"]')).toHaveLength(1)
  })
  it('A PUT cannot overwrite B panel and settles A even after changing plugins', async () => {
    const pending = deferred<ZbaPluginSettings>(); const c = client({ installedPlugins: vi.fn(async () => ({ revision: 'p1', plugins: [item(A, true), item(B, true)] })), pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true), item(B, true)] })), savePluginSettings: vi.fn(() => pending.promise) })
    const w = await market(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('A-new'); await w.get('[data-testid="marketplace-settings-save"]').trigger('click')
    const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    await settings(w, B.id); expect(caption(w)).toBe('B-old')
    pending.resolve(state('A-new', 's2')); await flushPromises()
    expect(caption(w)).toBe('B-old'); expect(w.find('[data-testid="marketplace-settings-notice"]').exists()).toBe(false)
    expect(pendingOperations(c, 'plugin-settings').find(record => record.key === key)?.state).toBe('settled')
  })
  it('blocks unknown settings retry across reload and reopen without storing secret/request body', async () => {
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true)] })), savePluginSettings: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const first = await market(c); await settings(first)
    await first.get('[data-testid="plugin-setting-secret-mode"]').setValue('set'); await first.get('[data-testid="plugin-setting-secret"]').setValue('SYNTHETIC-NOT-A-KEY')
    await first.get('[data-testid="marketplace-settings-save"]').trigger('click'); await flushPromises()
    const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    expect(JSON.stringify(pendingOperations(c))).not.toContain('SYNTHETIC-NOT-A-KEY')
    expect(JSON.stringify(pendingOperations(c))).not.toContain('edits')
    await first.get('[data-testid="marketplace-settings-reload"]').trigger('click'); await flushPromises()
    expect(getPendingOperation(c, 'plugin-settings', A.id)?.key).toBe(key)
    first.unmount(); const second = await market(c); await settings(second)
    expect(second.get('[data-testid="marketplace-settings-save"]').attributes('disabled')).toBeDefined()
    expect(second.text()).toContain('原操作键')
    expect(c.savePluginSettings).toHaveBeenCalledTimes(1)
  })
  it('rejects stale A read after A to B to A', async () => {
    const first = deferred<ZbaPluginSettings>(); let reads = 0
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true), item(B, true)] })), pluginSettings: vi.fn(async id => id === A.id && ++reads === 1 ? first.promise : state(id + '-fresh')) })
    const w = await market(c); await settings(w); await settings(w, B.id); await settings(w, A.id)
    expect(caption(w)).toBe(A.id + '-fresh')
    first.resolve(state('stale-A')); await flushPromises(); expect(caption(w)).toBe(A.id + '-fresh')
  })
  it('does not settle unknown settings on a failed GET and permits only acknowledged reconciliation', async () => {
    let failRead = false
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true)] })), pluginSettings: vi.fn(async () => { if (failRead) throw new ZbaNetworkError(); return state('current') }), savePluginSettings: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const w = await market(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('draft')
    await w.get('[data-testid="marketplace-settings-save"]').trigger('click'); await flushPromises()
    const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    failRead = true; await w.get('[data-testid="marketplace-settings-reload"]').trigger('click'); await flushPromises()
    expect(w.find('[data-testid="marketplace-settings-reconcile"]').exists()).toBe(false)
    expect(getPendingOperation(c, 'plugin-settings', A.id)?.key).toBe(key)
    failRead = false; await w.get('[data-testid="marketplace-settings-reload"]').trigger('click'); await flushPromises()
    expect(w.get('[data-testid="marketplace-settings-reconcile"]').attributes('disabled')).toBeDefined()
    await w.get('[data-testid="marketplace-settings-reconcile-ack"]').setValue(true)
    await w.get('[data-testid="marketplace-settings-reconcile"]').trigger('click'); await flushPromises()
    expect(getPendingOperation(c, 'plugin-settings', A.id)).toBeNull()
    expect(w.text()).toContain('不是原提交的成功或失败回执')
    expect(c.savePluginSettings).toHaveBeenCalledTimes(1)
  })
  it('keeps installed management available when the catalog is unavailable', async () => {
    const c = client({ pluginCatalog: vi.fn(async () => { throw new ZbaNetworkError() }), installedPlugins: vi.fn(async () => ({ revision: 'p1', plugins: [item(A, true)] })) })
    const w = await market(c); expect(w.find('[data-testid="marketplace-settings"]').exists()).toBe(true); expect(w.find('[data-testid="marketplace-uninstall"]').exists()).toBe(true)
  })
  it('settles the original settings key after lock and owner unmount', async () => {
    const pending = deferred<ZbaPluginSettings>()
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true)] })), savePluginSettings: vi.fn(() => pending.promise) })
    const w = await market(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('draft')
    await w.get('[data-testid="marketplace-settings-save"]').trigger('click'); const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    const api = w.vm as unknown as { requestLeave(reason: 'lock'): Promise<boolean> }
    expect(await api.requestLeave('lock')).toBe(true)
    w.unmount(); pending.resolve(state('saved')); await flushPromises()
    expect(pendingOperations(c, 'plugin-settings').find(record => record.key === key)?.state).toBe('settled')
  })
  it('protects dirty plugin switches/close and clears only secret edits on hide/downgrade', async () => {
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true), item(B, true)] })) })
    const w = await market(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('A-draft')
    await settings(w, B.id)
    expect(w.get('#marketplace-settings-heading').text()).toContain(A.id)
    expect(w.find('[data-testid="marketplace-discard-confirmation"]').exists()).toBe(true)
    await w.get('[data-testid="marketplace-discard-cancel"]').trigger('click')
    await w.get('[data-testid="plugin-setting-secret-mode"]').setValue('set'); await w.get('[data-testid="plugin-setting-secret"]').setValue('SYNTHETIC-NOT-A-KEY')
    await w.setProps({ active: false, mode: 'read' }); await flushPromises()
    expect(w.find('[data-testid="plugin-setting-secret"]').exists()).toBe(false)
    expect(caption(w)).toBe('A-draft')
    await w.setProps({ active: true }); await w.get('[data-testid="marketplace-close"]').trigger('click')
    expect(w.emitted('close')).toBeUndefined()
    await w.get('[data-testid="marketplace-discard-confirm"]').trigger('click')
    expect(w.emitted('close')).toHaveLength(1)
  })
})
