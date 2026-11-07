import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Market from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError } from '../../src/api/zba'
import type { PluginSourceDiscovery } from '../../src/api/plugin-import'
import { clearPendingOperations } from '../../src/shared/sync/pending-operations'
import { discovery, installPreview, marketDialogShell } from '../fixtures/plugin-import'
import { syntheticManifest } from '../fixtures/provider-marketplace'
const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren(); vi.useRealTimers() })
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r }); return { promise, resolve } }
async function harness() {
  const client = new ZbaClient({ baseUrl: '/synthetic-import' })
  vi.spyOn(client, 'pluginCatalog').mockResolvedValue({ revision: 'rev-1', source: 'https://example.com/catalog.json', trustNotice: '信任你确认的来源。', categories: [], plugins: [{ manifest: syntheticManifest, enabled: false, status: 'available', restartRequired: false }] })
  vi.spyOn(client, 'installedPlugins').mockResolvedValue({ revision: 'rev-1', plugins: [] })
  const discover = vi.spyOn(client, 'discoverPluginSource').mockResolvedValue(discovery())
  const preview = vi.spyOn(client, 'previewPlugin').mockImplementation(async request => installPreview({ source: { kind: request.origin }, manifest: request.origin === 'repository' ? { ...syntheticManifest, id: request.pluginId, version: request.version } : request.manifest }))
  const install = vi.spyOn(client, 'installPlugin').mockResolvedValue({ revision: 'rev-2', operation: { id: 'operation-example', pluginId: syntheticManifest.id, kind: 'install', status: 'installed', restartRequired: false } })
  const recovery = vi.spyOn(client, 'pluginOperation').mockResolvedValue({ revision: 'rev-2', operation: { id: 'operation-example', pluginId: syntheticManifest.id, kind: 'install', status: 'installed', restartRequired: false } })
  const wrapper = mount(Market, { props: { client, mode: 'admin', writesEnabled: true }, attachTo: document.body, global: { stubs: { ManagementDialog: marketDialogShell } } }); wrappers.push(wrapper); await flushPromises()
  return { client, wrapper, discover, preview, install, recovery }
}
type Wrapper = Awaited<ReturnType<typeof harness>>['wrapper']
async function browseSource(wrapper: Wrapper) { await wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/example-plugin'); await wrapper.get('[data-testid="marketplace-source-resolve"]').trigger('click'); await flushPromises() }
async function catalogPreview(wrapper: Wrapper) { await wrapper.get('[data-testid="marketplace-install"]').trigger('click'); await flushPromises() }
async function confirm(wrapper: Wrapper) { await wrapper.get('[data-testid="preview-acknowledge"]').setValue(true); await wrapper.get('[data-testid="preview-confirm"]').trigger('click'); await flushPromises() }
describe('M2 discovery preview confirmation flow', () => {
  it('discovers multiple plugins and versions, previews an exact choice and never installs during reads', async () => {
    const { wrapper, discover, preview, install } = await harness(); await browseSource(wrapper)
    expect(discover).toHaveBeenCalledTimes(1); expect(install).not.toHaveBeenCalled(); expect(preview).not.toHaveBeenCalled()
    expect(wrapper.findAll('[data-testid="source-plugin"] option')).toHaveLength(2)
    expect(wrapper.findAll('[data-testid="source-version"] option')).toHaveLength(2)
    await wrapper.get('[data-testid="source-version"]').setValue('2.0.0'); await wrapper.get('[data-testid="source-preview"]').trigger('click'); await flushPromises()
    expect(preview).toHaveBeenLastCalledWith({ origin: 'repository', discoveryId: 'discovery-example', pluginId: syntheticManifest.id, version: '2.0.0', expectedRevision: 'rev-1' })
    expect(wrapper.get('[data-testid="plugin-install-preview"]').text()).toContain('2.0.0'); expect(install).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="preview-confirm"]').attributes('disabled')).toBeDefined(); await confirm(wrapper)
    expect(install).toHaveBeenCalledTimes(1); expect(install.mock.calls[0]?.[0]).toMatchObject({ previewId: 'preview-example', manifest: { id: syntheticManifest.id, version: '2.0.0' }, trustAcknowledged: true })
  })
  it('catalog install opens preview and confirmation uses exactly its candidate', async () => {
    const { wrapper, preview, install } = await harness(); await catalogPreview(wrapper)
    expect(preview).toHaveBeenCalledWith({ origin: 'catalog', manifest: syntheticManifest, expectedRevision: 'rev-1' }); expect(install).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="preview-sha256"]').text()).toContain(syntheticManifest.sha256)
    expect(wrapper.get('[data-testid="plugin-install-preview"]').text()).toContain('默认停用不代表零代码执行')
    expect(wrapper.get('[data-testid="plugin-install-preview"]').text()).toContain('安装时才校验')
    await confirm(wrapper); expect(install).toHaveBeenCalledTimes(1); expect(wrapper.find('[data-testid="preview-confirm"]').exists()).toBe(false)
  })
  it('JSON imports require the same preview and cannot forge source or audit metadata', async () => {
    const { wrapper, preview, install } = await harness(); const input = wrapper.get('[data-testid="marketplace-json-input"]')
    await input.setValue(JSON.stringify({ ...syntheticManifest, metadata: { review: 'reviewed' } })); await wrapper.get('[data-testid="json-preview"]').trigger('click'); await flushPromises()
    expect(preview).not.toHaveBeenCalled(); expect(wrapper.get('[data-testid="marketplace-json-error"]').text()).toContain('非技术字段')
    await input.setValue(JSON.stringify(syntheticManifest)); await wrapper.get('[data-testid="json-preview"]').trigger('click'); await flushPromises()
    expect(preview).toHaveBeenCalledWith({ origin: 'json', manifest: syntheticManifest, expectedRevision: 'rev-1' }); expect(install).not.toHaveBeenCalled()
    await confirm(wrapper); expect(install.mock.calls[0]?.[0].previewId).toBe('preview-example')
  })
  it.each(['source', 'plugin', 'version', 'json'] as const)('invalidates acknowledged preview when %s changes', async field => {
    const { wrapper, install } = await harness(); await browseSource(wrapper); await wrapper.get('[data-testid="source-preview"]').trigger('click'); await flushPromises(); await wrapper.get('[data-testid="preview-acknowledge"]').setValue(true)
    if (field === 'source') await wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/other-plugin')
    if (field === 'plugin') await wrapper.get('[data-testid="source-plugin"]').setValue('example.second')
    if (field === 'version') await wrapper.get('[data-testid="source-version"]').setValue('2.0.0')
    if (field === 'json') await wrapper.get('[data-testid="marketplace-json-input"]').setValue('{}')
    expect(wrapper.find('[data-testid="preview-confirm"]').exists()).toBe(false); expect(install).not.toHaveBeenCalled()
  })
  it('ignores stale discovery results after source edit', async () => {
    const { wrapper, discover } = await harness(); const pending = deferred<PluginSourceDiscovery>(); discover.mockReturnValueOnce(pending.promise)
    await wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/example-plugin'); await wrapper.get('[data-testid="marketplace-source-resolve"]').trigger('click')
    await wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/other-plugin'); pending.resolve(discovery()); await flushPromises()
    expect(wrapper.find('[data-testid="source-candidates"]').exists()).toBe(false)
  })
  it('locks expired discovery and preview with a new-preview route', async () => {
    vi.useFakeTimers(); const { wrapper, preview, discover, install } = await harness()
    discover.mockResolvedValueOnce(discovery({ expiresAt: new Date(Date.now() + 300).toISOString() })); await browseSource(wrapper)
    preview.mockResolvedValueOnce(installPreview({ source: { kind: 'repository' }, expiresAt: new Date(Date.now() + 300).toISOString() })); await wrapper.get('[data-testid="source-preview"]').trigger('click'); await flushPromises(); await wrapper.get('[data-testid="preview-acknowledge"]').setValue(true)
    await vi.advanceTimersByTimeAsync(500); await flushPromises()
    expect(wrapper.get('[data-testid="source-preview"]').attributes('disabled')).toBeDefined(); expect(wrapper.get('[data-testid="preview-confirm"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="plugin-install-preview"]').text()).toContain('已过期'); expect(install).not.toHaveBeenCalled()
  })
  it('never falls back after known expired-preview rejection', async () => {
    const { wrapper, install, recovery } = await harness(); install.mockRejectedValueOnce(new ZbaRequestError(409, 'plugin_preview_refresh_required', '预览已过期'))
    await catalogPreview(wrapper); await confirm(wrapper)
    expect(install).toHaveBeenCalledTimes(1); expect(recovery).not.toHaveBeenCalled(); expect(wrapper.find('[data-testid="preview-confirm"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="marketplace-notice"]').text()).toContain('重新发现并预览')
  })
  it('recovers an unknown install by the original key and never dispatches a second install', async () => {
    const { wrapper, install, recovery } = await harness(); install.mockRejectedValueOnce(new ZbaNetworkError()); await catalogPreview(wrapper); await confirm(wrapper)
    const original = install.mock.calls[0]?.[0]; expect(wrapper.find('[data-testid="marketplace-recovery"]').exists()).toBe(true)
    await wrapper.get('[data-testid="marketplace-recovery-check"]').trigger('click'); await flushPromises()
    expect(recovery).toHaveBeenCalledWith(original?.idempotencyKey); expect(install).toHaveBeenCalledTimes(1)
  })
})