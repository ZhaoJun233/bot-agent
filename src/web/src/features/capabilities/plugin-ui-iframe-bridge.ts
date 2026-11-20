import type { ZbaClient, ZbaPluginSettingEdit } from '../../api/zba'
import { ZbaRequestError } from '../../api/zba'
import type { ZbaPluginUiSession } from '../../api/plugin-ui'
import { newNonce } from '../../shared/nonce'
import { beginOperation, markOperation, pendingOperations } from '../../shared/sync/pending-operations'

export interface IframeBridgeHostState {
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active: boolean
  theme: 'light' | 'dark'
  currentGenerationId: string
  onClose: () => void
  onSaved?: () => void
  onAuthExpired?: (status: number, code: string) => void
}

export interface IframeBridgeRequest {
  type: 'zba.ui.request'
  requestId: string
  channelId: string
  pluginId: string
  version: string
  releaseSha256: string
  generationId: string
  action: string
  payload?: Record<string, unknown>
}

const WRITE_ACTIONS = new Set(['quotas.save', 'settings.save'])
const TOKEN_QUOTA_PLUGIN_ID = 'preset.feature.token-quota'
const MIN_QUOTA_LIMIT = 1
const MAX_QUOTA_LIMIT = 1_000_000_000
const REVISION_PATTERN = /^(0|[1-9]\d*)$/

function isValidIdempotencyKey(value: unknown): value is string {
  return typeof value === 'string' && value.length >= 8 && value.length <= 256
    && [...value].every(ch => ch >= '!' && ch <= '~' && ch !== '/' && ch.charCodeAt(0) !== 92)
}

function isQuotaDefiniteRejection(err: unknown): boolean {
  return err instanceof ZbaRequestError && (
    (err.status === 409 && err.code === 'quota_revision_conflict')
    || (err.status === 503 && err.code === 'quota_receipt_capacity')
    || ([400, 401, 403, 404].includes(err.status) && err.code !== 'http_error')
  )
}

function isSettingsDefiniteRejection(err: unknown): boolean {
  return err instanceof ZbaRequestError && (
    err.status === 409
    || ([400, 401, 403, 404].includes(err.status) && err.code !== 'http_error')
  )
}

function parseSettingEdits(value: unknown): ZbaPluginSettingEdit[] | null {
  if (!Array.isArray(value)) return null
  const parsed: ZbaPluginSettingEdit[] = []
  for (const item of value) {
    if (typeof item !== 'object' || item === null || Array.isArray(item)) return null
    const record = item as Record<string, unknown>
    if (typeof record.key !== 'string' || record.key.trim().length === 0) return null
    if (record.kind !== 0 && record.kind !== 1 && record.kind !== 2) return null
    if (record.value !== undefined && typeof record.value !== 'string') return null
    parsed.push({
      key: record.key,
      kind: record.kind,
      ...(record.value !== undefined ? { value: record.value } : {}),
    })
  }
  return parsed
}

/**
 * Sandboxed iframe MessageChannel bridge enforcing A14/A16:
 * - Never exposes Bearer token, parent DOM, or raw ZbaClient.
 * - Strictly binds channelId, pluginId, version, releaseSha256, generationId, and client owner.
 * - Scopes quota actions strictly to preset.feature.token-quota and operations.list to the owning plugin.
 * - Rejects write actions when read-only, writes disabled, backend unready, version/generation mismatched, or auth expired.
 * - Preserves read-only original-key receipt lookup (quotas.find / operations.list) even after generation drift.
 */
