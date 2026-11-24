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

export type BridgeActionHandler = (
  payload: Record<string, unknown>,
  state: IframeBridgeHostState
) => Promise<{ ok: true; data: unknown } | { ok: false; error: string }>

const TOKEN_QUOTA_PLUGIN_ID = 'preset.feature.token-quota'
const SCHEDULER_PLUGIN_ID = 'preset.feature.scheduler'
const PERSONAL_TODO_PLUGIN_ID = 'preset.feature.personal-todo'

const WRITE_ACTIONS = new Set([
  'quotas.save',
  'settings.save',
  'scheduler.save',
  'scheduler.delete',
  'todo.save',
  'todo.delete',
])

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

const MAX_BRIDGE_PAYLOAD_BYTES = 64 * 1024
const CONTROL_CHAR_PATTERN = /[\u0000-\u001f\u007f]/
const TOKEN_ID_PATTERN = /^[A-Za-z0-9._-]{3,64}$/

function isSafeString(value: unknown, maxLen: number, allowNewlines = false): value is string {
  if (typeof value !== 'string' || value.length > maxLen) return false
  if (!allowNewlines) return !CONTROL_CHAR_PATTERN.test(value)
  return ![...value].some(ch => ch.charCodeAt(0) < 0x20 && ch !== '\n' && ch !== '\r' && ch !== '\t')
}

function isActionPermittedForPlugin(pluginId: string, action: string): boolean {
  if (action === 'dialog.close' || action === 'operations.list' || action === 'settings.read' || action === 'settings.save') {
    return true
  }
  if (action === 'quotas.list' || action === 'quotas.save' || action === 'quotas.find') {
    return pluginId === TOKEN_QUOTA_PLUGIN_ID
  }
  if (action === 'scheduler.list' || action === 'scheduler.save' || action === 'scheduler.delete' || action === 'scheduler.runs') {
    return pluginId === SCHEDULER_PLUGIN_ID
  }
  if (action === 'todo.list' || action === 'todo.save' || action === 'todo.delete') {
    return pluginId === PERSONAL_TODO_PLUGIN_ID
  }
  return false
}

