import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, reactive, shallowRef } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient, type ZbaInstalledPlugins } from '../../src/api/zba'
import { usePluginLifecyclePreview } from '../../src/features/capabilities/use-plugin-lifecycle-preview'
import type { PluginLifecyclePreview } from '../../src/api/plugin-lifecycle'
import { currentManifest, nextManifest, previousManifest, installedRow, lifecyclePreview, updateRequest } from '../fixtures/plugin-lifecycle'
const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(w => w.unmount()); vi.useRealTimers() })
function setup() {
  const client = shallowRef(new ZbaClient({ baseUrl: '/synthetic-lifecycle' }))
  const installed = reactive<ZbaInstalledPlugins>({ revision: 'rev-1', plugins: [installedRow()] })
  const context = reactive({ revision: 'rev-1', canWrite: true, active: true, blockedIds: [] as string[] })
  const request = vi.spyOn(client.value, 'previewPluginLifecycle').mockImplementation(async value => lifecyclePreview(value))
  let state!: ReturnType<typeof usePluginLifecyclePreview>
  const wrapper = mount(defineComponent({ setup() { state = usePluginLifecyclePreview({ client: () => client.value, installed: () => installed,
    revision: () => context.revision, canWrite: () => context.canWrite, active: () => context.active, blockedIds: () => context.blockedIds }); return () => null } }))
  wrappers.push(wrapper); return { client, installed, context, request, state, wrapper }
}
describe('disposable lifecycle previews', () => {
  it('rejects failed-install labeling when an Active version is still projected', async () => {
    const h = setup(); h.installed.plugins[0]!.pendingManifest = currentManifest; await flushPromises()
    const request = { kind: 'uninstall' as const, pluginId: currentManifest.id, expectedRevision: 'rev-1' }
    h.request.mockResolvedValue(lifecyclePreview(request, { recovery: 'failed-install', pendingManifest: currentManifest }))
    await h.state.request(request)
    expect(h.state.preview.value).toBeNull(); h.state.acknowledged.value = true; expect(h.state.consume()).toBeNull()
  })
  it('binds failed-install cleanup to the exact pending-only snapshot', async () => {
    const h = setup()
    h.installed.plugins = [installedRow({ manifest: nextManifest, installedVersion: null, pendingManifest: nextManifest, previousManifest: null, status: 'failed', enabled: false })]
    await flushPromises()
    const request = { kind: 'uninstall' as const, pluginId: nextManifest.id, expectedRevision: 'rev-1' }
    const value = lifecyclePreview(request, { recovery: 'failed-install', currentManifest: nextManifest,
      pendingManifest: nextManifest, currentStatus: 'failed', enabled: false })
    h.request.mockResolvedValue(value); await h.state.request(request)
    expect(h.state.preview.value?.recovery).toBe('failed-install')
    h.state.acknowledged.value = true; expect(h.state.canConfirm.value).toBe(true)
    h.installed.plugins[0]!.pendingManifest = { ...nextManifest, sha256: 'd'.repeat(64) }; await flushPromises()
    expect(h.state.consume()).toBeNull()
    await h.state.request(request); expect(h.state.preview.value).toBeNull()
  })
  it('consumes only once after exact acknowledgement', async () => {
    const { state } = setup(); await state.request(updateRequest())
    expect(state.consume()).toBeNull(); state.acknowledged.value = true; expect(state.consume()?.previewId).toBe('lifecycle-example'); expect(state.consume()).toBeNull()
  })
  it.each(['revision', 'admin', 'active', 'caller', 'pending', 'enabled', 'manifest', 'previous', 'stored-pending'] as const)('clears preview and acknowledgement on %s drift', async field => {
    const h = setup(); await h.state.request(updateRequest()); h.state.acknowledged.value = true
    if (field === 'revision') h.context.revision = 'rev-2'
    if (field === 'admin') h.context.canWrite = false
    if (field === 'active') h.context.active = false
    if (field === 'caller') h.client.value = new ZbaClient({ baseUrl: '/changed-caller' })
    if (field === 'pending') h.context.blockedIds = [currentManifest.id]
    if (field === 'enabled') h.installed.plugins[0]!.enabled = false
    if (field === 'manifest') h.installed.plugins[0]!.manifest = { ...currentManifest, permissions: ['network'] }
    if (field === 'previous') h.installed.plugins[0]!.previousManifest = null
    if (field === 'stored-pending') h.installed.plugins[0]!.pendingManifest = nextManifest
    expect(h.state.preview.value).toBeNull(); expect(h.state.acknowledged.value).toBe(false); expect(h.state.consume()).toBeNull()
  })
  it('rejects expiry between acknowledgment and consume and clears it on the clock tick', async () => {
    vi.useFakeTimers(); const h = setup(); h.request.mockImplementation(async value => lifecyclePreview(value, { expiresAt: new Date(Date.now() + 10).toISOString() }))
    await h.state.request(updateRequest()); h.state.acknowledged.value = true
    vi.advanceTimersByTime(20); expect(h.state.consume()).toBeNull(); expect(h.state.acknowledged.value).toBe(false)
  })
  it.each(['kind', 'id', 'origin', 'target', 'current', 'enabled', 'status', 'previous', 'pending'] as const)('rejects validly-shaped but mismatched %s snapshots', async field => {
    const h = setup(); const selection = field === 'previous' ? { kind: 'rollback' as const, pluginId: currentManifest.id } : updateRequest()
    h.request.mockImplementation(async request => {
      const value = lifecyclePreview(request)
      if (field === 'kind') value.kind = 'uninstall'
      if (field === 'id') value.pluginId = 'other'
      if (field === 'origin') value.targetSource!.origin = 'json'
      if (field === 'target') value.targetManifest = { ...nextManifest, downloadUrl: 'https://example.com/different.zip' }
      if (field === 'current') value.currentManifest = { ...currentManifest, entryType: 'Other.Type' }
      if (field === 'enabled') value.enabled = false
      if (field === 'status') value.currentStatus = 'staged'
      if (field === 'previous') value.targetManifest = { ...previousManifest, permissions: ['network'] }
      if (field === 'pending') value.pendingManifest = nextManifest
      return value
    })
    await h.state.request(selection); h.state.acknowledged.value = true
    expect(h.state.preview.value).toBeNull(); expect(h.state.consume()).toBeNull(); expect(h.state.error.value).toContain('不一致')
  })
  it('ignores late completion after candidate replacement, context drift or unmount', async () => {
    const h = setup(); let resolve!: (value: PluginLifecyclePreview) => void
    h.request.mockImplementationOnce(() => new Promise(r => { resolve = r }))
    const pending = h.state.request(updateRequest()); h.context.revision = 'rev-2'; h.wrapper.unmount()
    resolve(lifecyclePreview(updateRequest())); await pending; await flushPromises()
    expect(h.state.preview.value).toBeNull(); expect(h.state.consume()).toBeNull()
  })
  it('requires Pending for same-current failed recovery, never infers it from failed status', async () => {
    const h = setup(); h.installed.plugins[0]!.status = 'failed'; h.installed.plugins[0]!.previousManifest = null
    h.request.mockImplementation(async request => lifecyclePreview(request, { recovery: 'failed-pending', pendingManifest: nextManifest, currentStatus: 'failed', targetManifest: currentManifest }))
    await h.state.request({ kind: 'rollback', pluginId: currentManifest.id }); expect(h.state.preview.value).toBeNull()
    h.installed.plugins[0]!.pendingManifest = nextManifest
    await h.state.request({ kind: 'rollback', pluginId: currentManifest.id }); h.state.acknowledged.value = true
    expect(h.state.consume()?.recovery).toBe('failed-pending')
  })
})