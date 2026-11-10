import { afterEach, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Market from '../../src/features/capabilities/PluginMarketplaceDialog.vue'
import { ZbaClient } from '../../src/api/zba'
import { clearPendingOperations, getPendingOperation } from '../../src/shared/sync/pending-operations'
import { marketDialogShell, previewFor } from '../fixtures/plugin-import'
import { syntheticManifest } from '../fixtures/provider-marketplace'

const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren() })

it('lookup conflict retains original key and tells the user not to submit another key', async () => {
  const keys: string[] = []
  const c = new ZbaClient({ baseUrl: '/synthetic-recovery-copy', token: 'synthetic-only', fetchImpl: vi.fn(async (_url, init) => {
    if (init?.method === 'POST') {
      keys.push(JSON.parse(String(init.body)).idempotencyKey)
      return new Response('<html>synthetic proxy response</html>', { status: 408, headers: { 'Content-Type': 'text/html' } })
    }
    return new Response(JSON.stringify({ error: { code: 'idempotency_conflict', message: 'Synthetic association unverifiable' } }), { status: 409, headers: { 'Content-Type': 'application/json' } })
  }) })
  vi.spyOn(c, 'pluginCatalog').mockResolvedValue({ revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic only', categories: [], plugins: [{ manifest: syntheticManifest, installedVersion: null, enabled: false, status: 'available', restartRequired: false }] })
  vi.spyOn(c, 'installedPlugins').mockResolvedValue({ revision: 'p1', plugins: [] })
  vi.spyOn(c, 'previewPlugin').mockImplementation(async request => previewFor(request))
  const lookup = vi.spyOn(c, 'pluginOperation')
  const w = mount(Market, { props: { client: c, mode: 'admin', writesEnabled: true }, attachTo: document.body, global: { stubs: { ManagementDialog: marketDialogShell } } })
  wrappers.push(w); await flushPromises()
  await w.get('[data-testid="marketplace-install"]').trigger('click'); await flushPromises()
  await w.get('[data-testid="preview-acknowledge"]').setValue(true)
  await w.get('[data-testid="preview-confirm"]').trigger('click'); await flushPromises()
  expect(keys).toHaveLength(1)
  await w.get('[data-testid="marketplace-recovery-check"]').trigger('click'); await flushPromises()
  expect(lookup).toHaveBeenCalledExactlyOnceWith(keys[0])
  expect(getPendingOperation(c, 'plugin', syntheticManifest.id)).toMatchObject({ key: keys[0], state: 'unknown' })
  expect(w.get('[data-testid="marketplace-install"]').attributes('disabled')).toBeDefined()
  expect(keys).toHaveLength(1)
  const message = w.get('[data-testid="marketplace-notice"]').text()
  expect(message).toContain('保留原操作键')
  expect(message).toContain('不要换键重复提交')
  expect(message).not.toContain('已被服务端作废')
  expect(message).not.toContain('换一个操作键提交')
})
