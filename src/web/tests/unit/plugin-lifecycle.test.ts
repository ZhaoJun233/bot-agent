import { describe, expect, it } from 'vitest'
import { ZbaClient } from '../../src/api/zba'
import { isPluginLifecyclePreview } from '../../src/api/plugin-lifecycle'
import { isPluginManifest } from '../../src/features/capabilities/plugin-manifest'
import { lifecycleChanges, sameLifecycleManifest } from '../../src/api/plugin-lifecycle-diff'
import { installedRow, lifecyclePreview, nextManifest, updateRequest } from '../fixtures/plugin-lifecycle'
import { presentation as metadata } from '../fixtures/plugin-presentation'
function clientWith(value: unknown) {
  const calls: Array<{ path: string; method: string; body: unknown }> = []
  const client = new ZbaClient({ baseUrl: '/zba/v1', fetchImpl: async (input, init) => {
    calls.push({ path: String(input), method: init?.method ?? 'GET', body: init?.body ? JSON.parse(String(init.body)) : null })
    return new Response(JSON.stringify(value), { status: 200, headers: { 'content-type': 'application/json' } })
  } })
  return { client, calls }
}
const valid = () => lifecyclePreview(updateRequest())
describe('strict lifecycle transport', () => {
  it('posts only the readonly lifecycle request and sends preview-bound rollback acknowledgement', async () => {
    const preview = clientWith(valid()), request = updateRequest()
    await preview.client.previewPluginLifecycle(request)
    expect(preview.calls).toEqual([{ path: '/zba/v1/plugins/lifecycle/preview', method: 'POST', body: request }])
    const receipt = clientWith({ revision: 'rev-2', operation: { id: 'key', pluginId: request.pluginId, kind: 'rollback', status: 'restart_required', restartRequired: true } })
    const confirm = { manifest: nextManifest, previewId: 'lifecycle-example', expectedRevision: 'rev-1', idempotencyKey: 'key', trustAcknowledged: true as const }
    await receipt.client.rollbackPlugin(confirm)
    expect(receipt.calls[0]).toEqual({ path: '/zba/v1/plugins/rollback', method: 'POST', body: confirm })
    await receipt.client.uninstallPlugin(request.pluginId, { previewId: confirm.previewId, expectedRevision: 'rev-1', idempotencyKey: 'key', trustAcknowledged: true })
    expect(receipt.calls[1]?.body).toEqual({ previewId: confirm.previewId, expectedRevision: 'rev-1', idempotencyKey: 'key', trustAcknowledged: true })
  })
  it.each([
    ['missing current', { currentManifest: undefined }], ['missing pending', { pendingManifest: undefined }], ['missing recovery', { recovery: undefined }],
    ['recovery array', { recovery: ['none'] }], ['kind array', { kind: ['update'] }],
    ['status array', { checks: [{ code: 'check', status: ['passed'], message: 'synthetic' }] }],
    ['origin array', { targetSource: { origin: ['catalog'], url: null, revision: null } }],
    ['bad recovery', { recovery: 'automatic' }], ['missing nullable target source', { targetSource: undefined }],
    ['missing metadata', { metadata: undefined }], ['rollover date', { expiresAt: '2028-02-30T12:00:00Z' }],
    ['date-only', { expiresAt: '2028-02-29' }], ['newline', { expiresAt: '2028-02-29T12:00:00Z\n' }],
    ['false preservation', { preservation: { settings: true, userData: false, enabledIntent: true } }],
    ['unknown status', { checks: [{ code: 'check', status: 'safe', message: '' }] }],
    ['apply with blocker', { checks: [{ code: 'dependency', status: 'blocked', message: 'missing' }] }],
    ['incomplete diff', { changes: [] }], ['wrong current id', { currentManifest: { ...valid().currentManifest, id: 'other' } }],
    ['pending other id', { pendingManifest: { ...nextManifest, id: 'other' } }],
    ['wrong source', { targetSource: { origin: 'installed', url: null, revision: null } }],
    ['incorrect diff JSON', { changes: [{ field: 'version', before: '1', after: '2' }] }],
    ['invented recovery', { recovery: 'failed-pending' }],
  ])('rejects %s without mutation', async (_name, patch) => {
    const { client, calls } = clientWith({ ...valid(), ...patch })
    await expect(client.previewPluginLifecycle(updateRequest())).rejects.toMatchObject({ code: 'invalid_json' })
    expect(calls).toHaveLength(1)
  })
  it.each(['2028-02-29T12:00:00Z', '2028-02-29T12:00:00+08:00'])('accepts real absolute timestamp %s', expiresAt => {
    expect(isPluginLifecyclePreview({ ...valid(), expiresAt }, isPluginManifest)).toBe(true)
  })
  it('correlates every supplied candidate field, not merely id/version/hash', async () => {
    const value = valid(); value.targetManifest = { ...nextManifest, entryType: 'Other.Plugin' }; value.changes = lifecycleChanges(value.currentManifest, value.targetManifest)
    await expect(clientWith(value).client.previewPluginLifecycle(updateRequest())).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('rejects self-declared external review/official provenance', async () => {
    const request = { ...updateRequest(), kind: 'update' as const, candidate: { origin: 'json' as const, manifest: nextManifest } }
    await expect(clientWith(lifecyclePreview(request, { metadata: metadata({ sourceKind: 'official' }) })).client.previewPluginLifecycle(request)).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('requires sorted complete unique differences and matches semantic JSON, not key order', () => {
    const value = valid()
    expect(isPluginLifecyclePreview({ ...value, changes: [...value.changes].reverse() }, isPluginManifest)).toBe(false)
    expect(isPluginLifecyclePreview({ ...value, changes: [...value.changes, value.changes[0]] }, isPluginManifest)).toBe(false)
    expect(isPluginLifecyclePreview({ ...value, changes: value.changes.map(change => ({ ...change, after: change.after && ' ' + change.after })) }, isPluginManifest)).toBe(true)
  })
  it('accepts blocked missing Previous and explicit failed-candidate recovery, not fake same-target rollback recovery', () => {
    const request = { kind: 'rollback' as const, pluginId: nextManifest.id, expectedRevision: 'rev-1' }
    const blocked = lifecyclePreview(request, { canApply: false, targetManifest: null, targetSource: null })
    expect(isPluginLifecyclePreview(blocked, isPluginManifest)).toBe(true)
    const recovery = lifecyclePreview(request, { recovery: 'failed-pending', pendingManifest: nextManifest, targetManifest: valid().currentManifest })
    expect(isPluginLifecyclePreview(recovery, isPluginManifest)).toBe(true)
    expect(isPluginLifecyclePreview({ ...recovery, kind: 'update' }, isPluginManifest)).toBe(false)
    expect(isPluginLifecyclePreview({ ...recovery, recovery: 'none' }, isPluginManifest)).toBe(false)
  })
  it('compares escaped Unicode and object-property ordering semantically in nested settings diffs', () => {
    const targetManifest = { ...nextManifest, settings: [{ key: 'label', label: '新设置', type: 'string' as const }] }
    const value = lifecyclePreview(updateRequest(), { targetManifest })
    const settings = value.changes.find(change => change.field === 'settings')!
    const nested = JSON.parse(settings.after!)[0]
    settings.after = JSON.stringify([{ choices: nested.choices, required: nested.required, defaultValue: nested.defaultValue,
      type: nested.type, label: nested.label, key: nested.key }]).replace('新', '\\u65b0')
    expect(settings.after).toContain(String.fromCharCode(92) + 'u65b0')
    expect(isPluginLifecyclePreview(value, isPluginManifest)).toBe(true)
    settings.after = settings.after.replace('label', 'other')
    expect(isPluginLifecyclePreview(value, isPluginManifest)).toBe(false)
  })
  it('normalizes lifecycle download URLs just as host fingerprints do', () => {
    const raw = { ...nextManifest, downloadUrl: 'https://EXAMPLE.COM:443/a/../plugin.zip' }
    const canonical = { ...nextManifest, downloadUrl: 'https://example.com/plugin.zip' }
    expect(sameLifecycleManifest(raw, canonical)).toBe(true)
    expect(lifecycleChanges(raw, canonical)).toEqual([])
  })
  it('accepts explicit failed-install cleanup only with the exact pending candidate and absent target', () => {
    const request = { kind: 'uninstall' as const, pluginId: nextManifest.id, expectedRevision: 'rev-1' }
    const value = lifecyclePreview(request, { recovery: 'failed-install', currentManifest: nextManifest, pendingManifest: nextManifest })
    expect(isPluginLifecyclePreview(value, isPluginManifest)).toBe(true)
    expect(isPluginLifecyclePreview({ ...value, canApply: false, checks: [{ code: 'inflight', status: 'blocked', message: 'Still unknown' }] }, isPluginManifest)).toBe(true)
    for (const patch of [{ kind: 'rollback' }, { kind: 'update' }, { pendingManifest: null },
      { currentManifest: valid().currentManifest }, { targetSource: { origin: 'installed', url: null, revision: null } },
      { metadata: metadata() }, { targetManifest: nextManifest }]) {
      expect(isPluginLifecyclePreview({ ...value, ...patch }, isPluginManifest)).toBe(false)
    }
  })
  it('decodes authoritative Previous/Pending projection and rejects malformed artifact identity', async () => {
    await expect(clientWith({ revision: 'rev-1', plugins: [installedRow()] }).client.installedPlugins()).resolves.toMatchObject({ plugins: [installedRow()] })
    await expect(clientWith({ revision: 'rev-1', plugins: [installedRow({ previousManifest: { ...nextManifest, id: 'other' } })] }).client.installedPlugins()).rejects.toMatchObject({ code: 'invalid_json' })
  })
})