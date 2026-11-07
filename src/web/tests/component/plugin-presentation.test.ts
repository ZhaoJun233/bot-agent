import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient } from '../../src/api/zba'
import PluginCatalogBrowser from '../../src/features/capabilities/PluginCatalogBrowser.vue'
import PluginMarketplaceDialog from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { builtinCatalogEntry, catalogEntry } from '../../src/features/capabilities/plugin-marketplace'
import type { PluginCatalogEntry } from '../../src/features/capabilities/plugin-catalog'
import { pythonManifest } from '../fixtures/python-plugin'
import { builtin, packageItem, presentation } from '../fixtures/plugin-presentation'
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { mounted.splice(0).forEach(wrapper => wrapper.unmount()) })
function browser(entries: PluginCatalogEntry[], canWrite = true) {
  const wrapper = mount(PluginCatalogBrowser, { props: { entries, canWrite } }); mounted.push(wrapper); return wrapper
}
describe('Chinese unified marketplace presentation', () => {
  it('offers builtin configuration only, without fake package actions or audit claim', async () => {
    const entry = builtinCatalogEntry(builtin())
    const wrapper = browser([entry])
    expect(wrapper.text()).toContain('联网搜索')
    expect(wrapper.text()).toContain('已内置 · 已停用')
    expect(wrapper.get('[data-testid="review-badge"]').text()).toContain('未单独审计')
    for (const id of ['marketplace-install', 'marketplace-uninstall', 'marketplace-trust', 'marketplace-sha256'])
      expect(wrapper.find('[data-testid="' + id + '"]').exists()).toBe(false)
    await wrapper.get('[data-testid="configure-builtin"]').trigger('click')
    expect(wrapper.emitted('settings')).toEqual([[entry]])
    expect(wrapper.emitted('install')).toBeUndefined()
    expect(wrapper.emitted('uninstall')).toBeUndefined()
  })
  it('keeps readonly builtin inspection available without mutation controls', async () => {
    const wrapper = browser([builtinCatalogEntry(builtin())], false)
    expect(wrapper.get('[data-testid="configure-builtin"]').text()).toBe('查看配置')
    expect(wrapper.text()).toContain('不能保存变更')
    await wrapper.get('[data-testid="configure-builtin"]').trigger('click')
    expect(wrapper.emitted('settings')).toHaveLength(1)
  })
  it('filters shared Chinese discovery by source, runtime, review and state', async () => {
    const wrapper = browser([builtinCatalogEntry(builtin()), catalogEntry(packageItem())])
    await wrapper.get('[data-testid="marketplace-search"]').setValue('联网')
    expect(wrapper.findAll('.catalog-entry')).toHaveLength(1)
    await wrapper.get('[data-testid="marketplace-search"]').setValue('')
    await wrapper.get('[data-testid="catalog-source"]').setValue('official')
    await wrapper.get('[data-testid="catalog-runtime"]').setValue('dotnet')
    await wrapper.get('[data-testid="catalog-review"]').setValue('reviewed')
    await wrapper.get('[data-testid="catalog-state"]').setValue('available')
    expect(wrapper.findAll('.catalog-entry')).toHaveLength(1)
    expect(wrapper.get('[data-testid="marketplace-detail"]').text()).toContain('示例问候')
    await wrapper.get('[data-testid="catalog-runtime"]').setValue('python')
    expect(wrapper.get('[data-testid="marketplace-empty"]').text()).toContain('没有匹配')
  })
  it('separates official source from review and opens readonly install preview', async () => {
    const metadata = presentation({ repository: 'javascript:alert(1)', review: { ...presentation().review, sha256: 'c'.repeat(64) } })
    const wrapper = browser([catalogEntry(packageItem({ metadata }))])
    expect(wrapper.text()).toContain('官方发布')
    expect(wrapper.get('[data-testid="review-badge"]').text()).toBe('未审核')
    expect(wrapper.find('a').exists()).toBe(false)
    expect(wrapper.get('[data-testid="marketplace-install"]').text()).toBe('预览安装')
    expect(wrapper.find('[data-testid="marketplace-trust"]').exists()).toBe(false)
    await wrapper.get('[data-testid="marketplace-install"]').trigger('click')
    expect(wrapper.emitted('install')).toHaveLength(1)
  })
  it('routes installed upgrades to readonly preview instead of a global trust acknowledgement', async () => {
    const wrapper = browser([catalogEntry(packageItem({ installedVersion: '0.9.0' }))])
    expect(wrapper.get('[data-testid="marketplace-update"]').text()).toBe('预览升级')
    expect(wrapper.find('[data-testid="marketplace-trust"]').exists()).toBe(false)
    await wrapper.get('[data-testid="marketplace-update"]').trigger('click'); expect(wrapper.emitted('install')).toHaveLength(1)
  })
  it('renders descriptive strings as text and leaves unconfirmed Python execution unavailable', async () => {
    const wrapper = browser([catalogEntry(packageItem({ manifest: pythonManifest, metadata: presentation({ name: '<img src=x onerror=alert(1)>', runtime: 'python' }) }))])
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('<img src=x onerror=alert(1)>')
    expect(wrapper.text()).toContain('宿主尚未确认运行时兼容与环境准备状态')
    expect(wrapper.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="marketplace-trust"]').exists()).toBe(false)
  })
  it('shows metadata details and exact-version review provenance', () => {
    const wrapper = browser([catalogEntry(packageItem())])
    for (const text of ['使用', '配置说明', '兼容要求', '维护与更新', '权限与风险', '许可证', '示例审核者']) expect(wrapper.text()).toContain(text)
    expect(wrapper.get('[data-testid="review-badge"]').text()).toBe('已审核此版本与 ZIP')
  })
})
describe('dialog registry handoff', () => {
  it('shows fallback notice and routes builtin configuration without package mutations', async () => {
    const client = new ZbaClient({ baseUrl: 'https://example.com' })
    vi.spyOn(client, 'pluginCatalog').mockResolvedValue({ revision: 'r1', source: 'bundled', trustNotice: '内置不代表独立审计。', categories: [], plugins: [], builtinPlugins: [builtin()], metadataNotice: '扩展说明不可用；使用内置说明。' })
    vi.spyOn(client, 'installedPlugins').mockResolvedValue({ revision: 'r1', plugins: [], builtinPlugins: [builtin({ enabled: true })] })
    const install = vi.spyOn(client, 'installPlugin'); const update = vi.spyOn(client, 'updatePlugin'); const uninstall = vi.spyOn(client, 'uninstallPlugin')
    const wrapper = mount(PluginMarketplaceDialog, { props: { client, mode: 'admin', writesEnabled: true }, global: { stubs: { teleport: true } } }); mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.get('details.source-panel').element.hasAttribute('open')).toBe(true)
    expect(wrapper.get('[data-testid="marketplace-source-input"]').isVisible()).toBe(true)
    const discovery = wrapper.get('[data-testid="marketplace-search"]').element
    expect(discovery.compareDocumentPosition(wrapper.get('details.source-panel').element) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy()
    expect(wrapper.get('[data-testid="metadata-notice"]').text()).toContain('使用内置说明')
    expect(wrapper.get('[data-testid="marketplace-package-state"]').text()).toContain('已启用')
    await wrapper.get('[data-testid="configure-builtin"]').trigger('click')
    expect(wrapper.emitted('open-settings')).toEqual([[builtin().pluginId]])
    expect(install).not.toHaveBeenCalled(); expect(update).not.toHaveBeenCalled(); expect(uninstall).not.toHaveBeenCalled()
  })
})