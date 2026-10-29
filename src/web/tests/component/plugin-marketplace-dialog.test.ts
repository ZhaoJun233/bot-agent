import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PluginMarketplaceDialog from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient, ZbaNetworkError, type ZbaPluginCatalog, type ZbaPluginOperation, type ZbaPluginSettings } from '../../src/api/zba'
import { syntheticManifest, syntheticSettings } from '../fixtures/provider-marketplace'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount(); document.body.replaceChildren() })

function catalog(overrides: Partial<ZbaPluginCatalog> = {}): ZbaPluginCatalog {
  return {
    revision: 'rev-1',
    source: 'https://example.com/catalog.json',
    trustNotice: '仅安装你信任的代码。',
    categories: ['工具'],
    plugins: [{ manifest: syntheticManifest, enabled: false, status: 'available', restartRequired: false }],
    ...overrides
  }
}
function operation(overrides: Partial<ZbaPluginOperation> = {}): ZbaPluginOperation {
  return { id: 'op-1', pluginId: syntheticManifest.id, kind: 'install', status: 'restart_required', restartRequired: true, ...overrides }
}
function settings(): ZbaPluginSettings {
  return { revision: 'rev-2', schema: syntheticSettings, values: {}, configuredSecrets: {}, restartRequired: false }
}
function clientWith(overrides: Partial<ZbaClient> = {}): ZbaClient {
  return {
    pluginCatalog: vi.fn(async () => catalog()),
    resolvePluginSource: vi.fn(async () => ({ manifest: syntheticManifest, trustNotice: '请核对 SHA-256。' })),
    installPlugin: vi.fn(async () => ({ operation: operation(), revision: 'rev-2' })),
    pluginOperation: vi.fn(async () => ({ operation: operation(), revision: 'rev-2' })),
    pluginSettings: vi.fn(async () => settings()),
    ...overrides
  } as unknown as ZbaClient
}
async function mountDialog(client = clientWith()) {
  const trigger = document.createElement('button'); trigger.textContent = 'Open'; document.body.append(trigger); trigger.focus()
  const wrapper = mount(PluginMarketplaceDialog, { props: { client, mode: 'admin', writesEnabled: true, trigger }, attachTo: document.body, global: { stubs: { teleport: true } } })
  mounted.push(wrapper)
  await flushPromises()
  return { wrapper, trigger }
}

describe('plugin marketplace idempotency and trust gate', () => {
  it('requires the trust acknowledgement before installing and sends one acknowledged request', async () => {
    const installPlugin = vi.fn<ZbaClient['installPlugin']>(async () => ({ operation: operation(), revision: 'rev-2' }))
    const { wrapper } = await mountDialog(clientWith({ installPlugin }))
    expect(wrapper.get('[data-testid="marketplace-version"]').text()).toBe(syntheticManifest.version)
    expect(wrapper.get('[data-testid="marketplace-sha256"]').text()).toBe(syntheticManifest.sha256)
    expect(wrapper.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="marketplace-trust"]').setValue(true)
    expect(wrapper.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('[data-testid="marketplace-install"]').trigger('click')
    await flushPromises()
    expect(installPlugin).toHaveBeenCalledTimes(1)
    expect(installPlugin.mock.calls[0]?.[0]).toMatchObject({ expectedRevision: 'rev-1', trustAcknowledged: true })
    expect(installPlugin.mock.calls[0]?.[0].idempotencyKey).toMatch(/^plugin:install:/)
    expect(wrapper.get('[data-testid="marketplace-notice"]').text()).toContain('重启')
  })

  it('recovers an unknown result with the same operation key instead of resubmitting', async () => {
    const installPlugin = vi.fn<ZbaClient['installPlugin']>(async () => { throw new ZbaNetworkError() })
    const pluginOperation = vi.fn<ZbaClient['pluginOperation']>(async () => ({ operation: operation({ status: 'staged', restartRequired: true }), revision: 'rev-2' }))
    const { wrapper } = await mountDialog(clientWith({ installPlugin, pluginOperation }))
    await wrapper.get('[data-testid="marketplace-trust"]').setValue(true)
    await wrapper.get('[data-testid="marketplace-install"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="marketplace-recovery"]').exists()).toBe(true)
    const key = installPlugin.mock.calls[0]?.[0].idempotencyKey
    // 关键：网络失败后不再发第二次写请求。
    expect(installPlugin).toHaveBeenCalledTimes(1)
    await wrapper.get('[data-testid="marketplace-recovery-check"]').trigger('click')
    await flushPromises()
    expect(pluginOperation).toHaveBeenCalledTimes(1)
    expect(pluginOperation.mock.calls[0]?.[0]).toBe(key)
    expect(installPlugin).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="marketplace-recovery"]').exists()).toBe(false)
  })

  it('validates the data source locally and never fetches the URL', async () => {
    const resolvePluginSource = vi.fn(async () => ({ manifest: syntheticManifest, trustNotice: '请核对 SHA-256。' }))
    const { wrapper } = await mountDialog(clientWith({ resolvePluginSource }))
    await wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://example.com/plugin.zip')
    await wrapper.get('[data-testid="marketplace-source-resolve"]').trigger('click')
    await flushPromises()
    expect(resolvePluginSource).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="marketplace-source-error"]').text()).toContain('GitHub')
    await wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/example-plugin/releases/tag/v1.0.0')
    await wrapper.get('[data-testid="marketplace-source-resolve"]').trigger('click')
    await flushPromises()
    expect(resolvePluginSource).toHaveBeenCalledWith('https://github.com/example-org/example-plugin/releases/tag/v1.0.0')
    expect(wrapper.get('[data-testid="marketplace-notice"]').text()).toContain('SHA-256')
  })
})
