import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Market from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient } from '../../src/api/zba'
import { clearPendingOperations, getPendingOperation } from '../../src/shared/sync/pending-operations'
import { marketDialogShell, previewFor } from '../fixtures/plugin-import'
import { syntheticManifest } from '../fixtures/provider-marketplace'

const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren() })

describe('original plugin key after ambiguous proxy response', () => {
  it.each([408, 409, 429])('retains original operation after HTTP %i without authoritative rejection', async status => {
    const submittedKeys: string[] = []
    const c = new ZbaClient({ baseUrl: '/synthetic-http-' + status, token: 'synthetic-only', fetchImpl: vi.fn(async (_url, init) => {
      const request = JSON.parse(String(init?.body))
      submittedKeys.push(request.idempotencyKey)
      // The proxy response does not establish whether the origin accepted the operation.
      return new Response('<html>synthetic proxy response</html>', { status, headers: { 'Content-Type': 'text/html' } })
    }) })
    vi.spyOn(c, 'pluginCatalog').mockResolvedValue({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic only', categories: [], plugins: [{ manifest: syntheticManifest, installedVersion: null, enabled: false, status: 'available', restartRequired: false }] })
    vi.spyOn(c, 'installedPlugins').mockResolvedValue({ revision: 'p1', plugins: [] })
    vi.spyOn(c, 'previewPlugin').mockImplementation(async request => previewFor(request))
    const w = mount(Market, { props: { client: c, mode: 'admin', writesEnabled: true }, attachTo: document.body, global: { stubs: { ManagementDialog: marketDialogShell } } })
    wrappers.push(w)
    await flushPromises()
    async function install() {
      await w.get('[data-testid="marketplace-install"]').trigger('click'); await flushPromises()
      await w.get('[data-testid="preview-acknowledge"]').setValue(true)
      await w.get('[data-testid="preview-confirm"]').trigger('click'); await flushPromises()
    }
    await install()
    expect(submittedKeys).toHaveLength(1)
    const pending = getPendingOperation(c, 'plugin', syntheticManifest.id)
    const blocked = w.get('[data-testid="marketplace-install"]').attributes('disabled') !== undefined
    const recovery = w.find('[data-testid="marketplace-recovery-check"]').exists()
    if (!blocked) await install()
    const observed = { status, state: pending?.state ?? null, retainsOriginalKey: pending?.key === submittedKeys[0], blocked, recovery, submittedCount: submittedKeys.length, distinctKeyCount: new Set(submittedKeys).size }
    expect(observed).toEqual({ status, state: 'unknown', retainsOriginalKey: true, blocked: true, recovery: true, submittedCount: 1, distinctKeyCount: 1 })
  })
})
