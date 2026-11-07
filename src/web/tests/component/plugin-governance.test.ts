import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import PluginGovernanceNotice from '../../src/features/capabilities/PluginGovernanceNotice.vue'
import PluginCatalogBrowser from '../../src/features/capabilities/PluginCatalogBrowser.vue'
import PluginInstallPreview from '../../src/features/capabilities/PluginInstallPreview.vue'
import PluginLifecyclePreview from '../../src/features/capabilities/PluginLifecyclePreview.vue'
import type { PluginGovernance } from '../../src/api/plugin-governance'
import { installPreview } from '../fixtures/plugin-import'
import { currentManifest, lifecyclePreview } from '../fixtures/plugin-lifecycle'
import { syntheticManifest } from '../fixtures/provider-marketplace'
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })
const revoked: PluginGovernance = { status: 'revoked', blocked: true, reason: '<img src=x onerror=alert(1)>', updatedAt: '2026-11-06T00:00:00Z', match: 'sha256' }

describe('governance risk notices and acquisition guard', () => {
  it('escapes advisory text and explicitly leaves installed remediation to the user', () => {
    const wrapper = mount(PluginGovernanceNotice, { props: { state: revoked, installed: true, showSource: true } }); mounted.push(wrapper)
    expect(wrapper.text()).toContain('禁止获取此包')
    expect(wrapper.text()).toContain('不会静默停用或删除')
    expect(wrapper.text()).toContain('不能视为已通过检查')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.text()).toContain('<img src=x')
  })
  it('does not substitute cached checks for up-to-date governance', () => {
    const wrapper = mount(PluginGovernanceNotice, { props: { sourceOnly: true, source: { status: 'cached', source: 'https://example.com/governance.json', generatedAt: '2026-11-01T00:00:00Z', checkedAt: '2026-11-06T00:00:00Z', errorCode: 'unavailable' } } }); mounted.push(wrapper)
    expect(wrapper.text()).toContain('不能确认未出现新撤销')
    expect(wrapper.text()).toContain('2026-11-01T00:00:00Z')
  })
  it('blocks target acquisition but preserves explicit installed removal', async () => {
    const entry = { id: syntheticManifest.id, name: 'Synthetic plugin', summary: 'Synthetic only', category: 'tools', manifest: { ...syntheticManifest, version: '2.0.0' }, installedVersion: '1.0.0', governance: revoked, installedGovernance: revoked }
    const wrapper = mount(PluginCatalogBrowser, { props: { entries: [entry], canWrite: true } }); mounted.push(wrapper)
    expect(wrapper.get('[data-testid="marketplace-update"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="marketplace-update"]').trigger('click')
    expect(wrapper.emitted('install')).toBeUndefined()
    expect(wrapper.get('[data-testid="marketplace-uninstall"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('[data-testid="marketplace-uninstall"]').trigger('click')
    expect(wrapper.emitted('uninstall')).toHaveLength(1)
    expect(wrapper.text()).toContain('当前已安装包')
  })
  it('shows native advisory warnings and retains explicit confirmation', async () => {
    const warning = { code: 'governance', status: 'warning' as const, message: 'Synthetic offline governance warning' }
    const install = mount(PluginInstallPreview, { props: { preview: installPreview({ checks: [warning] }), acknowledged: false, canConfirm: false, expired: false, busy: false } }); mounted.push(install)
    expect(install.get('[data-status="warning"]').text()).toContain('警告')
    expect(install.text()).toContain(warning.message)
    expect(install.get('[data-testid="preview-confirm"]').attributes('disabled')).toBeDefined()
    await install.setProps({ acknowledged: true, canConfirm: true })
    expect(install.get('[data-testid="preview-confirm"]').attributes('disabled')).toBeUndefined()
    const removal = mount(PluginLifecyclePreview, { props: { preview: lifecyclePreview({ kind: 'uninstall', expectedRevision: 'r1', pluginId: currentManifest.id }, { checks: [warning] }), acknowledged: true, canConfirm: true, expired: false, busy: false } }); mounted.push(removal)
    expect(removal.get('.lifecycle-checks').text()).toContain('警告')
    expect(removal.text()).toContain(warning.message)
    expect(removal.get('[data-testid="lifecycle-confirm"]').attributes('disabled')).toBeUndefined()
  })
  it('guards confirm buttons even if a caller mistakenly enables revoked previews', () => {
    const install = mount(PluginInstallPreview, { props: { preview: installPreview({ governance: revoked }), acknowledged: true, canConfirm: true, expired: false, busy: false } }); mounted.push(install)
    expect(install.get('[data-testid="preview-confirm"]').attributes('disabled')).toBeDefined()
    const rollback = mount(PluginLifecyclePreview, { props: { preview: lifecyclePreview({ kind: 'rollback', expectedRevision: 'r1', pluginId: currentManifest.id }, { targetGovernance: revoked }), acknowledged: true, canConfirm: true, expired: false, busy: false } }); mounted.push(rollback)
    expect(rollback.get('[data-testid="lifecycle-confirm"]').attributes('disabled')).toBeDefined()
  })
})
