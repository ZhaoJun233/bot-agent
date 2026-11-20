import { describe, expect, it, vi } from 'vitest'
import { canonicalManifest, samePreviewManifest } from '../../src/api/plugin-manifest-values'
import { ZbaRequestError, type ZbaClient, type ZbaPluginPackageManifest } from '../../src/api/zba'
import { isPluginUiSession, isSafePluginUiEntryUrl, type ZbaPluginUiSession } from '../../src/api/plugin-ui'
import { createIframeBridgeController } from '../../src/features/capabilities/plugin-ui-iframe-bridge'
import { pendingOperations } from '../../src/shared/sync/pending-operations'

const SHA_A = 'a'.repeat(64)
const SHA_B = 'b'.repeat(64)
const SHA_C = 'c'.repeat(64)

function baseManifest(): ZbaPluginPackageManifest {
  return {
    id: 'preset.feature.token-quota',
    version: '1.0.0',
    entryAssembly: 'Ui.dll',
    entryType: 'UiPlugin',
    sha256: SHA_A,
    downloadUrl: 'https://github.com/example-org/example-plugins/releases/download/ui-1.0.0/ui.zip',
    apiVersion: 1,
    targetFramework: 'net10.0',
    permissions: [],
    dependencies: [],
    settings: [],
    adoption: 'cold',
    exclusiveResources: ['platform.connection'],
    ui: {
      contractVersion: 1,
      entry: 'web/index.html',
      kind: 'dialog',
      title: 'UI v1',
      allowedActions: ['quotas.list', 'quotas.save', 'quotas.find', 'operations.list'],
      assets: [
        { path: 'web/index.html', sha256: SHA_B.toUpperCase(), contentType: 'text/html; charset=utf-8' },
        { path: 'web/app.js', sha256: SHA_C, contentType: 'text/javascript; charset=utf-8' },
      ],
    },
  }
}

function makeSession(overrides: Partial<ZbaPluginUiSession> = {}): ZbaPluginUiSession {
  return {
    sessionId: 'uis-1',
    pluginId: 'preset.feature.token-quota',
    version: '1.0.0',
    releaseSha256: SHA_A,
    generationId: 'gen-1',
    contractVersion: 1,
    entryUrl: `/zba/v1/plugins/preset.feature.token-quota/ui/${SHA_A}/t1/web/index.html`,
    allowedActions: ['quotas.list', 'quotas.save', 'quotas.find', 'operations.list', 'dialog.close'],
    backendReady: true,
    versionMatched: true,
    adoptionMode: 'cold',
    kind: 'dialog',
    title: 'Token 配额',
    expiresAt: '2026-11-20T05:00:00Z',
    ...overrides,
  }
}