/**
 * Sandboxed iframe MessageChannel bridge with declarative action registry:
 * - Never exposes Bearer token, parent DOM, or raw ZbaClient.
 * - Strictly binds channelId, pluginId, version, releaseSha256, generationId, and client owner.
 * - Enforces per-plugin action prefix ownership (`quotas.*`, `scheduler.*`, `todo.*`).
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

  const handlers = new Map<string, BridgeActionHandler>([
    ['dialog.close', async (_payload, state) => {
      state.onClose()
      return { ok: true, data: { closed: true } }
    }],
    ['operations.list', async () => {
      const records = session.pluginId === TOKEN_QUOTA_PLUGIN_ID
        ? pendingOperations(ownerClient, 'quota').filter(r => r.state !== 'settled')
        : pendingOperations(ownerClient, 'plugin-settings').filter(r => r.state !== 'settled' && r.target === session.pluginId)
      return {
        ok: true,
        data: records.map(r => ({ key: r.key, target: r.target, meta: r.meta, state: r.state })),
      }
    }],
    ['quotas.list', async () => ({ ok: true, data: await ownerClient.listQuotas() })],
    ['quotas.find', async (payload) => {
      const key = payload.idempotencyKey
      if (!isValidIdempotencyKey(key)) return { ok: false, error: 'invalid_idempotency_key' }
      const found = await ownerClient.findQuota(key)
      if (found.found) markOperation(ownerClient, key, 'settled')
      return { ok: true, data: found }
    }],
    ['quotas.save', async (payload, state) => {
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
    }],
    ['settings.read', async () => ({ ok: true, data: await ownerClient.pluginSettings(session.pluginId) })],
    ['settings.save', async (payload, state) => {
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
        const saved = await ownerClient.savePluginSettings(session.pluginId, { expectedRevision, idempotencyKey, edits })
        markOperation(ownerClient, idempotencyKey, 'settled')
        state.onSaved?.()
        return { ok: true, data: saved }
      } catch (err) {
        markOperation(ownerClient, idempotencyKey, isSettingsDefiniteRejection(err) ? 'settled' : 'unknown')
        throw err
      }
    }],
    ['scheduler.list', async () => ({ ok: true, data: await ownerClient.listScheduledJobs() })],
    ['scheduler.runs', async (payload) => {
      if (payload.jobId !== undefined && payload.jobId !== null && !isSafeString(payload.jobId, 64))
        return { ok: false, error: 'invalid_scheduler_payload' }
      const rawLimit = payload.limit
      if (rawLimit !== undefined && (typeof rawLimit !== 'number' || !Number.isInteger(rawLimit) || rawLimit < 1 || rawLimit > 200))
        return { ok: false, error: 'invalid_scheduler_payload' }
      return {
        ok: true,
        data: await ownerClient.listScheduledRuns({
          jobId: typeof payload.jobId === 'string' ? payload.jobId.trim() : null,
          limit: rawLimit ?? 50,
        }),
      }
    }],
    ['scheduler.save', async (payload, state) => {
      const expectedRevision = typeof payload.expectedRevision === 'number' && Number.isSafeInteger(payload.expectedRevision)
        ? payload.expectedRevision : -1
      const scopeKey = typeof payload.scopeKey === 'string' ? payload.scopeKey.trim() : ''
      const kind = typeof payload.kind === 'string' ? payload.kind.trim() : ''
      const text = typeof payload.text === 'string' ? payload.text.trim() : ''
      if (payload.idempotencyKey !== undefined && !isValidIdempotencyKey(payload.idempotencyKey))
        return { ok: false, error: 'invalid_idempotency_key' }
      const key = typeof payload.idempotencyKey === 'string' ? payload.idempotencyKey : `sched-${newNonce()}`
      if (expectedRevision < 0 || !scopeKey || scopeKey.length > 256 || CONTROL_CHAR_PATTERN.test(scopeKey)
        || !kind || kind.length > 32 || CONTROL_CHAR_PATTERN.test(kind)
        || !text || !isSafeString(text, 2000, true)
        || (payload.jobId !== undefined && payload.jobId !== null && (typeof payload.jobId !== 'string' || !TOKEN_ID_PATTERN.test(payload.jobId.trim())))
        || (payload.cronExpr !== undefined && payload.cronExpr !== null && !isSafeString(payload.cronExpr, 256))
        || (payload.timezoneIana !== undefined && payload.timezoneIana !== null && !isSafeString(payload.timezoneIana, 80)))
        return { ok: false, error: 'invalid_scheduler_payload' }
      const saved = await ownerClient.saveScheduledJob({
        expectedRevision,
        idempotencyKey: key,
        jobId: typeof payload.jobId === 'string' ? payload.jobId.trim() : null,
        scopeKey,
        kind,
        cronExpr: typeof payload.cronExpr === 'string' ? payload.cronExpr.trim() : null,
        timezoneIana: typeof payload.timezoneIana === 'string' ? payload.timezoneIana.trim() : 'Asia/Shanghai',
        text,
        missedPolicy: typeof payload.missedPolicy === 'string' ? payload.missedPolicy.trim() : 'skip_and_notify',
        enabled: typeof payload.enabled === 'boolean' ? payload.enabled : true,
        nextFireUnix: typeof payload.nextFireUnix === 'number' && Number.isSafeInteger(payload.nextFireUnix) ? payload.nextFireUnix : 0,
      })
      state.onSaved?.()
      return { ok: true, data: saved }
    }],
    ['scheduler.delete', async (payload, state) => {
      const expectedRevision = typeof payload.expectedRevision === 'number' && Number.isSafeInteger(payload.expectedRevision)
        ? payload.expectedRevision : -1
      const jobId = typeof payload.jobId === 'string' ? payload.jobId.trim() : ''
      if (payload.idempotencyKey !== undefined && !isValidIdempotencyKey(payload.idempotencyKey))
        return { ok: false, error: 'invalid_idempotency_key' }
      const key = typeof payload.idempotencyKey === 'string' ? payload.idempotencyKey : `sched-del-${newNonce()}`
      if (expectedRevision < 0 || !TOKEN_ID_PATTERN.test(jobId)) return { ok: false, error: 'invalid_scheduler_payload' }
      const res = await ownerClient.deleteScheduledJob({ expectedRevision, idempotencyKey: key, jobId })
      state.onSaved?.()
      return { ok: true, data: res }
    }],
    ['todo.list', async (_payload, state) => {
      if (state.mode !== 'admin') return { ok: false, error: 'forbidden' }
      return { ok: true, data: await ownerClient.listPersonalTodos() }
    }],
    ['todo.save', async (payload, state) => {
      const expectedRevision = typeof payload.expectedRevision === 'number' && Number.isSafeInteger(payload.expectedRevision)
        ? payload.expectedRevision : -1
      const title = typeof payload.title === 'string' ? payload.title.trim() : ''
      if (payload.idempotencyKey !== undefined && !isValidIdempotencyKey(payload.idempotencyKey))
        return { ok: false, error: 'invalid_idempotency_key' }
      const key = typeof payload.idempotencyKey === 'string' ? payload.idempotencyKey : `todo-${newNonce()}`
      const priority = payload.priority === undefined ? 2 : payload.priority
      const status = payload.status === undefined ? 'open' : payload.status
      if (expectedRevision < 0 || !title || title.length > 200 || CONTROL_CHAR_PATTERN.test(title)
        || typeof priority !== 'number' || !Number.isInteger(priority) || priority < 1 || priority > 3
        || (status !== 'open' && status !== 'done' && status !== 'archived')
        || (payload.todoId !== undefined && payload.todoId !== null && (typeof payload.todoId !== 'string' || !TOKEN_ID_PATTERN.test(payload.todoId.trim())))
        || (payload.notes !== undefined && payload.notes !== null && !isSafeString(payload.notes, 1000, true))
        || (payload.sourceHint !== undefined && payload.sourceHint !== null && !isSafeString(payload.sourceHint, 120))
        || (payload.timezoneIana !== undefined && payload.timezoneIana !== null && !isSafeString(payload.timezoneIana, 80)))
        return { ok: false, error: 'invalid_todo_payload' }
      const saved = await ownerClient.savePersonalTodo({
        expectedRevision,
        idempotencyKey: key,
        todoId: typeof payload.todoId === 'string' ? payload.todoId.trim() : null,
        title,
        notes: typeof payload.notes === 'string' ? payload.notes.trim() : null,
        priority,
        status,
        dueUnix: typeof payload.dueUnix === 'number' && Number.isSafeInteger(payload.dueUnix) ? payload.dueUnix : null,
        sourceHint: typeof payload.sourceHint === 'string' ? payload.sourceHint.trim() : null,
        timezoneIana: typeof payload.timezoneIana === 'string' ? payload.timezoneIana.trim() : 'Asia/Shanghai',
      })
      state.onSaved?.()
      return { ok: true, data: saved }
    }],
    ['todo.delete', async (payload, state) => {
      const expectedRevision = typeof payload.expectedRevision === 'number' && Number.isSafeInteger(payload.expectedRevision)
        ? payload.expectedRevision : -1
      const todoId = typeof payload.todoId === 'string' ? payload.todoId.trim() : ''
      if (payload.idempotencyKey !== undefined && !isValidIdempotencyKey(payload.idempotencyKey))
        return { ok: false, error: 'invalid_idempotency_key' }
      const key = typeof payload.idempotencyKey === 'string' ? payload.idempotencyKey : `todo-del-${newNonce()}`
      if (expectedRevision < 0 || !TOKEN_ID_PATTERN.test(todoId)) return { ok: false, error: 'invalid_todo_payload' }
      const res = await ownerClient.deletePersonalTodo({ expectedRevision, idempotencyKey: key, todoId })
      state.onSaved?.()
      return { ok: true, data: res }
    }],
  ])

  async function dispatch(msg: unknown): Promise<{ ok: true; data: unknown } | { ok: false; error: string }> {
    if (disposed || typeof msg !== 'object' || msg === null) return { ok: false, error: 'plugin_ui_invalid_message' }
    let serializedSize = 0
    try {
      serializedSize = JSON.stringify(msg).length
    } catch {
      return { ok: false, error: 'plugin_ui_invalid_message' }
    }
    if (serializedSize > MAX_BRIDGE_PAYLOAD_BYTES) return { ok: false, error: 'plugin_ui_payload_too_large' }

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
    if (!allowed.has(action) || !isActionPermittedForPlugin(session.pluginId, action))
      return { ok: false, error: 'plugin_ui_action_forbidden' }

    const state = getState()
    if (state.client !== ownerClient) return { ok: false, error: 'plugin_ui_session_owner_changed' }
    if (!state.active && action !== 'dialog.close') return { ok: false, error: 'plugin_ui_inactive' }

    if (WRITE_ACTIONS.has(action)) {
      const check = isWriteAllowed(state)
      if (!check.ok) return check
    }

    const handler = handlers.get(action)
    if (!handler) return { ok: false, error: 'plugin_ui_action_unsupported' }
    const payload = (typeof req.payload === 'object' && req.payload !== null ? req.payload : {}) as Record<string, unknown>

    try {
      return await handler(payload, state)
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
