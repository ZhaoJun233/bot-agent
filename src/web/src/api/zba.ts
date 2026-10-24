export interface ZbaHealth {
  status: string
  ready: boolean
  configured: boolean
  contractVersion: string
  checkedAt: string
}

export interface ZbaComponent {
  id: string
  kind: string
  displayName: string
  state: string
  available: boolean
  reasonCode?: string
  updatedAt?: string
}

export interface ZbaErrorSummary {
  at: string
  scope: string
  code: string
}

export interface ZbaStatus {
  state: string
  ready: boolean
  version: string
  startedAt: string
  updatedAt: string
  configurationVersion?: string
  configurationApplyState: string
  components: ZbaComponent[]
  recentErrors: ZbaErrorSummary[]
}

export interface ZbaMeta {
  contractVersion: string
  serverVersion: string
  authentication: string
  scopes: string[]
  sse: boolean
  mcpManagement: boolean
  dockerManagement: boolean
  maxBodyBytes: number
  instanceId: string
  features: string[]
}

// 以下为 S2 写操作前的 wire 类型；枚举在 zba-1.0 中按数字传输。
export type ZbaTaskState = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7

export interface ZbaTaskPage {
  items: ZbaTaskSummary[]
  /**
   * 末页没有下一个游标。
   *
   * ZbaJson 用 JsonIgnoreCondition.WhenWritingNull 序列化：null 会被**整段省略**，
   * 所以真实末页是「没有这个字段」，不是 `nextCursor: null`。强制要求它存在
   * 会把合法末页判成 invalid_json（S3-02）。
   */
  nextCursor?: string | null
}

/** 事件环里的一条事件：只带标识/状态/错误码，不带提示词、工具载荷或聊天正文。 */
export interface ZbaEvent {
  sequence: number
  type: string
  at: string
  taskId?: string | null
  state?: string | null
  version?: string | null
  reasonCode?: string | null
}

/**
 * 事件分页。
 *
 * `nextSequence` 是服务端已确认的游标锚点；`resyncRequired` 表示中间有缺口
 * （环淘汰、序号落盘失败、进程重启），必须先整份重读状态，再从记录的锚点补读。
 */
export interface ZbaEventPage {
  items: ZbaEvent[]
  nextSequence: number
  resyncRequired: boolean
  generatedAt: string
}

export interface ZbaSessionSummary {
  id: string
  name: string
  backend: string
  current: boolean
  turns: number
  historyChars: number
  createdAt: string
  updatedAt: string
}

export interface ZbaSessionPage {
  items: ZbaSessionSummary[]
  /** 与任务列表同源：末页整段省略该字段。 */
  nextCursor?: string | null
}

export interface ZbaSessionEnsureResult {
  session: ZbaSessionSummary
  created: boolean
}

export interface ZbaTaskListQuery {
  limit?: number
  cursor?: string
}

export interface ZbaTaskAccepted {
  id: string
  state: ZbaTaskState
  acceptedAt: string
}

export interface ZbaTaskSubmission {
  sourceKey: string
  sessionId: string
  prompt: string
  target: 'server'
  timeoutSeconds: number
}

const TASK_FIND_CODES: ZbaTaskFindCode[] = ['found', 'expired', 'accepted_unknown', 'unknown']

export interface ZbaTaskSummary {
  id: string
  state: ZbaTaskState
  target: string
  sessionId: string
  createdAt: string
  updatedAt: string
  durationMs?: number
  toolCalls?: number
  errorCode?: string
  cancellationRequested: boolean
}

export interface ZbaTaskResult {
  id: string
  state: ZbaTaskState
  updatedAt: string
  available: boolean
  reasonCode?: string
  summary?: string
  summaryTruncated: boolean
  summaryChars: number
  toolCalls?: number
  durationMs?: number
  errorCode?: string
}

export type ZbaTaskFindCode = 'found' | 'expired' | 'accepted_unknown' | 'unknown'

export interface ZbaTaskFindResult {
  code: ZbaTaskFindCode
  instanceId: string
  task?: ZbaTaskSummary
  taskId?: string
  acceptedAt?: string
}

export interface ZbaApprovalSummary {
  id: string
  toolId: string
  summary: string
  session: string
  expiresAt: string
  policyVersion: number
}

export interface ZbaApprovalDecisionResult {
  ok: boolean
  decision: string
  reasonCode: string
}

export interface ZbaTaskCancelResult {
  state: ZbaTaskState
  cancellationRequested: boolean
  outcome: string
}


// ── S2-3 能力管理（manage 契约，枚举按数字传输） ──────────────────────────

export type ZbaManagedKind = 0 | 1 | 2 | 3 | 4
export type ZbaManagedAction = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8
export type ZbaManagedRuntimeState = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7
export type ZbaManagedOperationState = 0 | 1 | 2 | 3 | 4 | 5 | 6
export type ZbaManagedResultCode =
  | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8
  | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16
export type ZbaManagedCommandKind = 'driver' | 'plugin' | 'provider' | 'mcp'

/** 判别键：kind 决定 key 的形状（driver/device 是对象，plugin/provider/mcp 是字符串）。 */
export type ZbaManagedObjectKey =
  | { kind: 'driver'; key: { platformId: string; accountScope: string; instanceId: string } }
  | { kind: 'device'; key: { deviceId: string } }
  | { kind: 'plugin'; key: string }
  | { kind: 'provider'; key: string }
  | { kind: 'mcp'; key: string }

export interface ZbaActionCapability {
  action: ZbaManagedAction
  supported: boolean
  reasonCode: string
}

export interface ZbaManagedDescriptor {
  key: ZbaManagedObjectKey
  displayName: string
  desiredEnabled?: boolean | null
  runtimeState: ZbaManagedRuntimeState
  actions: ZbaActionCapability[]
  credentialConfigured: boolean
  errorCode?: string
  descriptorRevision: string
}

export interface ZbaManagedPage {
  code: ZbaManagedResultCode
  items: ZbaManagedDescriptor[]
  nextCursor?: string | null
  descriptorRevision: string
}

export interface ZbaManagedInspectResult {
  code: ZbaManagedResultCode
  descriptor?: ZbaManagedDescriptor
}

export interface ZbaManagedReceipt {
  key: string
  target: ZbaManagedObjectKey
  action: ZbaManagedAction
  descriptorRevision: string
  state: ZbaManagedOperationState
  errorCode?: string
}

