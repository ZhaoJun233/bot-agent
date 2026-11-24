export interface ZbaQuota {
  sourceKey: string
  displayName: string
  revision: string
  dailyTokenLimit: number
  usedPromptTokens: number
  usedCompletionTokens: number
  usedTokens: number
  remainingTokens: number
  resetDate: string
  nextResetAt: string
  energySaving: boolean
  minimumDailyTokenLimit: number
  maximumDailyTokenLimit: number
}
export interface ZbaQuotaPage { items: ZbaQuota[] }
export interface ZbaQuotaSaveRequest {
  sourceKey: string
  expectedRevision: string
  idempotencyKey: string
  dailyTokenLimit: number
}
export interface ZbaQuotaSaveResult { quota: ZbaQuota }
export type ZbaQuotaFindResult = { found: false; quota?: never } | { found: true; quota: ZbaQuota }

export interface QuotaClient {
  listQuotas(): Promise<ZbaQuotaPage>
  saveQuota(request: ZbaQuotaSaveRequest): Promise<ZbaQuotaSaveResult>
  findQuota(idempotencyKey: string): Promise<ZbaQuotaFindResult>
}

export type QuotaTransport = <T>(path: string, init: RequestInit,
  validate: (value: unknown) => value is T) => Promise<T>

/** Authenticated transport is supplied by the web shell; quota wire rules belong to this package. */
export class QuotaApi {
  constructor(private readonly request: QuotaTransport) {}
  listQuotas(): Promise<ZbaQuotaPage> {
    return this.request('/quotas', { method: 'GET' }, isQuotaPage)
  }

  /** One explicit CAS write; never replay automatically. */
  saveQuota(request: ZbaQuotaSaveRequest): Promise<ZbaQuotaSaveResult> {
    return this.request('/quotas/save', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(request)
    }, (value): value is ZbaQuotaSaveResult => isQuotaSaveResult(value)
      && value.quota.sourceKey === request.sourceKey && value.quota.dailyTokenLimit === request.dailyTokenLimit)
  }

  findQuota(idempotencyKey: string): Promise<ZbaQuotaFindResult> {
    return this.request('/quotas/find', {
      method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ idempotencyKey })
    }, isQuotaFindResult)
  }

}

function isQuota(value: unknown): value is ZbaQuota {
  if (!isRecord(value)) return false
  for (const key of ['sourceKey', 'displayName', 'revision']) {
    if (typeof value[key] !== 'string' || !value[key].trim()) return false
  }
  for (const key of ['dailyTokenLimit', 'usedPromptTokens', 'usedCompletionTokens', 'usedTokens',
    'remainingTokens', 'minimumDailyTokenLimit', 'maximumDailyTokenLimit']) {
    if (typeof value[key] !== 'number' || !Number.isSafeInteger(value[key]) || value[key] < 0) return false
  }
  const minimum = value.minimumDailyTokenLimit as number
  const maximum = value.maximumDailyTokenLimit as number
  const limit = value.dailyTokenLimit as number
  if (minimum > maximum || limit < minimum || limit > maximum || maximum > 2147483647) return false
  if (typeof value.resetDate !== 'string' || !/^\d{4}-\d{2}-\d{2}$/.test(value.resetDate)
    || Number.isNaN(Date.parse(value.resetDate))
    || new Date(value.resetDate).toISOString().slice(0, 10) !== value.resetDate) return false
  return typeof value.energySaving === 'boolean' && typeof value.nextResetAt === 'string'
    && /^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})$/.test(value.nextResetAt)
    && Number.isFinite(Date.parse(value.nextResetAt))
}
function isQuotaPage(value: unknown): value is ZbaQuotaPage {
  return isRecord(value) && Array.isArray(value.items) && value.items.every(isQuota)
    && new Set(value.items.map((item) => item.sourceKey)).size === value.items.length
}
function isQuotaSaveResult(value: unknown): value is ZbaQuotaSaveResult {
  return isRecord(value) && isQuota(value.quota)
}
function isQuotaFindResult(value: unknown): value is ZbaQuotaFindResult {
  return isRecord(value) && (value.found === true ? isQuota(value.quota)
    : value.found === false && value.quota === undefined)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null
}
