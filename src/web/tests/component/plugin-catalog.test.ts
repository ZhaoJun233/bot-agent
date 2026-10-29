import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import PluginCatalogBrowser from '../../src/features/capabilities/PluginCatalogBrowser.vue'
import type { PluginCatalogEntry } from '../../src/features/capabilities/plugin-catalog'
import { syntheticManifest } from '../fixtures/provider-marketplace'
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })
const entry: PluginCatalogEntry = { id: 'example.plugin', name: '示例插件', summary: '示例功能', category: '工具', manifest: syntheticManifest }
function browser(entries: PluginCatalogEntry[] = [entry], canWrite = true) {
  const wrapper = mount(PluginCatalogBrowser, { props: { entries, canWrite } }); mounted.push(wrapper); return wrapper
}
describe('marketplace catalog trust gate', () => {
  it('shows fixed version, integrity, permissions and in-process warning before install', async () => {
    const wrapper = browser()
    expect(wrapper.get('[data-testid="marketplace-version"]').text()).toBe('1.0.0')
    expect(wrapper.get('[data-testid="marketplace-sha256"]').text()).toBe('a'.repeat(64))
    expect(wrapper.get('[data-testid="marketplace-permissions"]').text()).toContain('messages.read')
    expect(wrapper.get('[data-testid="marketplace-trust-warning"]').text()).toContain('不是沙箱')
    expect(wrapper.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="marketplace-trust"]').setValue(true)
    await wrapper.get('[data-testid="marketplace-install"]').trigger('click')
    expect(wrapper.emitted('install')).toEqual([[entry]])
    expect(wrapper.text()).toContain('不会自动重启')
  })
  it('filters by search/category, clears trust for a different release, and handles empty results', async () => {
    const other: PluginCatalogEntry = { ...entry, id: 'other.plugin', name: '另一个插件', category: '消息', manifest: { ...syntheticManifest, id: 'other.plugin', sha256: 'b'.repeat(64) } }
    const wrapper = browser([entry, other])
    await wrapper.get('[data-testid="marketplace-trust"]').setValue(true)
    await wrapper.get('[data-testid="marketplace-category"]').setValue('消息')
    expect(wrapper.findAll('.catalog-entry')).toHaveLength(1)
    expect(wrapper.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="marketplace-search"]').setValue('not-found')
    expect(wrapper.get('[data-testid="marketplace-empty"]').text()).toContain('没有匹配')
    expect(wrapper.find('[data-testid="marketplace-detail"]').exists()).toBe(false)
  })
  it('never enables installation for read-only users even when trust is checked programmatically', async () => {
    const wrapper = browser([entry], false)
    await wrapper.get('[data-testid="marketplace-trust"]').setValue(true)
    await wrapper.get('[data-testid="marketplace-install"]').trigger('click')
    expect(wrapper.emitted('install')).toBeUndefined()
  })
  it('locks a staged release and requires explicit confirmation before uninstall', async () => {
    const wrapper = browser([{ ...entry, installedVersion: '1.0.0' }])
    await wrapper.get('[data-testid="marketplace-uninstall"]').trigger('click')
    expect(wrapper.emitted('uninstall')).toBeUndefined()
    await wrapper.get('[data-testid="marketplace-uninstall-confirm"]').trigger('click')
    expect(wrapper.emitted('uninstall')).toHaveLength(1)
    await wrapper.setProps({ entries: [{ ...entry, installedVersion: '1.0.0', pendingUninstall: true }] })
    expect(wrapper.get('[data-testid="marketplace-package-state"]').text()).toContain('需重启')
    expect(wrapper.find('[data-testid="marketplace-uninstall-confirm"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="marketplace-uninstall"]').attributes('disabled')).toBeDefined()
  })
})