export interface ZbaManagedOperationResult {
  code: ZbaManagedResultCode
  replayed: boolean
  receipt?: ZbaManagedReceipt
}

export interface ZbaManagedCancellationResult {
  code: ZbaManagedResultCode
  state: ZbaManagedOperationState
}

export interface ZbaCapabilityListQuery {
  kind?: ZbaManagedKind
  limit?: number
  cursor?: string
}

export type ZbaConfigurationResultCode =
  | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8
  | 9 | 10 | 11 | 12 | 13 | 14 | 15 | 16

export type ZbaConfigurationDecision = 0 | 1
export type ZbaCommitState = 0 | 1 | 2
export type ZbaApplyState = 0 | 1 | 2 | 3 | 4 | 5
export type ZbaApplyStep = 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7
export type ZbaFieldEffect = 0 | 1 | 2 | 3
export type ZbaCredentialSource = 0 | 1 | 2

export interface ZbaSecretMetadata {
  slot: number
  configured: boolean
  source: ZbaCredentialSource
}

export interface ZbaFieldDescriptor {
  id: number
  effect: ZbaFieldEffect
  canClear: boolean
  editable: boolean
  legacyNeedsReview: boolean
}

export type ZbaFieldVisibility = 0 | 1 | 2
export type ZbaUserValueKind = 0 | 1 | 2
export type ZbaConfigurationValueSource = 0 | 1 | 2 | 3 | 4
export type ZbaCredentialBindingState = 0 | 1 | 2 | 3

/**
 * 配置 read 的字段投影（cfg-0.3.3）。
 *
 * `visibility` 决定携带哪个值字段：`0` 隐藏、`1` 可编辑（`value`）、
 * `2` 可改名（`nameRaw`）。`display` 是服务端已脱敏的展示串，永远不是原始值。
 */
export interface ZbaReadField<T = unknown> {
  visibility: ZbaFieldVisibility
  display: string
  rule: ZbaFieldDescriptor
  userMode: ZbaUserValueKind
  effectiveSource: ZbaConfigurationValueSource
  applyState: ZbaApplyState
  value?: T
  nameRaw?: string
}

export interface ZbaPolicyAccountDefault {
  kind: 'accountDefault'
  platformId: string
  accountScope: string
}

export interface ZbaPolicyInstance {
  kind: 'instance'
  key: { platformId: string; accountScope: string; instanceId: string }
}

export type ZbaPolicyTarget = ZbaPolicyAccountDefault | ZbaPolicyInstance

export interface ZbaListRuleValue {
  kind: 0 | 1 | 2
  value: string
}

export interface ZbaActionRuleValue {
  kind: 0 | 1
  actions: string[]
}

export interface ZbaFeatureOverride {
  feature: number
  enabled: boolean
}

export interface ZbaPlatformPolicyEntryView {
  target: ZbaPolicyTarget
  enabled: ZbaReadField<number>
  chatEnabled: ZbaReadField<number>
  groupWhitelist: ZbaReadField<ZbaListRuleValue>
  privateWhitelist: ZbaReadField<ZbaListRuleValue>
  allowedActions: ZbaReadField<ZbaActionRuleValue>
  features: ZbaReadField<ZbaFeatureOverride[]>
}

export interface ZbaPluginConfigurationView {
  key: string
  desiredEnabled: boolean
}

export interface ZbaProviderConfigurationView {
  key: string
  name: ZbaReadField<string>
  baseUrl: ZbaReadField<string>
  modelName: ZbaReadField<string>
  priority: number
  desiredEnabled: boolean
  credential: ZbaCredentialBindingState
  runtimeApplyState: ZbaApplyState
}

export interface ZbaRegisteredConfigurationView {
  plugins: ZbaPluginConfigurationView[]
  providers: ZbaProviderConfigurationView[]
}

/** `platform` 分区除通用字段外还带策略列表；其余分区只按字段名索引。 */
export interface ZbaPlatformView {
  [key: string]: ZbaReadField
  platformPolicies: ZbaReadField<ZbaPlatformPolicyEntryView[]>
}

export interface ZbaConfigurationView {
  behavior: Record<string, ZbaReadField>
  model: Record<string, ZbaReadField>
  agent: Record<string, ZbaReadField>
  platform: ZbaPlatformView
  voice: Record<string, ZbaReadField>
  music: Record<string, ZbaReadField>
  reporting: Record<string, ZbaReadField>
  registered: ZbaRegisteredConfigurationView
}

export interface ZbaConfigurationReadResult {
  code: ZbaConfigurationResultCode
  version: string
  generation: string
  contractRevision: string
  serverUnixSeconds: number
  // view 为 null 时按 wire 约定省略；非空形状已按 cfg-0.3.3 冻结（8 分区）。
  view?: ZbaConfigurationView
  secrets: ZbaSecretMetadata[]
  fields: ZbaFieldDescriptor[]
  latestApplyState: ZbaApplyState
}

/** 0 = 概览投影（隐藏值），1 = 可编辑投影（带 value）。 */
export type ZbaConfigurationReadKind = 0 | 1

export type ZbaSecretSlot = 0 | 1 | 2 | 3 | 4 | 5
export type ZbaSecretEditKind = 0 | 1 | 2

export interface ZbaSecretEdit {
  slot: ZbaSecretSlot
  kind: ZbaSecretEditKind
  value?: string
}

export type ZbaFieldEditKind = 0 | 1 | 2

/** 单个字段的 Keep(0)/Set(1)/Clear(2)；Clear 不带 value。 */
export interface ZbaFieldEdit {
  kind: ZbaFieldEditKind
  value?: unknown
}

export type ZbaPatchSection =
  | 'behavior' | 'model' | 'agent' | 'platform' | 'voice' | 'music' | 'reporting'

export type ZbaConfigurationPatch = Partial<Record<ZbaPatchSection, Record<string, ZbaFieldEdit>>>

export interface ZbaConfigurationSaveRequest {
  contractRevision: string
  expectedVersion: string
  key: string
  changes: ZbaConfigurationPatch
  secrets: ZbaSecretEdit[]
}

export interface ZbaConfigurationValidationRequest {
  contractRevision: string
  expectedVersion: string
  changes: ZbaConfigurationPatch
  secrets: ZbaSecretEdit[]
}

export interface ZbaManagedValidationResult {
  code: number
  errors: ZbaFieldError[]
}

export interface ZbaApplyStepReport {
  step: ZbaApplyStep
  state: ZbaApplyState
  errorCode?: string
}