describe('D2.1 manifest UI canonicalization and iframe bridge security (A14/A16/A36)', () => {
  it('normalizes asset/action order and detects any single UI or adoption field mutation (A36)', () => {
    const left = baseManifest()
    const reordered = baseManifest()
    reordered.ui!.assets = [...reordered.ui!.assets].reverse()
    reordered.ui!.allowedActions = [...reordered.ui!.allowedActions!].reverse()
    expect(samePreviewManifest(left, reordered)).toBe(true)

    const diffHash = baseManifest()
    diffHash.ui!.assets[0]!.sha256 = SHA_A
    expect(samePreviewManifest(left, diffHash)).toBe(false)

    const diffAction = baseManifest()
    diffAction.ui!.allowedActions = ['quotas.list']
    expect(samePreviewManifest(left, diffAction)).toBe(false)

    const diffAdoption = baseManifest()
    diffAdoption.adoption = 'online'
    expect(samePreviewManifest(left, diffAdoption)).toBe(false)

    const diffExclusive = baseManifest()
    diffExclusive.exclusiveResources = ['listener.port']
    expect(samePreviewManifest(left, diffExclusive)).toBe(false)

    expect(canonicalManifest(left).ui?.assets[0]?.path).toBe('web/app.js')
    // Canonical UI emits explicit null fields to match backend PluginLifecycleManifest serialization
    const minimal = baseManifest()
    minimal.ui!.allowedActions = null
    minimal.ui!.kind = null
    minimal.ui!.title = null
    expect(canonicalManifest(minimal).ui).toEqual({
      contractVersion: 1,
      entry: 'web/index.html',
      assets: [
        { path: 'web/app.js', sha256: SHA_C, contentType: 'text/javascript; charset=utf-8' },
        { path: 'web/index.html', sha256: SHA_B, contentType: 'text/html; charset=utf-8' },
      ],
      allowedActions: null,
      kind: null,
      title: null,
    })
  })

  it('rejects untrusted or traversal entryUrl in isPluginUiSession / isSafePluginUiEntryUrl', () => {
    expect(isPluginUiSession(makeSession())).toBe(true)
    const signedTicket = `uis-${'1'.repeat(32)}.1763614800.${'f'.repeat(64)}`
    expect(isPluginUiSession(makeSession({
      entryUrl: `/zba/v1/plugins/preset.feature.token-quota/ui/${SHA_A}/${signedTicket}/web/index.html`,
    }))).toBe(true)
    expect(isSafePluginUiEntryUrl(`/zba/v1/plugins/preset.feature.token-quota/ui/${SHA_A}/../web/index.html`, 'preset.feature.token-quota', SHA_A)).toBe(false)
    expect(isSafePluginUiEntryUrl(`/zba/v1/plugins/preset.feature.token-quota/ui/${SHA_A}/a..b/web/index.html`, 'preset.feature.token-quota', SHA_A)).toBe(false)
    expect(isSafePluginUiEntryUrl('https://evil.example/web/index.html', 'preset.feature.token-quota', SHA_A)).toBe(false)
    expect(isSafePluginUiEntryUrl('javascript:alert(1)', 'preset.feature.token-quota', SHA_A)).toBe(false)
    expect(isSafePluginUiEntryUrl(`//evil.example/zba/v1/plugins/preset.feature.token-quota/ui/${SHA_A}/t1/web/index.html`, 'preset.feature.token-quota', SHA_A)).toBe(false)
    expect(isSafePluginUiEntryUrl(`/zba/v1/plugins/preset.feature.token-quota/ui/${SHA_A}/t1/web/../secret.html`, 'preset.feature.token-quota', SHA_A)).toBe(false)
    expect(isPluginUiSession(makeSession({ entryUrl: 'https://evil.example/index.html' }))).toBe(false)
  })

  it('rejects forged identity, disallowed actions, cross-plugin quota access, read-only writes, and stale generation writes while preserving original-key recovery (A14/A16)', async () => {
    const sampleQuota = {
      sourceKey: 'group:10001',
      displayName: '测试群',
      revision: '0',
      dailyTokenLimit: 100,
      usedPromptTokens: 10,
      usedCompletionTokens: 5,
      usedTokens: 15,
      remainingTokens: 85,
      resetDate: '2026-11-20',
      nextResetAt: '2026-11-21T00:00:00Z',
      energySaving: false,
      minimumDailyTokenLimit: 1,
      maximumDailyTokenLimit: 1_000_000_000,
    }
    const saveQuota = vi.fn().mockResolvedValue({ quota: { ...sampleQuota, revision: '1' } })
    const findQuota = vi.fn().mockResolvedValue({ found: true, quota: { ...sampleQuota, revision: '1' } })
    const listQuotas = vi.fn().mockResolvedValue({ items: [sampleQuota] })
    const onAuthExpired = vi.fn()
    const client = { saveQuota, findQuota, listQuotas } as unknown as ZbaClient

    let mode: 'read' | 'admin' = 'read'
    let writesEnabled = true
    let currentGenerationId = 'gen-1'
    const session = makeSession()
    const bridge = createIframeBridgeController(session, () => ({
      client,
      mode,
      writesEnabled,
      active: true,
      theme: 'light',
      currentGenerationId,
      onClose: vi.fn(),
      onAuthExpired,
    }))

    const validReq = (action: string, payload?: Record<string, unknown>) => ({
      type: 'zba.ui.request',
      requestId: 'r1',
      channelId: bridge.channelId,
      pluginId: session.pluginId,
      version: session.version,
      releaseSha256: session.releaseSha256,
      generationId: session.generationId,
      action,
      payload,
    })

    // Forged channel rejected
    const forged = await bridge.dispatch({ ...validReq('quotas.list'), channelId: 'ch-wrong' })
    expect(forged).toEqual({ ok: false, error: 'plugin_ui_identity_mismatch' })

    // Disallowed action rejected
    const forbidden = await bridge.dispatch(validReq('settings.save', { expectedRevision: '1', edits: [] }))
    expect(forbidden).toEqual({ ok: false, error: 'plugin_ui_action_forbidden' })

    // Cross-plugin quota action rejected even if session.allowedActions were tampered
    const otherSession = makeSession({
      pluginId: 'community.other.ui',
      entryUrl: `/zba/v1/plugins/community.other.ui/ui/${SHA_A}/t2/web/index.html`,
      allowedActions: ['quotas.list', 'quotas.save'],
    })
    const otherBridge = createIframeBridgeController(otherSession, () => ({
      client,
      mode: 'admin',
      writesEnabled: true,
      active: true,
      theme: 'light',
      currentGenerationId: 'gen-1',
      onClose: vi.fn(),
    }))
    const crossQuota = await otherBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r-x',
      channelId: otherBridge.channelId,
      pluginId: otherSession.pluginId,
      version: otherSession.version,
      releaseSha256: otherSession.releaseSha256,
      generationId: otherSession.generationId,
      action: 'quotas.list',
    })
    expect(crossQuota).toEqual({ ok: false, error: 'plugin_ui_action_forbidden' })

    // Read-only mode: read succeeds, write fails with zero backend calls
    expect((await bridge.dispatch(validReq('quotas.list'))).ok).toBe(true)
    const roWrite = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 100, expectedRevision: '0' }))
    expect(roWrite).toEqual({ ok: false, error: 'plugin_ui_read_only' })
    expect(saveQuota).not.toHaveBeenCalled()

    // Admin mode: limit=0 rejected, valid limit with string expectedRevision succeeds
    mode = 'admin'
    const zeroLimit = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 0, expectedRevision: '0' }))
    expect(zeroLimit).toEqual({ ok: false, error: 'invalid_quota_payload' })

    const okWrite = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 100, expectedRevision: '0' }))
    expect(okWrite.ok).toBe(true)
    expect(saveQuota).toHaveBeenCalledTimes(1)
    expect(saveQuota.mock.calls[0]![0].expectedRevision).toBe('0')
    // Settled operations must not linger in operations.list
    expect(await bridge.dispatch(validReq('operations.list'))).toEqual({ ok: true, data: [] })

    // Definite 409 conflict settles operation (never leaves stuck unknown pending op) and 403 does NOT trigger auth-expired
    saveQuota.mockRejectedValueOnce(new ZbaRequestError(409, 'quota_revision_conflict', 'conflict'))
    const conflict = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 120, expectedRevision: '0' }))
    expect(conflict).toEqual({ ok: false, error: 'quota_revision_conflict' })
    expect(pendingOperations(client, 'quota').filter(r => r.state !== 'settled')).toHaveLength(0)

    saveQuota.mockRejectedValueOnce(new ZbaRequestError(403, 'forbidden', 'forbidden'))
    const denied = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 120, expectedRevision: '1' }))
    expect(denied).toEqual({ ok: false, error: 'forbidden' })
    expect(onAuthExpired).not.toHaveBeenCalled()
    expect(pendingOperations(client, 'quota').filter(r => r.state !== 'settled')).toHaveLength(0)

    // 401 DOES emit auth-expired
    saveQuota.mockRejectedValueOnce(new ZbaRequestError(401, 'unauthorized', 'expired'))
    const expired = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 120, expectedRevision: '1' }))
    expect(expired).toEqual({ ok: false, error: 'unauthorized' })
    expect(onAuthExpired).toHaveBeenCalledTimes(1)

    // Ambiguous network error leaves operation in 'unknown' state for original-key recovery
    saveQuota.mockRejectedValueOnce(new Error('network_timeout'))
    const ambiguous = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 150, expectedRevision: '1' }))
    expect(ambiguous).toEqual({ ok: false, error: 'network_timeout' })
    const opsAfterTimeout = await bridge.dispatch(validReq('operations.list'))
    expect(opsAfterTimeout.ok && Array.isArray(opsAfterTimeout.data) && opsAfterTimeout.data.length === 1).toBe(true)

    // Generation drift after upgrade: write rejected, but read-only original-key recovery still succeeds!
    currentGenerationId = 'gen-2'
    const staleWrite = await bridge.dispatch(validReq('quotas.save', { sourceKey: 'group:10001', dailyTokenLimit: 200, expectedRevision: '1' }))
    expect(staleWrite).toEqual({ ok: false, error: 'plugin_ui_generation_mismatch' })

    const origKey = (opsAfterTimeout.ok && Array.isArray(opsAfterTimeout.data) ? (opsAfterTimeout.data[0] as { key: string }).key : 'quota-orig-key-1')
    const recovery = await bridge.dispatch(validReq('quotas.find', { idempotencyKey: origKey }))
    expect(recovery.ok).toBe(true)
    expect(findQuota).toHaveBeenCalledWith(origKey)
    expect(await bridge.dispatch(validReq('operations.list'))).toEqual({ ok: true, data: [] })
  })
})
