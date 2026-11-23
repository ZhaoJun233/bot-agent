export interface ZbaWorkspaceMessageView {
  sequence: number
  role: string
  text: string
  mode: string
  attachmentName?: string | null
  createdUnix: number
  taskId?: string | null
  status?: string | null
}

export interface ZbaWorkspaceMessagesResponse {
  scopeKey: string
  lastSequence: number
  messages: ZbaWorkspaceMessageView[]
}

export interface ZbaWorkspaceSendRequest {
  idempotencyKey: string
  text: string
  mode: 'chat' | 'task' | string
  attachmentName?: string | null
  attachmentBase64?: string | null
}

export interface ZbaWorkspaceSendResult {
  ok: boolean
  replayed: boolean
  operationKey: string
  reasonCode?: string | null
  taskId?: string | null
  snapshot: ZbaWorkspaceMessagesResponse
}

export interface ZbaWorkspaceClearResult {
  ok: boolean
  snapshot: ZbaWorkspaceMessagesResponse
}

export interface ZbaWorkspaceIdentityLinkView {
  canonicalPersonId: string
  platformId: string
  accountScope: string
  instanceId: string
  maskedUserId: string
  linkedUnix: number
}

export interface ZbaWorkspaceIdentityResponse {
  enabled: boolean
  shareTodos: boolean
  shareKnowledge: boolean
  sharePreferences: boolean
  links: ZbaWorkspaceIdentityLinkView[]
  workspaceCanonicalPersonId?: string | null
}

export interface ZbaWorkspaceIdentityToggleRequest {
  enabled: boolean
  shareTodos: boolean
  shareKnowledge: boolean
  sharePreferences: boolean
  workspaceCanonicalPersonId?: string | null
}

function isObj(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isInt(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value)
}

function isOptNullStr(value: unknown): value is string | null | undefined {
  return value === undefined || value === null || typeof value === 'string'
}

export function isWorkspaceMessageView(value: unknown): value is ZbaWorkspaceMessageView {
  return isObj(value)
    && isInt(value.sequence)
    && typeof value.role === 'string'
    && typeof value.text === 'string'
    && typeof value.mode === 'string'
    && isOptNullStr(value.attachmentName)
    && isInt(value.createdUnix)
    && isOptNullStr(value.taskId)
    && isOptNullStr(value.status)
}

export function isWorkspaceMessagesResponse(value: unknown): value is ZbaWorkspaceMessagesResponse {
  return isObj(value)
    && typeof value.scopeKey === 'string'
    && isInt(value.lastSequence)
    && Array.isArray(value.messages)
    && value.messages.every(isWorkspaceMessageView)
}

export function isWorkspaceSendResult(value: unknown): value is ZbaWorkspaceSendResult {
  return isObj(value)
    && typeof value.ok === 'boolean'
    && typeof value.replayed === 'boolean'
    && typeof value.operationKey === 'string'
    && isOptNullStr(value.reasonCode)
    && isOptNullStr(value.taskId)
    && isWorkspaceMessagesResponse(value.snapshot)
}

export function isWorkspaceClearResult(value: unknown): value is ZbaWorkspaceClearResult {
  return isObj(value)
    && typeof value.ok === 'boolean'
    && isWorkspaceMessagesResponse(value.snapshot)
}

export function isWorkspaceIdentityLinkView(value: unknown): value is ZbaWorkspaceIdentityLinkView {
  return isObj(value)
    && typeof value.canonicalPersonId === 'string'
    && typeof value.platformId === 'string'
    && typeof value.accountScope === 'string'
    && typeof value.instanceId === 'string'
    && typeof value.maskedUserId === 'string'
    && isInt(value.linkedUnix)
}

export function isWorkspaceIdentityResponse(value: unknown): value is ZbaWorkspaceIdentityResponse {
  return isObj(value)
    && typeof value.enabled === 'boolean'
    && typeof value.shareTodos === 'boolean'
    && typeof value.shareKnowledge === 'boolean'
    && typeof value.sharePreferences === 'boolean'
    && Array.isArray(value.links)
    && value.links.every(isWorkspaceIdentityLinkView)
    && isOptNullStr(value.workspaceCanonicalPersonId)
}
