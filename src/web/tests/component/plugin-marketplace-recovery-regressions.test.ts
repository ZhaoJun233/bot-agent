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
  it.each([{ mode: 'admin' as const }, { writesEnabled: false }])('preserves a pending catalog read across permission change %j', async change => {
    const index = deferred<{ revision: string; source: string; trustNotice: string; categories: string[]; plugins: ZbaPluginCatalogItem[] }>()
    const c = client({ pluginCatalog: vi.fn(() => index.promise) })
    const w = mount(Market, { props: { client: c, mode: 'read', writesEnabled: true, active: true }, attachTo: document.body, global: { stubs: { teleport: true } } })
    wrappers.push(w)
    await flushPromises()
    await w.setProps(change)
    index.resolve({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic only', categories: [], plugins: [item(A)] })
    await flushPromises()
    expect(w.find('[data-testid="marketplace-item-example.a"]').exists()).toBe(true)
    expect(c.pluginCatalog).toHaveBeenCalledTimes(1)
  })

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
  it('keeps installed management available when the catalog is unavailable', async () => {
    const c = client({ pluginCatalog: vi.fn(async () => { throw new ZbaNetworkError() }), installedPlugins: vi.fn(async () => ({ revision: 'p1', plugins: [item(A, true)] })) })
    const w = await market(c); expect(w.find('[data-testid="marketplace-settings"]').exists()).toBe(true); expect(w.find('[data-testid="marketplace-uninstall"]').exists()).toBe(true)
  })

})
