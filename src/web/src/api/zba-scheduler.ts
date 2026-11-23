export interface ZbaScheduledJobView {
  jobId: string
  scopeKey: string
  maskedScope: string
  kind: string
  cronExpr?: string | null
  timezoneIana: string
  text: string
  missedPolicy: string
  enabled: boolean
  nextFireUnix: number
  lastFiredUnix?: number | null
  failureCount: number
}

export interface ZbaScheduledJobRunView {
  runKey: string
  jobId: string
  scheduledUnix: number
  startedUnix: number
  finishedUnix?: number | null
  deliveryStatus: string
  reasonCode?: string | null
}

export interface ZbaSchedulerJobsResponse {
  revision: number
  jobs: ZbaScheduledJobView[]
}

export interface ZbaSchedulerSaveRequest {
  expectedRevision: number
  idempotencyKey: string
  jobId?: string | null
  scopeKey: string
  kind: string
  cronExpr?: string | null
  timezoneIana?: string | null
  text: string
  missedPolicy?: string | null
  enabled: boolean
  nextFireUnix: number
}

export interface ZbaSchedulerDeleteRequest {
  expectedRevision: number
  idempotencyKey: string
  jobId: string
}

export interface ZbaSchedulerWriteResult {
  ok: boolean
  replayed: boolean
  reasonCode?: string | null
  snapshot: ZbaSchedulerJobsResponse
}

export interface ZbaSchedulerRunsRequest {
  jobId?: string | null
  limit?: number
}

export interface ZbaSchedulerRunsResponse {
  runs: ZbaScheduledJobRunView[]
}

export interface ZbaPersonalTodoView {
  todoId: string
  ownerScopeKey: string
  title: string
  notes?: string | null
  priority: number
  status: string
  dueUnix?: number | null
  linkedJobId?: string | null
  sourceHint?: string | null
  createdUnix: number
  updatedUnix: number
}

export interface ZbaPersonalTodosResponse {
  revision: number
  items: ZbaPersonalTodoView[]
}

export interface ZbaPersonalTodoSaveRequest {
  expectedRevision: number
  idempotencyKey: string
  todoId?: string | null
  title: string
  notes?: string | null
  priority: number
  status: string
  dueUnix?: number | null
  sourceHint?: string | null
  timezoneIana?: string | null
}

export interface ZbaPersonalTodoDeleteRequest {
  expectedRevision: number
  idempotencyKey: string
  todoId: string
}

export interface ZbaPersonalTodoWriteResult {
  ok: boolean
  replayed: boolean
  reasonCode?: string | null
  snapshot: ZbaPersonalTodosResponse
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

function isOptNullInt(value: unknown): value is number | null | undefined {
  return value === undefined || value === null || isInt(value)
}

export function isScheduledJobView(value: unknown): value is ZbaScheduledJobView {
  return isObj(value)
    && typeof value.jobId === 'string'
    && typeof value.scopeKey === 'string'
    && typeof value.maskedScope === 'string'
    && typeof value.kind === 'string'
    && isOptNullStr(value.cronExpr)
    && typeof value.timezoneIana === 'string'
    && typeof value.text === 'string'
    && typeof value.missedPolicy === 'string'
    && typeof value.enabled === 'boolean'
    && isInt(value.nextFireUnix)
    && isOptNullInt(value.lastFiredUnix)
    && isInt(value.failureCount)
}

export function isSchedulerJobsResponse(value: unknown): value is ZbaSchedulerJobsResponse {
  return isObj(value)
    && isInt(value.revision)
    && Array.isArray(value.jobs)
    && value.jobs.every(isScheduledJobView)
}

export function isSchedulerWriteResult(value: unknown): value is ZbaSchedulerWriteResult {
  return isObj(value)
    && typeof value.ok === 'boolean'
    && typeof value.replayed === 'boolean'
    && isOptNullStr(value.reasonCode)
    && isSchedulerJobsResponse(value.snapshot)
}

export function isScheduledJobRunView(value: unknown): value is ZbaScheduledJobRunView {
  return isObj(value)
    && typeof value.runKey === 'string'
    && typeof value.jobId === 'string'
    && isInt(value.scheduledUnix)
    && isInt(value.startedUnix)
    && isOptNullInt(value.finishedUnix)
    && typeof value.deliveryStatus === 'string'
    && isOptNullStr(value.reasonCode)
}

export function isSchedulerRunsResponse(value: unknown): value is ZbaSchedulerRunsResponse {
  return isObj(value)
    && Array.isArray(value.runs)
    && value.runs.every(isScheduledJobRunView)
}

export function isPersonalTodoView(value: unknown): value is ZbaPersonalTodoView {
  return isObj(value)
    && typeof value.todoId === 'string'
    && typeof value.ownerScopeKey === 'string'
    && typeof value.title === 'string'
    && isOptNullStr(value.notes)
    && isInt(value.priority)
    && typeof value.status === 'string'
    && isOptNullInt(value.dueUnix)
    && isOptNullStr(value.linkedJobId)
    && isOptNullStr(value.sourceHint)
    && isInt(value.createdUnix)
    && isInt(value.updatedUnix)
}

export function isPersonalTodosResponse(value: unknown): value is ZbaPersonalTodosResponse {
  return isObj(value)
    && isInt(value.revision)
    && Array.isArray(value.items)
    && value.items.every(isPersonalTodoView)
}

export function isPersonalTodoWriteResult(value: unknown): value is ZbaPersonalTodoWriteResult {
  return isObj(value)
    && typeof value.ok === 'boolean'
    && typeof value.replayed === 'boolean'
    && isOptNullStr(value.reasonCode)
    && isPersonalTodosResponse(value.snapshot)
}
