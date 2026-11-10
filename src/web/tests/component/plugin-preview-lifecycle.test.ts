import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref, shallowRef } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient } from '../../src/api/zba'
import { usePluginPreview } from '../../src/features/capabilities/use-plugin-preview'
import { installPreview } from '../fixtures/plugin-import'
import { syntheticManifest } from '../fixtures/provider-marketplace'
import type { PluginInstallPreview } from '../../src/api/plugin-import'
const wrappers: Array<{ unmount(): void }> = []
afterEach(() => { wrappers.splice(0).forEach(wrapper => wrapper.unmount()); vi.useRealTimers() })
const selection = { origin: 'catalog' as const, manifest: syntheticManifest }
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r }); return { promise, resolve } }
function harness() {
  const client = shallowRef(new ZbaClient({ baseUrl: '/synthetic-preview' })); const revision = ref('rev-1'); const canWrite = ref(true); const active = ref(true); const blockedIds = ref<string[]>([])
  const request = vi.spyOn(client.value, 'previewPlugin').mockResolvedValue(installPreview())
  let state!: ReturnType<typeof usePluginPreview>
  wrappers.push(mount(defineComponent({ setup() { state = usePluginPreview({ client: () => client.value, revision: () => revision.value, canWrite: () => canWrite.value, active: () => active.value, blockedIds: () => blockedIds.value }); return () => h('div') } })))
  return { state, client, request, revision, canWrite, active, blockedIds }
}
describe('exact candidate preview lifecycle', () => {
  it('requires an acknowledgement and permits exactly one consumption', async () => {
    const { state } = harness(); await state.request(selection)
    expect(state.consume()).toBeNull(); state.acknowledged.value = true
    expect(state.consume()?.previewId).toBe('preview-example'); expect(state.consume()).toBeNull(); expect(state.acknowledged.value).toBe(false)
  })
  it.each(['revision', 'permission', 'visibility', 'principal', 'pending-operation'] as const)('invalidates acknowledgement on %s change', async change => {
    const harnessed = harness(); const { state } = harnessed; await state.request(selection); state.acknowledged.value = true
    if (change === 'revision') harnessed.revision.value = 'rev-2'
    if (change === 'permission') harnessed.canWrite.value = false
    if (change === 'visibility') harnessed.active.value = false
    if (change === 'principal') harnessed.client.value = new ZbaClient({ baseUrl: '/other-principal' })
    if (change === 'pending-operation') harnessed.blockedIds.value = [syntheticManifest.id]
    expect(state.preview.value).toBeNull(); expect(state.acknowledged.value).toBe(false); expect(state.consume()).toBeNull()
  })
  it('rejects a stale response after selection invalidation', async () => {
    const { state, request } = harness(); const pending = deferred<PluginInstallPreview>(); request.mockReturnValueOnce(pending.promise)
    const inflight = state.request(selection); state.invalidate(); pending.resolve(installPreview()); await inflight
    expect(state.preview.value).toBeNull(); expect(state.canConfirm.value).toBe(false)
  })
  it.each([{ revision: 'rev-2' }, { manifest: { ...syntheticManifest, id: 'example.other' } }, { manifest: { ...syntheticManifest, version: '2.0.0' } }, { manifest: { ...syntheticManifest, sha256: 'c'.repeat(64) } }, { source: { kind: 'json' as const } }])('rejects a mismatched preview response %j', async overrides => {
    const { state, request } = harness(); request.mockResolvedValueOnce(installPreview(overrides)); await state.request(selection)
    expect(state.preview.value).toBeNull(); expect(state.error.value).toContain('变化')
  })
  it.each([
    { downloadUrl: 'https://example.com/other.zip' }, { entryAssembly: 'Other.dll' }, { entryType: 'Other.Plugin' },
    { permissions: ['send'] }, { dependencies: [{ id: 'example.dependency', version: '2.0.0' }] },
    { settings: [{ key: 'caption', label: '标题', type: 'string' as const, defaultValue: 'changed' }] },
  ])('rejects changed technical content despite identical id/version/SHA %j', async fields => {
    for (const origin of ['catalog', 'json'] as const) {
      const { state, request } = harness()
      request.mockResolvedValueOnce(installPreview({ source: { kind: origin }, manifest: { ...syntheticManifest, ...fields } }))
      await state.request({ origin, manifest: syntheticManifest }); state.acknowledged.value = true
      expect(state.preview.value).toBeNull(); expect(state.error.value).toContain('变化'); expect(state.consume()).toBeNull()
    }
  })
  it('invalidates both acknowledged previews and late responses on unmount', async () => {
    const { state, request } = harness(); await state.request(selection); state.acknowledged.value = true
    wrappers.pop()!.unmount(); expect(state.preview.value).toBeNull(); expect(state.consume()).toBeNull()
    const other = harness(); const pending = deferred<PluginInstallPreview>(); other.request.mockReturnValueOnce(pending.promise)
    const inflight = other.state.request(selection); wrappers.pop()!.unmount(); pending.resolve(installPreview()); await inflight
    expect(other.state.preview.value).toBeNull(); expect(other.state.consume()).toBeNull(); expect(request).toHaveBeenCalledOnce()
  })
  it('revokes confirmation at expiry even without a fresh timer render', async () => {
    vi.useFakeTimers(); const { state, request } = harness(); request.mockResolvedValueOnce(installPreview({ expiresAt: new Date(Date.now() + 250).toISOString() }))
    await state.request(selection); state.acknowledged.value = true; expect(state.canConfirm.value).toBe(true)
    vi.setSystemTime(Date.now() + 300); expect(state.consume()).toBeNull()
    await vi.advanceTimersByTimeAsync(500); await flushPromises(); expect(state.acknowledged.value).toBe(false)
  })
  it('shows blocked preview but never allows acknowledgement to authorize it', async () => {
    const { state, request } = harness(); request.mockResolvedValueOnce(installPreview({ canInstall: false, checks: [{ code: 'dependency', status: 'blocked', message: '依赖缺失' }] }))
    await state.request(selection); state.acknowledged.value = true; expect(state.preview.value).not.toBeNull(); expect(state.consume()).toBeNull()
  })
})