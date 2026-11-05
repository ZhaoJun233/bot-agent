import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import PluginMarketplaceDialog from '../../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient } from '../../../src/api/zba'
import { syntheticManifest } from '../../fixtures/provider-marketplace'

const wrappers: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.replaceChildren() })
const manifest = syntheticManifest
async function setup() {
  const client = new ZbaClient({ baseUrl: '/synthetic/issue148-marketplace', token: 'synthetic-web-admin-token' })
  vi.spyOn(client, 'pluginCatalog').mockResolvedValue({
    revision: 'r1', source: 'https://example.com/index.json', trustNotice: '合成来源', categories: [],
    plugins: [{ manifest, installedVersion: manifest.version, enabled: false, status: 'installed', restartRequired: false }]
  })
  vi.spyOn(client, 'installedPlugins').mockResolvedValue({ revision: 'r1', plugins: [] })
  const read = vi.spyOn(client, 'pluginSettings').mockResolvedValue({
    revision: 'v1', restartRequired: false,
    schema: [{ key: 'label', label: '显示名称', type: 'string' }, { key: 'accessKey', label: '访问密钥', type: 'secret' }],
    values: { label: '已保存' }, configuredSecrets: { accessKey: false }
  })
  const wrapper = mount(PluginMarketplaceDialog, { attachTo: document.body,
    props: { client, mode: 'admin', writesEnabled: true, active: true, focusPluginId: manifest.id } })
  wrappers.push(wrapper); await flushPromises()
  return { wrapper, read }
}
function input(selector: string, value: string): void {
  const element = document.querySelector<HTMLInputElement>(selector)!
  element.value = value; element.dispatchEvent(new Event('input', { bubbles: true }))
}
async function draft(): Promise<void> {
  input('[data-testid="plugin-setting-label"]', '草稿')
  const mode = document.querySelector<HTMLSelectElement>('[data-testid="plugin-setting-accessKey-mode"]')!
  mode.value = 'set'; mode.dispatchEvent(new Event('change', { bubbles: true }))
  await flushPromises(); input('[data-testid="plugin-setting-accessKey"]', 'synthetic-secret')
  await flushPromises()
}
async function click(selector: string): Promise<void> {
  document.querySelector<HTMLButtonElement>(selector)!.click(); await flushPromises()
}

describe('issue148 marketplace draft lifetime', () => {
  it('requires explicit consent before a settings reload discards ordinary or secret draft', async () => {
    const { read } = await setup()
    await draft()
    await flushPromises(); const before = read.mock.calls.length
    await click('[data-testid="marketplace-settings-reload"]')
    expect(read).toHaveBeenCalledTimes(before)
    expect(document.querySelector<HTMLInputElement>('[data-testid="plugin-setting-label"]')!.value).toBe('草稿')
    await click('[data-testid="marketplace-discard-cancel"]')
    await click('[data-testid="marketplace-settings-reload"]')
    await click('[data-testid="marketplace-discard-confirm"]')
    expect(read).toHaveBeenCalledTimes(before + 1)
    expect(document.querySelector<HTMLInputElement>('[data-testid="plugin-setting-label"]')!.value).toBe('已保存')
    expect(document.querySelector('[data-testid="plugin-setting-accessKey"]')).toBeNull()
  })
  it('retains ordinary draft across navigation but clears secret input immediately', async () => {
    const { wrapper } = await setup()
    await draft()
    await flushPromises()
    expect(await wrapper.vm.requestLeave('navigate')).toBe(true)
    await flushPromises()
    expect(document.querySelector<HTMLInputElement>('[data-testid="plugin-setting-label"]')!.value).toBe('草稿')
    expect(document.querySelector('[data-testid="plugin-setting-accessKey"]')).toBeNull()
    const unload = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(unload)
    expect(unload.defaultPrevented).toBe(true)
  })
})
