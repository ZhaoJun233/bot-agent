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
  private readonly token: string
  private readonly fetchImpl: typeof fetch
  private readonly timeoutMs: number

  constructor(options: ZbaClientOptions = {}) {
    this.baseUrl = (options.baseUrl ?? '/zba/v1').replace(/\/+$/, '')
    this.token = options.token?.trim() ?? ''
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

function isTaskNotFound(value: unknown): boolean {
  return isRecord(value)
    && isRecord(value.error)
    && value.error.code === 'task_not_found'
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