export function createIframeBridgeController(
  session: ZbaPluginUiSession,
  getState: () => IframeBridgeHostState,
) {
  const channelId = 'ch-' + newNonce()
  const ownerClient = getState().client
  const allowed = new Set(session.allowedActions)
  let disposed = false

  function isWriteAllowed(state: IframeBridgeHostState): { ok: true } | { ok: false; error: string } {
    if (state.client !== ownerClient) return { ok: false, error: 'plugin_ui_session_owner_changed' }
    if (!state.active) return { ok: false, error: 'plugin_ui_inactive' }
    if (state.mode !== 'admin' || !state.writesEnabled) return { ok: false, error: 'plugin_ui_read_only' }
    if (!session.backendReady) return { ok: false, error: 'plugin_ui_backend_not_ready' }
    if (!session.versionMatched) return { ok: false, error: 'plugin_ui_version_mismatch' }
    if (state.currentGenerationId !== session.generationId) return { ok: false, error: 'plugin_ui_generation_mismatch' }
    return { ok: true }
  }

  async function dispatch(msg: unknown): Promise<{ ok: true; data: unknown } | { ok: false; error: string }> {
    if (disposed || typeof msg !== 'object' || msg === null) return { ok: false, error: 'plugin_ui_invalid_message' }
    const req = msg as Partial<IframeBridgeRequest>
    if (req.type !== 'zba.ui.request' || typeof req.requestId !== 'string' || !req.requestId)
      return { ok: false, error: 'plugin_ui_invalid_message' }
    if (req.channelId !== channelId
      || req.pluginId !== session.pluginId
      || req.version !== session.version
      || req.releaseSha256 !== session.releaseSha256
      || req.generationId !== session.generationId)
      return { ok: false, error: 'plugin_ui_identity_mismatch' }

    const action = typeof req.action === 'string' ? req.action : ''
    if (!allowed.has(action)) return { ok: false, error: 'plugin_ui_action_forbidden' }
    if (action.startsWith('quotas.') && session.pluginId !== TOKEN_QUOTA_PLUGIN_ID)
      return { ok: false, error: 'plugin_ui_action_forbidden' }

    const state = getState()
    if (state.client !== ownerClient) return { ok: false, error: 'plugin_ui_session_owner_changed' }

    if (WRITE_ACTIONS.has(action)) {
      const check = isWriteAllowed(state)
      if (!check.ok) return check
    }

    const payload = (typeof req.payload === 'object' && req.payload !== null ? req.payload : {}) as Record<string, unknown>

    try {
      switch (action) {
        case 'dialog.close':
          state.onClose()
          return { ok: true, data: { closed: true } }

        case 'operations.list': {
          const records = session.pluginId === TOKEN_QUOTA_PLUGIN_ID
            ? pendingOperations(ownerClient, 'quota').filter(r => r.state !== 'settled')
            : pendingOperations(ownerClient, 'plugin-settings').filter(r => r.state !== 'settled' && r.target === session.pluginId)
          return {
            ok: true,
            data: records.map(r => ({
              key: r.key, target: r.target, meta: r.meta, state: r.state,
            })),
          }
        }

        case 'quotas.list':
          return { ok: true, data: await ownerClient.listQuotas() }

        case 'quotas.find': {
          const key = payload.idempotencyKey
          if (!isValidIdempotencyKey(key)) return { ok: false, error: 'invalid_idempotency_key' }
          const found = await ownerClient.findQuota(key)
          if (found.found) markOperation(ownerClient, key, 'settled')
          return { ok: true, data: found }
        }

        case 'quotas.save': {
          const sourceKey = typeof payload.sourceKey === 'string' ? payload.sourceKey.trim() : ''
          const dailyTokenLimit = typeof payload.dailyTokenLimit === 'number' ? payload.dailyTokenLimit : -1
          const expectedRevision = typeof payload.expectedRevision === 'string'
            ? payload.expectedRevision.trim()
            : typeof payload.expectedRevision === 'number' && Number.isSafeInteger(payload.expectedRevision) && payload.expectedRevision >= 0
              ? String(payload.expectedRevision)
              : ''
          if (!sourceKey || sourceKey.length > 256 || /[\u0000-\u001f\u007f]/.test(sourceKey)
            || !Number.isInteger(dailyTokenLimit) || dailyTokenLimit < MIN_QUOTA_LIMIT || dailyTokenLimit > MAX_QUOTA_LIMIT
            || !REVISION_PATTERN.test(expectedRevision))
            return { ok: false, error: 'invalid_quota_payload' }
          let idempotencyKey: string
          if (payload.idempotencyKey !== undefined) {
            if (!isValidIdempotencyKey(payload.idempotencyKey)) return { ok: false, error: 'invalid_idempotency_key' }
            idempotencyKey = payload.idempotencyKey
          } else {
            idempotencyKey = `quota-${newNonce()}`
          }
          beginOperation(ownerClient, { key: idempotencyKey, kind: 'quota', target: sourceKey, meta: { sourceKey, dailyTokenLimit } })
          try {
            const result = await ownerClient.saveQuota({ sourceKey, dailyTokenLimit, expectedRevision, idempotencyKey })
            markOperation(ownerClient, idempotencyKey, 'settled')
            state.onSaved?.()
            return { ok: true, data: result }
          } catch (err) {
            markOperation(ownerClient, idempotencyKey, isQuotaDefiniteRejection(err) ? 'settled' : 'unknown')
            throw err
          }
        }

        case 'settings.read':
          return { ok: true, data: await ownerClient.pluginSettings(session.pluginId) }

        case 'settings.save': {
          const expectedRevision = typeof payload.expectedRevision === 'string' ? payload.expectedRevision.trim() : ''
          const edits = parseSettingEdits(payload.edits)
          if (!expectedRevision || !edits) return { ok: false, error: 'invalid_settings_payload' }
          let idempotencyKey: string
          if (payload.idempotencyKey !== undefined) {
            if (!isValidIdempotencyKey(payload.idempotencyKey)) return { ok: false, error: 'invalid_idempotency_key' }
            idempotencyKey = payload.idempotencyKey
          } else {
            idempotencyKey = `settings-${newNonce()}`
          }
          beginOperation(ownerClient, { key: idempotencyKey, kind: 'plugin-settings', target: session.pluginId })
          try {
            const saved = await ownerClient.savePluginSettings(session.pluginId, {
              expectedRevision,
              idempotencyKey,
              edits,
            })
            markOperation(ownerClient, idempotencyKey, 'settled')
            state.onSaved?.()
            return { ok: true, data: saved }
          } catch (err) {
            markOperation(ownerClient, idempotencyKey, isSettingsDefiniteRejection(err) ? 'settled' : 'unknown')
            throw err
          }
        }

        default:
          return { ok: false, error: 'plugin_ui_action_unsupported' }
      }
    } catch (err) {
      if (err instanceof ZbaRequestError) {
        if (err.status === 401) {
          state.onAuthExpired?.(err.status, err.code)
        }
        return { ok: false, error: err.code || (err.status === 401 ? 'unauthorized' : 'plugin_ui_request_failed') }
      }
      const message = err instanceof Error ? err.message : 'plugin_ui_request_failed'
      return { ok: false, error: message }
    }
  }

  function initPayload() {
    const state = getState()
    return {
      type: 'zba.ui.init',
      channelId,
      pluginId: session.pluginId,
      version: session.version,
      releaseSha256: session.releaseSha256,
      generationId: session.generationId,
      contractVersion: session.contractVersion,
      mode: state.mode,
      writesEnabled: state.writesEnabled && state.currentGenerationId === session.generationId,
      backendReady: session.backendReady,
      versionMatched: session.versionMatched && state.currentGenerationId === session.generationId,
      adoptionMode: session.adoptionMode,
      allowedActions: [...session.allowedActions],
      theme: state.theme,
    }
  }

  return {
    channelId,
    dispatch,
    initPayload,
    dispose() { disposed = true },
  }
}