export interface ZbaDeviceKeyAssignment {
  inputOrdinal: number
  key: string
}

export interface ZbaConfigurationReceipt {
  key: string
  contractRevision: string
  decision: ZbaConfigurationDecision
  observedVersion: string
  committedVersion?: string
  commitState: ZbaCommitState
  applyState: ZbaApplyState
  steps: ZbaApplyStepReport[]
  deviceKeyAssignments: ZbaDeviceKeyAssignment[]
}

export interface ZbaFieldError {
  address: {
    field: number
    object?: unknown
  }
  code: string
}

export interface ZbaConfigurationSaveResult {
  code: ZbaConfigurationResultCode
  replayed: boolean
  receipt?: ZbaConfigurationReceipt
  currentVersion: string
  errors: ZbaFieldError[]
}

export interface ZbaConfigurationFindResult {
  code: ZbaConfigurationResultCode
  receipt?: ZbaConfigurationReceipt
}

export class ZbaRequestError extends Error {
  readonly status: number
  readonly code: string
  readonly reason?: string
  readonly requestId?: string

  constructor(status: number, code: string, message: string, reason?: string, requestId?: string) {
    super(message)
    this.name = 'ZbaRequestError'
    this.status = status
    this.code = code
    this.reason = reason
    this.requestId = requestId
  }
}

export class ZbaNetworkError extends Error {
  readonly detail?: unknown

  constructor(detail?: unknown) {
    super('network_unavailable')
    this.name = 'ZbaNetworkError'
    this.detail = detail
  }
}

export interface ZbaClientOptions {
  baseUrl?: string
  token?: string
  fetchImpl?: typeof fetch
  timeoutMs?: number
}

interface ZbaResponse {
  status: number
  ok: boolean
  headers: Headers
  data: unknown
}

type AuthMode = 'required' | 'none'

export class ZbaClient {
  readonly baseUrl: string
  /**
   * 本地身份上下文标签：令牌的**单向摘要**。
   *
   * 只用于「同服务 + 同身份」的本地比对（例如锁定后恢复未决提交的只读核对），
   * 不参与鉴权、不进请求、不落存储，也无法从它还原令牌。
   */
  readonly identityLabel: string
  private readonly token: string
  private readonly fetchImpl: typeof fetch
  private readonly timeoutMs: number

  constructor(options: ZbaClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? '/zba/v1').replace(/\/+$/, '')
    this.token = options.token?.trim() ?? ''
    this.identityLabel = digest(this.token)
    this.fetchImpl = options.fetchImpl ?? globalThis.fetch.bind(globalThis)
    this.timeoutMs = options.timeoutMs ?? 10_000
  }

  async health(): Promise<ZbaHealth> {
    const response = await this.fetchJson('/health', { method: 'GET' }, 'none')

    // /health 在 unhealthy 时以 503 返回 ZbaHealth 本体，而不是 error 体；
    // 这是有效的 liveness 结果，不能当成传输失败。
    if (response.ok && isHealth(response.data)) return response.data
    if (response.status === 503 && isHealth(response.data)) return response.data
    if (response.ok) throw this.invalidResponse(response)
    throw this.errorFrom(response)
  }

  meta(): Promise<ZbaMeta> {
    return this.request('/meta', { method: 'GET' }, 'required', isMeta)
  }

  status(): Promise<ZbaStatus> {
    return this.request('/status', { method: 'GET' }, 'required', isStatus)
  }

  /**
   * 读取配置快照。kind=1 返回可编辑投影（字段带 `value`），kind=0 只返回
   * 隐藏投影（字段不带值）。两者都只读；保存走 `/configuration/save`。
   */
  readConfiguration(kind: ZbaConfigurationReadKind = 1): Promise<ZbaConfigurationReadResult> {
    return this.request('/configuration/read', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind })
    }, 'required', isConfigurationReadResult)
  }

  /** 只校验、不落盘；需要 admin（路由 scope 为 Admin）。 */
  async validateConfiguration(request: ZbaConfigurationValidationRequest): Promise<ZbaManagedValidationResult> {
    const response = await this.fetchJson('/capabilities/validate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    }, 'required')
    // 版本冲突/校验失败以 409/400 返回，但 body 仍是结果对象而不是 error 体。
    if (isManagedValidationResult(response.data)) return response.data
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 整批保存；同一个 key + 同一个请求体重放会返回 replayed=true 的原回执。 */
  async saveConfiguration(request: ZbaConfigurationSaveRequest): Promise<ZbaConfigurationSaveResult> {
    const response = await this.fetchJson('/configuration/save', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    }, 'required')
    if (isConfigurationSaveResult(response.data)
      && configurationResultStatus(response.data.code) === response.status) return response.data
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 按操作键核对一次提交的最终状态；NotFound(14) 表示同代际内没有该回执。 */
  async findConfiguration(key: string): Promise<ZbaConfigurationFindResult> {
    const response = await this.fetchJson('/configuration/find', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key })
    }, 'required')
    // 与保存一样：只有状态码与业务码构成契约上的配对，这次核对才具权威性。
    // HTTP 503 上带 code 14 这类矛盾响应既不是“没有回执”，也不是可靠失败，
    // 不能据此断言提交没有落盘；抛出错误让页面保留操作键与未知态。
    if (isConfigurationFindResult(response.data)
      && configurationResultStatus(response.data.code) === response.status) return response.data
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

