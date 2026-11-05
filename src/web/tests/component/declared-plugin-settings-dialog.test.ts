import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Dialog from '../../src/features/capabilities/DeclaredPluginSettingsDialog.vue'
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
async function declared(c: ZbaClient) { const w = mount(Dialog, { props: { client: c, mode: 'admin', writesEnabled: true, active: true, pluginId: A.id }, attachTo: document.body, global: { stubs: { teleport: true } } }); wrappers.push(w); await flushPromises(); return w }
type Wrapper = Awaited<ReturnType<typeof declared>>
async function settings(w: Wrapper, id = A.id) { await w.setProps({ pluginId: id }); await flushPromises() }
function caption(w: Wrapper) { return (w.get('[data-testid="plugin-setting-caption"]').element as HTMLInputElement).value }

describe('declared plugin settings authorized findings regressions', () => {
  it('A PUT cannot overwrite B panel and settles A even after changing plugins', async () => {
    const pending = deferred<ZbaPluginSettings>(); const c = client({ installedPlugins: vi.fn(async () => ({ revision: 'p1', plugins: [item(A, true), item(B, true)] })), pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true), item(B, true)] })), savePluginSettings: vi.fn(() => pending.promise) })
    const w = await declared(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('A-new'); await w.get('[data-testid="declared-plugin-settings-save"]').trigger('click')
    const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    await settings(w, B.id); expect(caption(w)).toBe('B-old')
    pending.resolve(state('A-new', 's2')); await flushPromises()
    expect(caption(w)).toBe('B-old'); expect(w.find('[data-testid="declared-plugin-settings-notice"]').exists()).toBe(false)
    expect(pendingOperations(c, 'plugin-settings').find(record => record.key === key)?.state).toBe('settled')
  })
  it('blocks unknown settings retry across reload and reopen without storing secret/request body', async () => {
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true)] })), savePluginSettings: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const first = await declared(c); await settings(first)
    await first.get('[data-testid="plugin-setting-secret-mode"]').setValue('set'); await first.get('[data-testid="plugin-setting-secret"]').setValue('SYNTHETIC-NOT-A-KEY')
    await first.get('[data-testid="declared-plugin-settings-save"]').trigger('click'); await flushPromises()
    const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    expect(JSON.stringify(pendingOperations(c))).not.toContain('SYNTHETIC-NOT-A-KEY')
    expect(JSON.stringify(pendingOperations(c))).not.toContain('edits')
    await first.get('[data-testid="declared-plugin-settings-reload"]').trigger('click'); await flushPromises()
    expect(getPendingOperation(c, 'plugin-settings', A.id)?.key).toBe(key)
    first.unmount(); const second = await declared(c); await settings(second)
    expect(second.get('[data-testid="declared-plugin-settings-save"]').attributes('disabled')).toBeDefined()
    expect(second.text()).toContain('原操作键')
    expect(c.savePluginSettings).toHaveBeenCalledTimes(1)
  })
  it('rejects stale A read after A to B to A', async () => {
    const first = deferred<ZbaPluginSettings>(); let reads = 0
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true), item(B, true)] })), pluginSettings: vi.fn(async id => id === A.id && ++reads === 1 ? first.promise : state(id + '-fresh')) })
    const w = await declared(c); await settings(w); await settings(w, B.id); await settings(w, A.id)
    expect(caption(w)).toBe(A.id + '-fresh')
    first.resolve(state('stale-A')); await flushPromises(); expect(caption(w)).toBe(A.id + '-fresh')
  })
  it('does not settle unknown settings on a failed GET and permits only acknowledged reconciliation', async () => {
    let failRead = false
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true)] })), pluginSettings: vi.fn(async () => { if (failRead) throw new ZbaNetworkError(); return state('current') }), savePluginSettings: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const w = await declared(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('draft')
    await w.get('[data-testid="declared-plugin-settings-save"]').trigger('click'); await flushPromises()
    const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    failRead = true; await w.get('[data-testid="declared-plugin-settings-reload"]').trigger('click'); await flushPromises()
    expect(w.find('[data-testid="declared-plugin-settings-reconcile"]').exists()).toBe(false)
    expect(getPendingOperation(c, 'plugin-settings', A.id)?.key).toBe(key)
    failRead = false; await w.get('[data-testid="declared-plugin-settings-reload"]').trigger('click'); await flushPromises()
    expect(w.get('[data-testid="declared-plugin-settings-reconcile"]').attributes('disabled')).toBeDefined()
    await w.get('[data-testid="declared-plugin-settings-reconcile-ack"]').setValue(true)
    await w.get('[data-testid="declared-plugin-settings-reconcile"]').trigger('click'); await flushPromises()
    expect(getPendingOperation(c, 'plugin-settings', A.id)).toBeNull()
    expect(w.text()).toContain('不是原提交的成功或失败回执')
    expect(c.savePluginSettings).toHaveBeenCalledTimes(1)
  })
  it('settles the original settings key after lock and owner unmount', async () => {
    const pending = deferred<ZbaPluginSettings>()
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true)] })), savePluginSettings: vi.fn(() => pending.promise) })
    const w = await declared(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('draft')
    await w.get('[data-testid="declared-plugin-settings-save"]').trigger('click'); const key = getPendingOperation(c, 'plugin-settings', A.id)?.key
    const api = w.vm as unknown as { requestLeave(reason: 'lock'): Promise<boolean> }
    expect(await api.requestLeave('lock')).toBe(true)
    w.unmount(); pending.resolve(state('saved')); await flushPromises()
    expect(pendingOperations(c, 'plugin-settings').find(record => record.key === key)?.state).toBe('settled')
  })
  it('protects dirty plugin switches/close and clears only secret edits on hide/downgrade', async () => {
    const c = client({ pluginCatalog: vi.fn(async () => ({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic', categories: [], plugins: [item(A, true), item(B, true)] })) })
    const w = await declared(c); await settings(w); await w.get('[data-testid="plugin-setting-caption"]').setValue('A-draft')
    await settings(w, B.id)
    expect(w.get('#declared-plugin-settings-heading').text()).toContain(A.id)
    expect(w.find('[data-testid="declared-plugin-settings-discard-confirmation"]').exists()).toBe(true)
    await w.get('[data-testid="declared-plugin-settings-discard-cancel"]').trigger('click')
    await w.get('[data-testid="plugin-setting-secret-mode"]').setValue('set'); await w.get('[data-testid="plugin-setting-secret"]').setValue('SYNTHETIC-NOT-A-KEY')
    await w.setProps({ active: false, mode: 'read' }); await flushPromises()
    expect(w.find('[data-testid="plugin-setting-secret"]').exists()).toBe(false)
    expect(caption(w)).toBe('A-draft')
    await w.setProps({ active: true }); await w.get('[data-testid="declared-plugin-settings-dialog-close"]').trigger('click')
    expect(w.emitted('close')).toBeUndefined()
    await w.get('[data-testid="declared-plugin-settings-discard-confirm"]').trigger('click')
    expect(w.emitted('close')).toHaveLength(1)
  })
})
