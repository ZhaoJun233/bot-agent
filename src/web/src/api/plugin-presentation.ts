/** Additive display metadata. Identity and enable state remain registry-owned. */
export interface PluginPresentationMetadata {
  name: string
  summary: string
  description: string
  category: string
  author: string
  repository: string
  license: string
  usage: string
  riskNotice: string
  configuration: string
  compatibility: string
  changelog: string
  maintenance: string
  permissionNotes: string
  tags: string[]
  runtime: 'builtin' | 'dotnet' | 'python'
  sourceKind: 'builtin' | 'official' | 'community'
  review: {
    status: 'unreviewed' | 'reviewed'
    version: string
    sha256: string | null
    reviewer?: string
    reviewedAt?: string
    sourceRevision?: string
    notes?: string
  }
}

export interface ZbaBuiltinPlugin {
  pluginId: string
  version: string
  enabled: boolean
  metadata: PluginPresentationMetadata
}

const textFields = ['name', 'summary', 'description', 'category', 'author', 'repository', 'license', 'usage', 'riskNotice', 'configuration', 'compatibility', 'changelog', 'maintenance', 'permissionNotes'] as const
function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function isPluginPresentation(value: unknown): value is PluginPresentationMetadata {
  if (!record(value) || !textFields.every(key => typeof value[key] === 'string')
    || !Array.isArray(value.tags) || !value.tags.every(tag => typeof tag === 'string')
    || !['builtin', 'dotnet', 'python'].includes(String(value.runtime))
    || !['builtin', 'official', 'community'].includes(String(value.sourceKind))) return false
  const review = value.review
  return record(review) && ['unreviewed', 'reviewed'].includes(String(review.status))
    && typeof review.version === 'string' && (review.sha256 === null || typeof review.sha256 === 'string')
    && ['reviewer', 'reviewedAt', 'sourceRevision', 'notes'].every(key => review[key] === undefined || typeof review[key] === 'string')
}

export function isBuiltinPluginList(value: unknown): value is ZbaBuiltinPlugin[] | undefined {
  return value === undefined || (Array.isArray(value) && value.every(item => record(item)
    && typeof item.pluginId === 'string' && typeof item.version === 'string' && typeof item.enabled === 'boolean'
    && isPluginPresentation(item.metadata) && item.metadata.runtime === 'builtin' && item.metadata.sourceKind === 'builtin'))
}

/** Check the written calendar date; Date.parse alone normalizes invalid days. */
export function isReviewTimestamp(value: string): boolean {
  // (?![\s\S]) requires the absolute end, including no final line terminator.
  const timestamp = /^(\d{4})-(0[1-9]|1[0-2])-(0[1-9]|[12]\d|3[01])T(?:[01]\d|2[0-3]):[0-5]\d:[0-5]\d(?:\.\d+)?(?:Z|[+-](?:[01]\d|2[0-3]):[0-5]\d)(?![\s\S])/.exec(value)
  if (!timestamp) return false
  const year = Number(timestamp[1])
  const month = Number(timestamp[2])
  const day = Number(timestamp[3])
  const leapYear = year % 4 === 0 && (year % 100 !== 0 || year % 400 === 0)
  const daysInMonth = [31, leapYear ? 29 : 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31]
  return year > 0 && day <= (daysInMonth[month - 1] ?? 0) && Number.isFinite(Date.parse(value))
}

/** Never turn presentation claims into an audit badge without exact artifact provenance. */
export function hasMatchingReview(metadata: PluginPresentationMetadata | undefined, version: string, sha256?: string): boolean {
  const review = metadata?.review
  return Boolean(metadata && metadata.runtime !== 'builtin' && review?.status === 'reviewed'
    && review.version === version && sha256 && /^[a-f0-9]{64}(?![\s\S])/i.test(sha256)
    && review.sha256?.toLowerCase() === sha256.toLowerCase() && review.reviewer?.trim()
    && review.reviewedAt && isReviewTimestamp(review.reviewedAt)
    && review.sourceRevision && /^(?:[a-f0-9]{40}|[a-f0-9]{64})(?![\s\S])/i.test(review.sourceRevision))
}