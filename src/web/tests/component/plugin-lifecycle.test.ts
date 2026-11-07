import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Market from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaPluginCatalogItem, type ZbaPluginOperationKind } from '../../src/api/zba'
import { clearPendingOperations } from '../../src/shared/sync/pending-operations'
import { marketDialogShell, discovery } from '../fixtures/plugin-import'
import { currentManifest, nextManifest, previousManifest, installedRow, lifecyclePreview } from '../fixtures/plugin-lifecycle'
const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren(); vi.useRealTimers() })
function receipt(kind: ZbaPluginOperationKind, errorCode?: string) {
  return { revision: 'rev-2', operation: { id: 'operation-example', pluginId: currentManifest.id, kind,
    status: errorCode ? 'failed' as const : kind === 'uninstall' ? 'not_installed' as const : 'installed' as const, restartRequired: false, errorCode } }
}
async function harness(row: ZbaPluginCatalogItem = installedRow()) {
  const client = new ZbaClient({ baseUrl: '/synthetic-lifecycle' })
  vi.spyOn(client, 'pluginCatalog').mockResolvedValue({ revision: 'rev-1', source: 'https://example.com/catalog.json', trustNotice: '不是沙箱', categories: [], plugins: [{ ...row, manifest: nextManifest }] })
  vi.spyOn(client, 'installedPlugins').mockResolvedValue({ revision: 'rev-1', plugins: [row] })
  vi.spyOn(client, 'discoverPluginSource').mockResolvedValue(discovery())
  const preview = vi.spyOn(client, 'previewPluginLifecycle').mockImplementation(async request => lifecyclePreview(request, { enabled: row.enabled }))
  const freshPreview = vi.spyOn(client, 'previewPlugin'), install = vi.spyOn(client, 'installPlugin')
  const update = vi.spyOn(client, 'updatePlugin').mockResolvedValue(receipt('update'))
  const rollback = vi.spyOn(client, 'rollbackPlugin').mockResolvedValue(receipt('rollback'))
  const uninstall = vi.spyOn(client, 'uninstallPlugin').mockResolvedValue(receipt('uninstall'))
  const recovery = vi.spyOn(client, 'pluginOperation').mockResolvedValue(receipt('update'))
  const wrapper = mount(Market, { props: { client, mode: 'admin', writesEnabled: true }, attachTo: document.body, global: { stubs: { ManagementDialog: marketDialogShell } } })
  wrappers.push(wrapper); await flushPromises()
  return { client, wrapper, preview, freshPreview, install, update, rollback, uninstall, recovery }
}
type Wrapper = Awaited<ReturnType<typeof harness>>['wrapper']
async function open(wrapper: Wrapper, kind: string) { await wrapper.get('[data-testid="marketplace-' + kind + '"]').trigger('click'); await flushPromises() }
async function confirm(wrapper: Wrapper) { await wrapper.get('[data-testid="lifecycle-acknowledge"]').setValue(true); await wrapper.get('[data-testid="lifecycle-confirm"]').trigger('click'); await flushPromises() }
describe('preview-bound package lifecycle', () => {
  it.each([true, false])('keeps pending-only cleanup preview-bound (canApply=%s)', async canApply => {
    const h = await harness(installedRow({ manifest: nextManifest, installedVersion: null, pendingManifest: nextManifest, previousManifest: null,
      status: 'failed', enabled: false }))
    h.preview.mockImplementation(async request => lifecyclePreview(request, { recovery: 'failed-install',
      currentManifest: nextManifest, pendingManifest: nextManifest, currentStatus: 'failed', enabled: false, canApply,
      checks: [{ code: 'terminal-install', status: canApply ? 'passed' : 'blocked', message: canApply ? 'Known failed install' : 'Still unknown' }] }))
    const w = h.wrapper
    expect(w.find('[data-testid=marketplace-rollback]').exists()).toBe(false)
    expect(w.find('[data-testid=marketplace-update]').exists()).toBe(false)
    expect(w.get('[data-testid=marketplace-install]').attributes('disabled')).toBeDefined()
    await open(w, 'uninstall')
    expect(w.get('[data-testid=lifecycle-preview]').text()).toContain('未生效安装候选')
    expect(w.get('[data-testid=lifecycle-current-version]').text()).toBe(nextManifest.version)
    expect(w.get('[data-testid=lifecycle-preview]').text()).toContain(nextManifest.sha256)
    expect(h.uninstall).not.toHaveBeenCalled(); expect(w.get('[data-testid=lifecycle-confirm]').attributes('disabled')).toBeDefined()
    await w.get('[data-testid=lifecycle-acknowledge]').setValue(true)
    if (canApply) {
      await w.get('[data-testid=lifecycle-confirm]').trigger('click'); await flushPromises()
      expect(h.uninstall).toHaveBeenCalledWith(nextManifest.id, expect.objectContaining({ previewId: 'lifecycle-example', expectedRevision: 'rev-1', trustAcknowledged: true }))
      expect(h.uninstall.mock.calls[0]?.[1]).not.toHaveProperty('manifest')
    } else {
      expect(w.get('[data-testid=lifecycle-confirm]').attributes('disabled')).toBeDefined()
      await w.get('[data-testid=lifecycle-confirm]').trigger('click'); expect(h.uninstall).not.toHaveBeenCalled()
    }
    expect(h.update).not.toHaveBeenCalled(); expect(h.rollback).not.toHaveBeenCalled(); expect(h.install).not.toHaveBeenCalled()
  })
  it.each(['update', 'rollback', 'uninstall'] as const)('%s requires a readonly preview then exact acknowledgement before any mutation', async kind => {
    const h = await harness(); await open(h.wrapper, kind)
    expect(h.preview).toHaveBeenCalledTimes(1); expect(h.freshPreview).not.toHaveBeenCalled(); expect(h.install).not.toHaveBeenCalled()
    expect(h.update).not.toHaveBeenCalled(); expect(h.rollback).not.toHaveBeenCalled(); expect(h.uninstall).not.toHaveBeenCalled()
    expect(h.wrapper.get('[data-testid="lifecycle-preview"]').text()).toContain('保留现有设置、用户数据和启用意图')
    expect(h.wrapper.get('[data-testid="lifecycle-confirm"]').attributes('disabled')).toBeDefined()
    await h.wrapper.get('[data-testid="lifecycle-confirm"]').trigger('click'); expect(h[kind]).not.toHaveBeenCalled()
    await confirm(h.wrapper); expect(h[kind]).toHaveBeenCalledTimes(1)
    const request = kind === 'uninstall' ? h.uninstall.mock.calls[0]?.[1] : h[kind].mock.calls[0]?.[0]
    expect(request).toMatchObject({ previewId: 'lifecycle-example', expectedRevision: 'rev-1', trustAcknowledged: true })
    if (kind !== 'uninstall') expect(request).toMatchObject({ manifest: kind === 'update' ? nextManifest : previousManifest })
    expect(h.wrapper.find('[data-testid="lifecycle-confirm"]').exists()).toBe(false)
  })
  it.each(['repository', 'json'] as const)('routes installed %s candidates to update, never fresh installation', async origin => {
    const h = await harness()
    if (origin === 'repository') {
      await h.wrapper.get('[data-testid="marketplace-source-input"]').setValue('https://github.com/example-org/example-plugin')
      await h.wrapper.get('[data-testid="marketplace-source-resolve"]').trigger('click'); await flushPromises()
      await h.wrapper.get('[data-testid="source-version"]').setValue('2.0.0'); await h.wrapper.get('[data-testid="source-preview"]').trigger('click')
    } else {
      await h.wrapper.get('[data-testid="marketplace-json-input"]').setValue(JSON.stringify(nextManifest))
      await h.wrapper.get('[data-testid="json-preview"]').trigger('click')
    }
    await flushPromises(); expect(h.preview).toHaveBeenCalledWith(expect.objectContaining({ kind: 'update', pluginId: nextManifest.id, candidate: expect.objectContaining({ origin }) }))
    expect(h.freshPreview).not.toHaveBeenCalled(); expect(h.install).not.toHaveBeenCalled()
    await confirm(h.wrapper); expect(h.update).toHaveBeenCalledTimes(1); expect(h.install).not.toHaveBeenCalled()
  })
  it('does not enable rollback for status alone; permits recovery preview from persisted Active/Pending', async () => {
    const h = await harness(installedRow({ previousManifest: null, pendingManifest: nextManifest, status: 'failed' }))
    h.preview.mockImplementation(async request => lifecyclePreview(request, { currentStatus: 'failed', recovery: 'failed-pending', pendingManifest: nextManifest, targetManifest: currentManifest }))
    expect(h.wrapper.get('[data-testid="marketplace-update"]').attributes('disabled')).toBeDefined()
    expect(h.wrapper.get('[data-testid="marketplace-uninstall"]').attributes('disabled')).toBeDefined()
    await open(h.wrapper, 'rollback')
    const card = h.wrapper.get('[data-testid="lifecycle-preview"]')
    expect(card.text()).toContain('放弃失败候选'); expect(card.text()).toContain('不是数据回滚'); expect(card.text()).toContain(nextManifest.sha256)
    await confirm(h.wrapper); expect(h.rollback.mock.calls[0]?.[0].manifest).toEqual(currentManifest)
  })
  it('keeps rollback unavailable when no persisted Previous or Pending exists', async () => {
    const h = await harness(installedRow({ previousManifest: null, status: 'failed' })); await open(h.wrapper, 'rollback')
    expect(h.preview).not.toHaveBeenCalled(); expect(h.rollback).not.toHaveBeenCalled()
  })
  it('blocks dependency/settings failures and never manufactures a confirmation', async () => {
    const h = await harness(); h.preview.mockImplementation(async request => lifecyclePreview(request, { canApply: false, checks: [{ code: 'reverse-dependency', status: 'blocked', message: '依赖者固定了当前版本' }] }))
    await open(h.wrapper, 'uninstall'); expect(h.wrapper.get('[data-testid="lifecycle-error"]').text()).toContain('阻止')
    await h.wrapper.get('[data-testid="lifecycle-confirm"]').trigger('click'); expect(h.uninstall).not.toHaveBeenCalled()
  })
  it.each(['kind', 'json', 'permission', 'active', 'close'] as const)('invalidates acknowledgement on %s drift', async change => {
    const h = await harness(); await open(h.wrapper, 'update'); await h.wrapper.get('[data-testid="lifecycle-acknowledge"]').setValue(true)
    if (change === 'kind') await open(h.wrapper, 'rollback')
    if (change === 'json') await h.wrapper.get('[data-testid="marketplace-json-input"]').setValue('{}')
    if (change === 'permission') await h.wrapper.setProps({ writesEnabled: false })
    if (change === 'active') await h.wrapper.setProps({ active: false })
    if (change === 'close') await h.wrapper.get('[data-testid="marketplace-close"]').trigger('click')
    await flushPromises()
    expect(h.wrapper.find('[data-testid="lifecycle-confirm"]').exists() && h.wrapper.get('[data-testid="lifecycle-confirm"]').attributes('disabled') === undefined).toBe(false)
    expect(h.update).not.toHaveBeenCalled(); expect(h.rollback).not.toHaveBeenCalled()
  })
  it('preserves disabled intent and renders restart-required receipt without enabling the package', async () => {
    const h = await harness(installedRow({ enabled: false })); h.rollback.mockResolvedValue({ revision: 'rev-2', operation: { ...receipt('rollback').operation, status: 'restart_required', restartRequired: true } })
    await open(h.wrapper, 'rollback'); expect(h.wrapper.get('[data-testid="lifecycle-preview"]').text()).toContain('已停用')
    await confirm(h.wrapper); expect(h.rollback.mock.calls[0]?.[0]).not.toHaveProperty('enabled')
    expect(h.wrapper.get('[data-testid="marketplace-notice"]').text()).toContain('重启')
  })
  it('retains original-key recovery for wrong target and matching unknown, settling only a known terminal receipt', async () => {
    const h = await harness(); h.update.mockRejectedValueOnce(new ZbaNetworkError())
    h.recovery.mockResolvedValueOnce({ ...receipt('update'), operation: { ...receipt('update').operation, pluginId: 'other' } })
      .mockResolvedValueOnce(receipt('update', 'plugin_operation_unknown')).mockResolvedValueOnce(receipt('update', 'plugin_reload_failed'))
    await open(h.wrapper, 'update'); await confirm(h.wrapper)
    const key = h.update.mock.calls[0]?.[0].idempotencyKey
    for (let attempt = 0; attempt < 3; attempt++) {
      await h.wrapper.get('[data-testid="marketplace-recovery-check"]').trigger('click'); await flushPromises()
      expect(h.recovery).toHaveBeenLastCalledWith(key); expect(h.update).toHaveBeenCalledTimes(1)
      expect(h.wrapper.find('[data-testid="marketplace-recovery"]').exists()).toBe(attempt < 2)
    }
  })
  it('does not fall back or retry a rejected preview-bound mutation', async () => {
    const h = await harness(); h.rollback.mockRejectedValueOnce(new ZbaRequestError(409, 'plugin_preview_refresh_required', 'stale'))
    await open(h.wrapper, 'rollback'); await confirm(h.wrapper)
    expect(h.rollback).toHaveBeenCalledTimes(1); expect(h.update).not.toHaveBeenCalled(); expect(h.install).not.toHaveBeenCalled()
    expect(h.wrapper.get('[data-testid="marketplace-notice"]').text()).toContain('重新发现并预览')
  })
  it('does not preview or mutate in read mode', async () => {
    const h = await harness(); await h.wrapper.setProps({ mode: 'read' }); await open(h.wrapper, 'update'); await open(h.wrapper, 'rollback'); await open(h.wrapper, 'uninstall')
    expect(h.preview).not.toHaveBeenCalled(); expect(h.update).not.toHaveBeenCalled(); expect(h.rollback).not.toHaveBeenCalled(); expect(h.uninstall).not.toHaveBeenCalled()
  })
})