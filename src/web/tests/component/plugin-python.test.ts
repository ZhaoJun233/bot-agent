import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Browser from '../../src/features/capabilities/PluginCatalogBrowser.vue'
import Market from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient } from '../../src/api/zba'
import { catalogEntry } from '../../src/features/capabilities/plugin-marketplace'
import { clearPendingOperations } from '../../src/shared/sync/pending-operations'
import { pythonManifest } from '../fixtures/python-plugin'
import { packageItem, presentation } from '../fixtures/plugin-presentation'
import { installPreview, marketDialogShell } from '../fixtures/plugin-import'
const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren() })

describe('Python marketplace readiness and existing confirmation path', () => {
  it.each([false, undefined])('blocks unready catalog acquisition but leaves cleanup available: %s', async ready => {
    const entry = catalogEntry(packageItem({ manifest: pythonManifest, installedVersion: '0.9.0', isCompatible: ready, compatibilityMessage: 'Python 环境未准备完成', metadata: presentation({ runtime: 'python' }) }))
    const wrapper = mount(Browser, { props: { entries: [entry], canWrite: true } }); wrappers.push(wrapper)
    expect(wrapper.text()).toContain('Python 环境未准备完成')
    expect(wrapper.text()).toContain('子进程不是安全沙箱')
    expect(wrapper.get('[data-testid="marketplace-update"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="marketplace-update"]').trigger('click')
    expect(wrapper.emitted('install')).toBeUndefined()
    expect(wrapper.get('[data-testid="marketplace-uninstall"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('[data-testid="marketplace-uninstall"]').trigger('click')
    expect(wrapper.emitted('uninstall')).toEqual([[entry]])
  })
  it('limits successful readiness to interpreter checks, not package activation', () => {
    const entry = catalogEntry(packageItem({ manifest: pythonManifest, isCompatible: true, compatibilityMessage: null }))
    const wrapper = mount(Browser, { props: { entries: [entry], canWrite: true } }); wrappers.push(wrapper)
    expect(wrapper.get('[data-testid="runtime-readiness"]').text()).toBe('解释器版本检查通过；包初始化与工具发现将在显式启用时执行。')
    expect(wrapper.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeUndefined()
  })
  it.each([true, false])('uses server preview, never blanket-disables Python or skips confirmation: %s', async ready => {
    const client = new ZbaClient({ baseUrl: '/synthetic-python' })
    vi.spyOn(client, 'pluginCatalog').mockResolvedValue({ revision: 'rev-1', source: 'bundled', trustNotice: 'Not a sandbox', categories: [], plugins: [packageItem({ manifest: pythonManifest, isCompatible: true })] })
    vi.spyOn(client, 'installedPlugins').mockResolvedValue({ revision: 'rev-1', plugins: [] })
    const preview = vi.spyOn(client, 'previewPlugin').mockResolvedValue(installPreview({ manifest: pythonManifest, canInstall: ready, checks: [{ code: 'python-runtime', status: ready ? 'passed' : 'blocked', message: ready ? '环境已准备' : 'Python 不可用' }] }))
    const install = vi.spyOn(client, 'installPlugin').mockResolvedValue({ revision: 'rev-2', operation: { id: 'python-install', pluginId: pythonManifest.id, kind: 'install', status: 'installed', restartRequired: false } })
    const wrapper = mount(Market, { props: { client, mode: 'admin', writesEnabled: true }, attachTo: document.body, global: { stubs: { ManagementDialog: marketDialogShell } } }); wrappers.push(wrapper)
    await flushPromises(); await wrapper.get('[data-testid="marketplace-install"]').trigger('click'); await flushPromises()
    expect(preview).toHaveBeenCalledWith({ origin: 'catalog', manifest: pythonManifest, expectedRevision: 'rev-1' })
    expect(install).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="plugin-install-preview"]').text()).toContain('Python >=3.11、<4')
    await wrapper.get('[data-testid="preview-acknowledge"]').setValue(true)
    expect(wrapper.get('[data-testid="preview-confirm"]').attributes('disabled') !== undefined).toBe(!ready)
    await wrapper.get('[data-testid="preview-confirm"]').trigger('click'); await flushPromises()
    if (ready) expect(install).toHaveBeenCalledWith(expect.objectContaining({ previewId: 'preview-example', manifest: pythonManifest, trustAcknowledged: true }))
    else expect(install).not.toHaveBeenCalled()
  })
})
