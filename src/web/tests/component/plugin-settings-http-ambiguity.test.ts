import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import Dialog from '../../src/features/capabilities/DeclaredPluginSettingsDialog.vue'
import { ZbaClient } from '../../src/api/zba'
import { clearPendingOperations, getPendingOperation } from '../../src/shared/sync/pending-operations'
import { marketDialogShell } from '../fixtures/plugin-import'
import { syntheticSettings } from '../fixtures/provider-marketplace'

const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); clearPendingOperations(); document.body.replaceChildren() })
describe('declared settings use original key after ambiguous HTTP', () => {
  it.each([408, 409, 429])('retains original settings operation after HTTP %i', async status => {
    const keys: string[] = []
    const c = new ZbaClient({ baseUrl: '/synthetic-settings-http-' + status, token: 'synthetic-only', fetchImpl: vi.fn(async (_url, init) => {
      keys.push(JSON.parse(String(init?.body)).idempotencyKey)
      return new Response('<html>synthetic proxy response</html>', { status, headers: { 'Content-Type': 'text/html' } })
    }) })
    vi.spyOn(c, 'pluginSettings').mockResolvedValue({ revision: 's1', schema: syntheticSettings, values: { caption: 'old' }, configuredSecrets: {}, restartRequired: false })
    const w = mount(Dialog, { props: { client: c, mode: 'admin', writesEnabled: true, active: true, pluginId: 'example.a' }, attachTo: document.body, global: { stubs: { ManagementDialog: marketDialogShell } } })
    wrappers.push(w); await flushPromises()
    await w.get('[data-testid="plugin-setting-caption"]').setValue('new')
    await w.get('[data-testid="declared-plugin-settings-save"]').trigger('click'); await flushPromises()
    expect(keys).toHaveLength(1)
    const pending = getPendingOperation(c, 'plugin-settings', 'example.a')
    const recovery = w.find('[data-testid="declared-plugin-settings-recovery"]').exists()
    if (!pending) {
      await w.get('[data-testid="plugin-setting-caption"]').setValue('new')
      await w.get('[data-testid="declared-plugin-settings-save"]').trigger('click'); await flushPromises()
    }
    const observed = { status, state: pending?.state ?? null, retainsOriginalKey: pending?.key === keys[0], recovery, submittedCount: keys.length, distinctKeyCount: new Set(keys).size }
    expect(observed).toEqual({ status, state: 'unknown', retainsOriginalKey: true, recovery: true, submittedCount: 1, distinctKeyCount: 1 })
  })
})
