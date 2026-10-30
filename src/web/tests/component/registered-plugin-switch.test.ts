import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import RegisteredPluginSwitch from '../../src/features/capabilities/RegisteredPluginSwitch.vue'
import { ZbaClient, ZbaNetworkError, type ZbaConfigurationReceipt } from '../../src/api/zba'
import { providerSnapshot } from '../fixtures/provider-marketplace'
const pluginKey = 'preset.feature.token-quota'
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })
function snapshot(enabled = true, version = '1') {
  const result = providerSnapshot([], version)
  result.view!.registered.plugins = [{ key: pluginKey, desiredEnabled: enabled }]
  return result
}
function receipt(key: string): ZbaConfigurationReceipt {
  return { key, contractRevision: 'cfg-0.3.3', decision: 0, observedVersion: '1', committedVersion: '2', commitState: 1, applyState: 3, steps: [], deviceKeyAssignments: [] }
}
function clientWith(overrides: Partial<ZbaClient> = {}): ZbaClient {
  return { readConfiguration: vi.fn(async () => snapshot()), saveConfiguration: vi.fn(), findConfiguration: vi.fn(), ...overrides } as unknown as ZbaClient
}
function editor(client = clientWith(), extra = {}) {
  const wrapper = mount(RegisteredPluginSwitch, { props: { client, pluginKey, mode: 'admin', writesEnabled: true, active: true, blocked: false, runtimeState: 4, ...extra } })
  mounted.push(wrapper); return wrapper
}
describe('registered quota plugin switch', () => {
  it('saves only the selected plugin with CAS, receipt and restart notice', async () => {
    let saved = false
    const client = clientWith({ readConfiguration: vi.fn(async () => snapshot(!saved, saved ? '2' : '1')),
      saveConfiguration: vi.fn<ZbaClient['saveConfiguration']>(async request => { saved = true; return { code: 0, errors: [], currentVersion: '2', replayed: false, receipt: receipt(request.key) } }) })
    const wrapper = editor(client); await flushPromises()
    await wrapper.get('[data-testid="quota-plugin-toggle"]').trigger('click'); await flushPromises()
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
    expect(vi.mocked(client.saveConfiguration).mock.calls[0]?.[0]).toMatchObject({ expectedVersion: '1', changes: { registered: { plugins: [{ key: pluginKey, enabled: false }], providers: [], policies: [] } }, secrets: [] })
    expect(wrapper.get('[data-testid="quota-plugin-desired"]').text()).toContain('停用')
    expect(wrapper.text()).toContain('需重启'); expect(wrapper.text()).toContain('运行中')
  })
  it('keeps unknown intent across hiding and reconciles the original key without resend', async () => {
    const client = clientWith({ saveConfiguration: vi.fn(async () => { throw new ZbaNetworkError() }),
      findConfiguration: vi.fn<ZbaClient['findConfiguration']>(async key => ({ code: 0, receipt: receipt(key) })) })
    const wrapper = editor(client); await flushPromises()
    await wrapper.get('[data-testid="quota-plugin-toggle"]').trigger('click'); await flushPromises()
    expect(wrapper.emitted('busy')?.at(-1)).toEqual([true])
    await wrapper.setProps({ active: false }); await wrapper.setProps({ active: true })
    await wrapper.get('[data-testid="quota-plugin-check"]').trigger('click'); await flushPromises()
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
    expect(client.findConfiguration).toHaveBeenCalledWith(vi.mocked(client.saveConfiguration).mock.calls[0]?.[0].key)
    expect(wrapper.emitted('busy')?.at(-1)).toEqual([false])
  })
  it.each([{ mode: 'read' }, { writesEnabled: false }, { active: false }, { blocked: true }])('enforces mode and pending draft gates %#', async extra => {
    const client = clientWith(); const wrapper = editor(client, extra); await flushPromises()
    const button = wrapper.find('[data-testid="quota-plugin-toggle"]')
    if (button.exists()) { expect(button.attributes('disabled')).toBeDefined(); await button.trigger('click') }
    expect(client.saveConfiguration).not.toHaveBeenCalled()
  })
  it('locks on conflict until explicit refresh and uses the new CAS baseline', async () => {
    let reads = 0
    const client = clientWith({ readConfiguration: vi.fn(async () => snapshot(true, String(++reads))),
      saveConfiguration: vi.fn<ZbaClient['saveConfiguration']>(async () => ({ code: 3, errors: [], currentVersion: '2', replayed: false })) })
    const wrapper = editor(client); await flushPromises()
    await wrapper.get('[data-testid="quota-plugin-toggle"]').trigger('click'); await flushPromises()
    expect(wrapper.find('[data-testid="quota-plugin-toggle"]').exists()).toBe(false)
    await wrapper.get('[data-testid="quota-plugin-reload"]').trigger('click'); await flushPromises()
    await wrapper.get('[data-testid="quota-plugin-toggle"]').trigger('click'); await flushPromises()
    expect(vi.mocked(client.saveConfiguration).mock.calls[1]?.[0].expectedVersion).toBe('2')
  })
  it('requires reload when a confirmed save cannot refresh desired state', async () => {
    const client = clientWith({ readConfiguration: vi.fn<ZbaClient['readConfiguration']>().mockResolvedValueOnce(snapshot()).mockRejectedValue(new ZbaNetworkError()),
      saveConfiguration: vi.fn<ZbaClient['saveConfiguration']>(async request => ({ code: 0, errors: [], currentVersion: '2', replayed: false, receipt: receipt(request.key) })) })
    const wrapper = editor(client); await flushPromises()
    await wrapper.get('[data-testid="quota-plugin-toggle"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[data-testid="quota-plugin-toggle"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="quota-plugin-reload"]').exists()).toBe(true)
  })
})