/**
   * 能力目录（分页）。游标是后端签发的不透明令牌（Base64URL 无填充 + '.' + 签名），
   * 客户端只负责原样回传；翻页失败就抛真实错误，由页面显示，不重试、不改写。
   */
  async listCapabilities(query: ZbaCapabilityListQuery = {}): Promise<ZbaManagedPage> {
    const params = new URLSearchParams()
    if (query.kind !== undefined) params.set('kind', String(query.kind))
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    if (query.cursor !== undefined) params.set('cursor', query.cursor)
    const suffix = params.size > 0 ? `?${params.toString()}` : ''
    const response = await this.fetchJson(`/capabilities${suffix}`, { method: 'GET' }, 'required')
    if (isManagedPage(response.data) && managedResultStatus(response.data.code) === response.status) return response.data
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 单个能力的描述符；NotFound(2) 以 404 返回，仍是可解释结果。 */
  async inspectCapability(key: ZbaManagedObjectKey): Promise<ZbaManagedInspectResult> {
    const response = await this.fetchJson('/capabilities/inspect', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key })
    }, 'required')
    if (isManagedInspectResult(response.data) && managedResultStatus(response.data.code) === response.status) {
      return response.data
    }
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 执行一个受支持的管理动作；operation key 由 mg1 生成，回执用于核对。 */
  async executeCapability(request: {
    contractRevision: string
    expectedDescriptorRevision: string
    key: string
    command: unknown
  }): Promise<ZbaManagedOperationResult> {
    const response = await this.fetchJson('/capabilities/execute', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(request)
    }, 'required')
    if (isManagedOperationResult(response.data) && managedResultStatus(response.data.code) === response.status) {
      return response.data
    }
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 按 operation key 核对动作结果（只读）。 */
  async queryCapability(key: string): Promise<ZbaManagedOperationResult> {
    const response = await this.fetchJson('/capabilities/query', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(key)
    }, 'required')
    if (isManagedOperationResult(response.data) && managedResultStatus(response.data.code) === response.status) {
      return response.data
    }
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 请求取消一个管理动作；返回的状态是服务端真实状态，不是“已取消”。 */
  async cancelCapability(key: string): Promise<ZbaManagedCancellationResult> {
    const response = await this.fetchJson('/capabilities/cancel', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(key)
    }, 'required')
    if (isManagedCancellationResult(response.data) && managedResultStatus(response.data.code) === response.status) {
      return response.data
    }
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  // ── S3：Agent 会话 ───────────────────────────────────────────────────────

  /** 列出某个来源下已存在的 server 会话。 */
  async listSessions(sourceKey: string, limit = 50): Promise<ZbaSessionPage> {
    const params = new URLSearchParams({ sourceKey, limit: String(limit) })
    const response = await this.fetchJson(`/sessions?${params.toString()}`, { method: 'GET' }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isSessionPage(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  /**
   * 确保来源下存在一个当前 server 会话。
   *
   * 提交任务前必须先有会话：后端对不存在的 sessionId 直接 404 session_not_found，
   * 不会替你新建。这里是唯一被允许的“顺带创建”入口。
   */
  async ensureSession(sourceKey: string): Promise<ZbaSessionEnsureResult> {
    const response = await this.fetchJson('/sessions/ensure', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sourceKey })
    }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isSessionEnsureResult(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  // ── S3：Agent 任务 ───────────────────────────────────────────────────────

  /** 任务列表（分页）。游标由后端签发，客户端只原样回传。 */
  async listTasks(query: ZbaTaskListQuery = {}): Promise<ZbaTaskPage> {
    const params = new URLSearchParams()
    if (query.limit !== undefined) params.set('limit', String(query.limit))
    if (query.cursor !== undefined) params.set('cursor', query.cursor)
    const suffix = params.size > 0 ? `?${params.toString()}` : ''
    const response = await this.fetchJson(`/tasks${suffix}`, { method: 'GET' }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isTaskPage(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  /**
   * 提交任务。幂等键由调用方持有：同键重复提交不会产生第二个任务，
   * 结果未知时用它 /tasks/find 核对，而不是换键重提。
   */
  async submitTask(request: ZbaTaskSubmission, idempotencyKey: string): Promise<ZbaTaskAccepted> {
    const response = await this.fetchJson('/tasks', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'Idempotency-Key': idempotencyKey },
      body: JSON.stringify(request)
    }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isTaskAccepted(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  /** 按幂等键核对提交结果：found / expired / accepted_unknown / unknown。 */
  async findTask(key: string): Promise<ZbaTaskFindResult> {
    const response = await this.fetchJson('/tasks/find', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ key })
    }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isTaskFindResult(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  /** 单个任务当前状态；404 是可解释结果（任务不存在）。 */
  async getTask(id: string): Promise<ZbaTaskSummary | null> {
    const response = await this.fetchJson(`/tasks/${encodeURIComponent(id)}`, { method: 'GET' })
    if (response.status === 200 && isTaskSummary(response.data)) return response.data
    if (response.status === 404 && isTaskNotFound(response.data)) return null
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 有界结果摘要；admin 路由，不可用时 available=false 并给出 reasonCode。 */
  async getTaskResult(id: string): Promise<ZbaTaskResult | null> {
    const response = await this.fetchJson(`/tasks/${encodeURIComponent(id)}/result`, { method: 'GET' })
    if (response.status === 200 && isTaskResult(response.data)) return response.data
    if (response.status === 404 && isTaskNotFound(response.data)) return null
    if (!response.ok) throw this.errorFrom(response)
    throw this.invalidResponse(response)
  }

  /** 请求取消：返回服务端真实状态，不把“已请求”写成“已取消”。 */
  async cancelTask(id: string): Promise<ZbaTaskCancelResult> {
    const response = await this.fetchJson(`/tasks/${encodeURIComponent(id)}/cancel`, { method: 'POST' }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isTaskCancelResult(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  // ── S3：审批 ────────────────────────────────────────────────────────────

  /** 待审批列表（已脱敏摘要，不含工具载荷）。 */
  async listApprovals(): Promise<ZbaApprovalSummary[]> {
    const response = await this.fetchJson('/approvals', { method: 'GET' }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (!isApprovalList(response.data)) throw this.invalidResponse(response)
    return Array.isArray(response.data) ? response.data : response.data.items
  }

  /** 批准或拒绝一次待审批请求；拒绝与过期都不能当作已执行。 */
  async decideApproval(id: string, approve: boolean): Promise<ZbaApprovalDecisionResult> {
    const response = await this.fetchJson(`/approvals/${encodeURIComponent(id)}/decide`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ approve })
    }, 'required')
    // 先验 HTTP 状态再认成功体：5xx/4xx 的错误体即使长得像成功回包也不能算受理。
    if (!response.ok) throw this.errorFrom(response)
    if (isApprovalDecisionResult(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  /**
   * 读取 after 之后的有序事件。
   *
   * 事件只是**失效通知**：不携带结果，也不能当作状态。客户端据此重读对应资源，
   * 最终状态以资源读取为准。resyncRequired=true 表示中间有缺口，必须整份重读。
   */
  async listEvents(after: number, limit = 100): Promise<ZbaEventPage> {
    const params = new URLSearchParams({ after: String(after), limit: String(limit) })
    const response = await this.fetchJson(`/events?${params.toString()}`, { method: 'GET' }, 'required')
    if (!response.ok) throw this.errorFrom(response)
    if (isEventPage(response.data)) return response.data
    throw this.invalidResponse(response)
  }

  /**
   * 只读地确认当前 Bearer 是否具备 admin 角色：

   * GET /tasks/{id}/result 是 admin 路由；admin 对不存在的 id 得到 404 task_not_found，
   * read 得到 403 forbidden。不使用任何写请求试探权限。
   *
   * 404 必须带 task_not_found 才算“路由可达且已授权”；代理误配/旧版本返回的
   * 其它 404 不能把只读令牌误显示为管理员。
   */
  async checkAdmin(): Promise<boolean> {
    const response = await this.fetchJson('/tasks/s1-permission-check/result', { method: 'GET' })
    if (response.status === 403) return false

    if (response.status === 200) {
      if (isRecord(response.data) && typeof response.data.id === 'string' && typeof response.data.state === 'number') {
        return true
      }
      throw this.invalidResponse(response)
    }

    if (response.status === 404 && isTaskNotFound(response.data)) return true
    throw this.errorFrom(response)
  }

  private async request<T>(
    path: string,
    init: RequestInit,
    auth: AuthMode,
    decode: (value: unknown) => value is T
  ): Promise<T> {
    const response = await this.fetchJson(path, init, auth)
    if (!response.ok) throw this.errorFrom(response)
    if (!decode(response.data)) throw this.invalidResponse(response)
    return response.data
  }

  /**
   * 一个 deadline 覆盖「等响应头 + 读完整响应体」。
   *
   * fetch 返回 Response 只代表 headers 已到；慢速/截断 body 仍然可以让
   * response.text() 无限挂起。这里用同一个 AbortController 和 Promise.race
   * 兜住两段，并在 finally 里清 timer。deadline 先赢时 work 仍可能稍后拒绝，
   * 用 catch 吞掉，避免未处理拒绝。
   */
  private async fetchJson(
    path: string,
    init: RequestInit,
    auth: AuthMode = 'required'
  ): Promise<ZbaResponse> {
    const headers = new Headers(init.headers)
    headers.set('Accept', 'application/json')
    if (auth === 'required' && this.token.length > 0) {
      headers.set('Authorization', `Bearer ${this.token}`)
    }

    const controller = new AbortController()
    let timer: ReturnType<typeof setTimeout> | undefined
    const deadline = new Promise<never>((_, reject) => {
      timer = setTimeout(() => {
        controller.abort()
        reject(new ZbaNetworkError('timeout'))
      }, this.timeoutMs)
    })

    const work = (async (): Promise<ZbaResponse> => {
      const response = await this.fetchImpl(`${this.baseUrl}${path}`, {
        ...init,
        headers,
        signal: init.signal
          ? combineSignals(init.signal, controller.signal)
          : controller.signal
      })
      let text: string
      try {
        text = await response.text()
      } catch (error) {
        throw new ZbaNetworkError(error)
      }
      return {
        status: response.status,
        ok: response.ok,
        headers: response.headers,
        data: parseJson(text)
      }
    })()

    try {
      return await Promise.race([work, deadline])
    } catch (error) {
      if (error instanceof ZbaNetworkError) throw error
      throw new ZbaNetworkError(error)
    } finally {
      if (timer !== undefined) clearTimeout(timer)
      work.catch(() => undefined)
    }
  }

  private invalidResponse(response: ZbaResponse): ZbaRequestError {
    return new ZbaRequestError(
      response.status,
      'invalid_json',
      'ZBA 响应格式无效。',
      undefined,
      response.headers.get('X-Request-Id') ?? undefined
    )
  }

  private errorFrom(response: ZbaResponse): ZbaRequestError {
    const error = isRecord(response.data) && isRecord(response.data.error) ? response.data.error : null
    const code = typeof error?.code === 'string' ? error.code : 'http_error'
    const message = typeof error?.message === 'string'
      ? error.message
      : `ZBA 返回 HTTP ${response.status}。`
    const reason = typeof error?.reason === 'string' ? error.reason : undefined
    const requestId = response.headers.get('X-Request-Id') ?? undefined
    return new ZbaRequestError(response.status, code, message, reason, requestId)
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}

/**
 * 令牌 → 本地身份标签。
 *
 * FNV-1a 32 位双通道：不是密码学哈希，也不需要是——它只在本机内存里做
 * 「这两个会话是不是同一个身份」的判断，从不外发、从不落盘。空令牌得到固定
 * 标签，未连接时不会误配到任何真实会话。
 */
function digest(value: string): string {
  if (value.length === 0) return 'anonymous'
  let h1 = 0x811c9dc5
  let h2 = 0x01000193
  for (let index = 0; index < value.length; index += 1) {
    const code = value.charCodeAt(index)
    h1 ^= code
    h1 = Math.imul(h1, 0x01000193) >>> 0
    h2 = (Math.imul(h2 ^ code, 0x85ebca6b) + index) >>> 0
  }
  return h1.toString(16).padStart(8, '0') + h2.toString(16).padStart(8, '0')
}

function isTaskNotFound(value: unknown): boolean {
  return isRecord(value)
    && isRecord(value.error)
    && value.error.code === 'task_not_found'
}

function isSessionSummary(value: unknown): value is ZbaSessionSummary {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.name === 'string'
    && typeof value.backend === 'string'
    && typeof value.current === 'boolean'
    && typeof value.turns === 'number'
    && typeof value.historyChars === 'number'
    && typeof value.createdAt === 'string'
    && typeof value.updatedAt === 'string'
}

function isSessionPage(value: unknown): value is ZbaSessionPage {
  return isRecord(value)
    && Array.isArray(value.items)
    && value.items.every(isSessionSummary)
    && isOptionalString(value.nextCursor)
}

/** 末页游标被服务端整段省略：undefined / null / string 都是合法 wire。 */
function isOptionalString(value: unknown): boolean {
  return value === undefined || value === null || typeof value === 'string'
}

/** 可选字段出现就必须是对的类型；缺失/null 都算「没有」。 */
function isOptionalResultString(value: unknown): boolean {
  return value === undefined || typeof value === 'string'
}

function isOptionalNonNegativeFiniteNumber(value: unknown): boolean {
  return value === undefined || (typeof value === 'number' && Number.isFinite(value) && value >= 0)
}

function isOptionalNonEmptyString(value: unknown): value is string | undefined | null {
  return value === undefined || value === null || (typeof value === 'string' && value.length > 0)
}

function isSessionEnsureResult(value: unknown): value is ZbaSessionEnsureResult {
  return isRecord(value) && isSessionSummary(value.session) && typeof value.created === 'boolean'
}

function isTaskSummary(value: unknown): value is ZbaTaskSummary {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.state === 'number'
    && typeof value.target === 'string'
    && typeof value.sessionId === 'string'
    && typeof value.createdAt === 'string'
    && typeof value.updatedAt === 'string'
    && typeof value.cancellationRequested === 'boolean'
}

function isTaskPage(value: unknown): value is ZbaTaskPage {
  return isRecord(value)
    && Array.isArray(value.items)
    && value.items.every(isTaskSummary)
    && isOptionalString(value.nextCursor)
}

/**
 * 受理回执的运行时校验（R5）。
 *
 * 计划 §9.1 第 226 行：**返回稳定 id 才跟踪为已受理**。只检查
 * `typeof id === 'string'` 会让 `{id:"",state:0,acceptedAt:...}` 这种 202
 * 被当成受理成功——没有稳定任务 id 就无从查询/取消/核对，关联却已被"解决"。
 * 空 id 判为非法响应：submitTask 抛出 invalid_json，页面保持结果未知并保留原键。
 */
function isTaskAccepted(value: unknown): value is ZbaTaskAccepted {
  return isRecord(value)
    && typeof value.id === 'string'
    && value.id.length > 0
    && typeof value.state === 'number'
    && typeof value.acceptedAt === 'string'
}

function isTaskResult(value: unknown): value is ZbaTaskResult {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.state === 'number'
    && typeof value.updatedAt === 'string'
    && typeof value.available === 'boolean'
    && typeof value.summaryTruncated === 'boolean'
    && typeof value.summaryChars === 'number' && Number.isFinite(value.summaryChars) && value.summaryChars >= 0
    && isOptionalResultString(value.reasonCode)
    && isOptionalResultString(value.summary)
    && isOptionalResultString(value.errorCode)
    && isOptionalNonNegativeFiniteNumber(value.toolCalls)
    && isOptionalNonNegativeFiniteNumber(value.durationMs)
}

/**
 * `/tasks/find` 的运行时校验。
 *
 * 只检查 code + instanceId 会让 `{code:'found',task:{id:42}}` 这种畸形回包
 * 把未决关联升级成「已受理」（S3-08）。这里按 code 分别要求配套身份：
 * - `found`：必须带**完整**的任务摘要，且 id 是稳定字符串；
 * - `expired` / `accepted_unknown`：持久回执存在，必须给受理时的 taskId；
 * - `unknown`：不给身份（但「查不到」不等于「没执行」。）
 */
function isTaskFindResult(value: unknown): value is ZbaTaskFindResult {
  if (!isRecord(value)) return false
  if (typeof value.code !== 'string' || !TASK_FIND_CODES.includes(value.code as ZbaTaskFindCode)) return false
  if (typeof value.instanceId !== 'string' || value.instanceId.length === 0) return false
  if (!isOptionalNonEmptyString(value.taskId)) return false
  if (value.acceptedAt !== undefined && value.acceptedAt !== null && typeof value.acceptedAt !== 'string') return false
  switch (value.code) {
    case 'found':
      return isTaskSummary(value.task) && typeof (value.task as ZbaTaskSummary).id === 'string'
        && (value.task as ZbaTaskSummary).id.length > 0
        && (value.taskId === undefined || value.taskId === null || value.taskId === (value.task as ZbaTaskSummary).id)
    case 'expired':
    case 'accepted_unknown':
      return (value.task === undefined || value.task === null) && typeof value.taskId === 'string'
    default:
      return value.task === undefined || value.task === null
  }
}

function isEvent(value: unknown): value is ZbaEvent {
  return isRecord(value)
    && typeof value.sequence === 'number'
    && Number.isSafeInteger(value.sequence)
    && value.sequence >= 0
    && typeof value.type === 'string'
    && value.type.length > 0
    && typeof value.at === 'string'
    && isOptionalNonEmptyString(value.taskId)
    && isOptionalNonEmptyString(value.state)
    && isOptionalNonEmptyString(value.version)
    && isOptionalNonEmptyString(value.reasonCode)
}

function isEventPage(value: unknown): value is ZbaEventPage {
  return isRecord(value)
    && Array.isArray(value.items)
    && value.items.every(isEvent)
    && typeof value.nextSequence === 'number'
    && Number.isSafeInteger(value.nextSequence)
    && value.nextSequence >= 0
    && typeof value.resyncRequired === 'boolean'
    && typeof value.generatedAt === 'string'
}

function isTaskCancelResult(value: unknown): value is ZbaTaskCancelResult {
  return isRecord(value)
    && typeof value.state === 'number'
    && typeof value.cancellationRequested === 'boolean'
    && typeof value.outcome === 'string'
}

function isApprovalSummary(value: unknown): value is ZbaApprovalSummary {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.toolId === 'string'
    && typeof value.summary === 'string'
    && typeof value.session === 'string'
    && typeof value.expiresAt === 'string'
    && typeof value.policyVersion === 'number'
}

/**
 * 审批队列的真实 wire 是**裸数组**（ZbaEndpoint.Approvals.cs 直接 Json(IReadOnlyList)）。
 *
 * 之前只认 `{items:[]}`，真实 HTTP 200 数组被判成 invalid_json，队列显示成空
 * ——把「读取失败」伪装成「确认没有审批」（S3-03）。这里以裸数组为准，
 * 同时容忍带 `items` 的分页信封，但两种形态都必须逐条通过身份校验；
 * 都不是就抛错，让 UI 显示读取失败而不是空队列。
 */
function isApprovalList(value: unknown): value is { items: ZbaApprovalSummary[] } {
  if (Array.isArray(value)) return value.every(isApprovalSummary)
  return isRecord(value) && Array.isArray(value.items) && value.items.every(isApprovalSummary)
}

function isApprovalDecisionResult(value: unknown): value is ZbaApprovalDecisionResult {
  return isRecord(value)
    && typeof value.ok === 'boolean'
    && typeof value.decision === 'string'
    && typeof value.reasonCode === 'string'
}

function isHealth(value: unknown): value is ZbaHealth {
  return isRecord(value)
    && typeof value.status === 'string'
    && typeof value.ready === 'boolean'
    && typeof value.configured === 'boolean'
    && typeof value.contractVersion === 'string'
    && typeof value.checkedAt === 'string'
}

/**
 * 运行时 wire 解码，而不只是 TypeScript 断言。
 *
 * 策略：S1 已冻结的必需字段、类型和嵌套数组必须全部有效；缺字段、null、
 * 错误类型或错误嵌套都算 `invalid_json`。未知的附加字段被忽略，以便后端
 * 追加字段时保持向后兼容；这些字段不会进入 UI。
 */
function isStringArray(value: unknown): value is string[] {
  return Array.isArray(value) && value.every((item) => typeof item === 'string')
}

function isMeta(value: unknown): value is ZbaMeta {
  return isRecord(value)
    && typeof value.contractVersion === 'string'
    && typeof value.serverVersion === 'string'
    && typeof value.authentication === 'string'
    && isStringArray(value.scopes)
    && typeof value.sse === 'boolean'
    && typeof value.mcpManagement === 'boolean'
    && typeof value.dockerManagement === 'boolean'
    && typeof value.maxBodyBytes === 'number'
    && Number.isFinite(value.maxBodyBytes)
    && typeof value.instanceId === 'string'
    && isStringArray(value.features)
}

function isComponent(value: unknown): value is ZbaComponent {
  return isRecord(value)
    && typeof value.id === 'string'
    && typeof value.kind === 'string'
    && typeof value.displayName === 'string'
    && typeof value.state === 'string'
    && typeof value.available === 'boolean'
    && (value.reasonCode === undefined || typeof value.reasonCode === 'string')
    && (value.updatedAt === undefined || typeof value.updatedAt === 'string')
}

function isErrorSummary(value: unknown): value is ZbaErrorSummary {
  return isRecord(value)
    && typeof value.at === 'string'
    && typeof value.scope === 'string'
    && typeof value.code === 'string'
}

function isStatus(value: unknown): value is ZbaStatus {
  return isRecord(value)
    && typeof value.state === 'string'
    && typeof value.ready === 'boolean'
    && typeof value.version === 'string'
    && typeof value.startedAt === 'string'
    && typeof value.updatedAt === 'string'
    && (value.configurationVersion === undefined || typeof value.configurationVersion === 'string')
    && typeof value.configurationApplyState === 'string'
    && Array.isArray(value.components)
    && value.components.every(isComponent)
    && Array.isArray(value.recentErrors)
    && value.recentErrors.every(isErrorSummary)
}

const CONFIGURATION_SECTIONS = ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting'] as const

function isInteger(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value)
}

function isFieldDescriptor(value: unknown): value is ZbaFieldDescriptor {
  return isRecord(value)
    && isInteger(value.id)
    && isInteger(value.effect)
    && typeof value.canClear === 'boolean'
    && typeof value.editable === 'boolean'
    && typeof value.legacyNeedsReview === 'boolean'
}

/**
 * ReadField 的运行时约束：`visibility` 决定携带哪个值字段。
 * 隐藏字段带值、可见字段缺值、改名变体用 `value` 都按 `invalid_json` 拒绝，
 * 避免 S2 把契约漂移当成可编辑原始值。
 */
function isReadField(value: unknown): value is ZbaReadField {
  if (!isRecord(value)) return false
  const visibility = value.visibility
  if (visibility !== 0 && visibility !== 1 && visibility !== 2) return false
  if (typeof value.display !== 'string'
    || !isInteger(value.userMode)
    || !isInteger(value.effectiveSource)
    || !isInteger(value.applyState)
    || !isFieldDescriptor(value.rule)) return false
  const hasValue = Object.prototype.hasOwnProperty.call(value, 'value')
  const hasNameRaw = Object.prototype.hasOwnProperty.call(value, 'nameRaw')
  if (visibility === 0) return !hasValue && !hasNameRaw
  if (visibility === 1) return hasValue && value.value !== null
  return typeof value.nameRaw === 'string' && !hasValue
}

function isConfigurationSection(value: unknown): boolean {
  return isRecord(value) && Object.values(value).every(isReadField)
}

function isRegisteredView(value: unknown): value is ZbaRegisteredConfigurationView {
  if (!isRecord(value) || !Array.isArray(value.plugins) || !Array.isArray(value.providers)) return false
  const pluginsValid = value.plugins.every((plugin) =>
    isRecord(plugin) && typeof plugin.key === 'string' && typeof plugin.desiredEnabled === 'boolean')
  const providersValid = value.providers.every((provider) =>
    isRecord(provider)
    && typeof provider.key === 'string'
    && isReadField(provider.name)
    && isReadField(provider.baseUrl)
    && isReadField(provider.modelName)
    && isInteger(provider.priority)
    && typeof provider.desiredEnabled === 'boolean'
    && isInteger(provider.credential)
    && isInteger(provider.runtimeApplyState))
  return pluginsValid && providersValid
}

function isConfigurationView(value: unknown): value is ZbaConfigurationView {
  if (!isRecord(value)) return false
  for (const section of CONFIGURATION_SECTIONS) {
    if (!isConfigurationSection(value[section])) return false
  }
  return isRegisteredView(value.registered)
}

function isSecretMetadata(value: unknown): value is ZbaSecretMetadata {
  return isRecord(value)
    && isInteger(value.slot)
    && typeof value.configured === 'boolean'
    && isInteger(value.source)
}

function isConfigurationReadResult(value: unknown): value is ZbaConfigurationReadResult {
  if (!isRecord(value)) return false
  if (!isInteger(value.code)
    || typeof value.version !== 'string'
    || typeof value.generation !== 'string'
    || typeof value.contractRevision !== 'string'
    || typeof value.serverUnixSeconds !== 'number'
    || !Number.isFinite(value.serverUnixSeconds)
    || !Array.isArray(value.secrets)
    || !value.secrets.every(isSecretMetadata)
    || !Array.isArray(value.fields)
    || !value.fields.every(isFieldDescriptor)
    || !isInteger(value.latestApplyState)) return false
  if (value.view !== undefined && !isConfigurationView(value.view)) return false
  return true
}

function isFieldError(value: unknown): value is ZbaFieldError {
  return isRecord(value)
    && isRecord(value.address)
    && isInteger(value.address.field)
    && typeof value.code === 'string'
}

function isApplyStepReport(value: unknown): value is ZbaApplyStepReport {
  return isRecord(value)
    && isInteger(value.step)
    && isInteger(value.state)
    && (value.errorCode === undefined || typeof value.errorCode === 'string')
}

function isDeviceKeyAssignment(value: unknown): value is ZbaDeviceKeyAssignment {
  return isRecord(value)
    && isInteger(value.inputOrdinal)
    && typeof value.key === 'string'
}

function isConfigurationReceipt(value: unknown): value is ZbaConfigurationReceipt {
  return isRecord(value)
    && typeof value.key === 'string'
    && typeof value.contractRevision === 'string'
    && isInteger(value.decision)
    && typeof value.observedVersion === 'string'
    && (value.committedVersion === undefined || typeof value.committedVersion === 'string')
    && isInteger(value.commitState)
    && isInteger(value.applyState)
    && Array.isArray(value.steps)
    && value.steps.every(isApplyStepReport)
    && Array.isArray(value.deviceKeyAssignments)
    && value.deviceKeyAssignments.every(isDeviceKeyAssignment)
}

function isManagedObjectKey(value: unknown): value is ZbaManagedObjectKey {
  if (!isRecord(value) || typeof value.kind !== 'string') return false
  switch (value.kind) {
    case 'driver':
      return isRecord(value.key)
        && typeof value.key.platformId === 'string'
        && typeof value.key.accountScope === 'string'
        && typeof value.key.instanceId === 'string'
    case 'device':
      return isRecord(value.key) && typeof value.key.deviceId === 'string'
    case 'plugin':
    case 'provider':
    case 'mcp':
      return typeof value.key === 'string'
    default:
      return false
  }
}

function isActionCapability(value: unknown): value is ZbaActionCapability {
  return isRecord(value)
    && isInteger(value.action)
    && typeof value.supported === 'boolean'
    && typeof value.reasonCode === 'string'
}

function isManagedDescriptor(value: unknown): value is ZbaManagedDescriptor {
  return isRecord(value)
    && isManagedObjectKey(value.key)
    && typeof value.displayName === 'string'
    && (value.desiredEnabled === undefined || value.desiredEnabled === null || typeof value.desiredEnabled === 'boolean')
    && isInteger(value.runtimeState)
    && Array.isArray(value.actions)
    && value.actions.every(isActionCapability)
    && typeof value.credentialConfigured === 'boolean'
    && (value.errorCode === undefined || typeof value.errorCode === 'string')
    && typeof value.descriptorRevision === 'string'
}

function isManagedPage(value: unknown): value is ZbaManagedPage {
  return isRecord(value)
    && isInteger(value.code)
    && Array.isArray(value.items)
    && value.items.every(isManagedDescriptor)
    && (value.nextCursor === undefined || value.nextCursor === null || typeof value.nextCursor === 'string')
    && typeof value.descriptorRevision === 'string'
}

function isManagedInspectResult(value: unknown): value is ZbaManagedInspectResult {
  return isRecord(value)
    && isInteger(value.code)
    && (value.descriptor === undefined || isManagedDescriptor(value.descriptor))
}

function isManagedReceipt(value: unknown): value is ZbaManagedReceipt {
  return isRecord(value)
    && typeof value.key === 'string'
    && isManagedObjectKey(value.target)
    && isInteger(value.action)
    && typeof value.descriptorRevision === 'string'
    && isInteger(value.state)
    && (value.errorCode === undefined || typeof value.errorCode === 'string')
}

function isManagedOperationResult(value: unknown): value is ZbaManagedOperationResult {
  return isRecord(value)
    && isInteger(value.code)
    && typeof value.replayed === 'boolean'
    && (value.receipt === undefined || isManagedReceipt(value.receipt))
}

function isManagedCancellationResult(value: unknown): value is ZbaManagedCancellationResult {
  return isRecord(value) && isInteger(value.code) && isInteger(value.state)
}

/**
 * 管理面结果码 → 后端返回的 HTTP 状态（ZbaEndpoint.Status(ManagedResultCode)）。
 * 只有配对成立才把 body 当成权威结果，避免"成功码搭错误状态"被误读。
 */
function managedResultStatus(code: ZbaManagedResultCode): number {
  switch (code) {
    case 0:
      return 200
    case 1:
      return 403
    case 2:
      return 404
    case 4:
      return 422
    case 3:
    case 5:
    case 7:
    case 9:
    case 11:
    case 15:
      return 409
    case 6:
    case 8:
    case 16:
      return 400
    case 10:
      return 410
    case 12:
      return 429
    case 13:
      return 503
    case 14:
      return 502
  }
}

function configurationResultStatus(code: ZbaConfigurationResultCode): number {
  switch (code) {
    case 0:
    case 13:
      return 200
    case 1:
      return 403
    case 14:
      return 404
    case 2:
    case 16:
      return 400
    case 3:
    case 4:
    case 6:
    case 8:
    case 11:
    case 15:
      return 409
    case 5:
      return 410
    case 7:
      return 429
    case 9:
    case 10:
    case 12:
      return 503
  }
}

function isConfigurationSaveResult(value: unknown): value is ZbaConfigurationSaveResult {
  if (!isRecord(value)
    || !isInteger(value.code)
    || typeof value.replayed !== 'boolean'
    || typeof value.currentVersion !== 'string'
    || !Array.isArray(value.errors)
    || !value.errors.every(isFieldError)) return false
  return value.receipt === undefined || isConfigurationReceipt(value.receipt)
}

function isConfigurationFindResult(value: unknown): value is ZbaConfigurationFindResult {
  if (!isRecord(value) || !isInteger(value.code)) return false
  if (value.receipt === undefined || value.receipt === null) return true
  return isConfigurationReceipt(value.receipt)
}

function isManagedValidationResult(value: unknown): value is ZbaManagedValidationResult {
  return isRecord(value)
    && isInteger(value.code)
    && Array.isArray(value.errors)
    && value.errors.every(isFieldError)
}

function parseJson(text: string): unknown {
  if (text.length === 0) return null
  try {
    return JSON.parse(text) as unknown
  } catch {
    return null
  }
}

function combineSignals(first: AbortSignal, second: AbortSignal): AbortSignal {
  if (typeof AbortSignal.any === 'function') return AbortSignal.any([first, second])

  const controller = new AbortController()
  const abort = (): void => controller.abort()
  if (first.aborted || second.aborted) {
    controller.abort()
  } else {
    first.addEventListener('abort', abort, { once: true })
    second.addEventListener('abort', abort, { once: true })
  }
  return controller.signal
}
