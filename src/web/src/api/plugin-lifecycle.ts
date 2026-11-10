import { isPluginGovernance, isPluginGovernanceSource, type PluginGovernance, type PluginGovernanceSource } from './plugin-governance'
import type { ZbaPluginPackageManifest } from './zba'
import { isPluginPresentation, isReviewTimestamp, type PluginPresentationMetadata } from './plugin-presentation'
import { matchesLifecycleChanges, sameLifecycleManifest, type PluginManifestChange } from './plugin-lifecycle-diff'

export type PluginLifecycleKind = 'update' | 'rollback' | 'uninstall'
export type PluginLifecycleCandidate =
  | { origin: 'repository'; discoveryId: string; version: string; manifest?: never }
  | { origin: 'catalog' | 'json'; manifest: ZbaPluginPackageManifest; discoveryId?: never; version?: never }
export type PluginLifecycleSelection =
  | { kind: 'update'; pluginId: string; candidate: PluginLifecycleCandidate }
  | { kind: 'rollback' | 'uninstall'; pluginId: string; candidate?: never }
export type PluginLifecyclePreviewRequest = PluginLifecycleSelection & { expectedRevision: string }
export interface PluginLifecyclePreview {
  currentGovernance?: PluginGovernance
  targetGovernance?: PluginGovernance | null
  governanceSource?: PluginGovernanceSource
  previewId: string; expiresAt: string; revision: string; kind: PluginLifecycleKind; pluginId: string
  recovery: 'none' | 'failed-pending' | 'failed-install'
  pendingManifest: ZbaPluginPackageManifest | null
  currentManifest: ZbaPluginPackageManifest; targetManifest: ZbaPluginPackageManifest | null
  targetSource: { origin: 'repository' | 'catalog' | 'json' | 'installed'; url: string | null; revision: string | null } | null
  metadata: PluginPresentationMetadata | null
  enabled: boolean; currentStatus: string; restartRequired: boolean; canApply: boolean
  changes: PluginManifestChange[]
  checks: Array<{ code: string; status: 'passed' | 'pending' | 'warning' | 'blocked'; message: string }>
  preservation: { settings: true; userData: true; enabledIntent: true }
  trustNotice: string
}
const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)
const text = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0
const nullableText = (value: unknown): boolean => value === null || text(value)
const oneOf = (value: unknown, values: readonly string[]): value is string => typeof value === 'string' && values.includes(value)
function source(value: unknown): boolean {
  return value === null || (record(value) && oneOf(value.origin, ['catalog', 'repository', 'json', 'installed'])
    && nullableText(value.url) && nullableText(value.revision))
}
function metadata(value: unknown, origin: string | null): boolean {
  if (value === null) return true
  return isPluginPresentation(value) && value.runtime !== 'builtin' && value.sourceKind !== 'builtin'
    && (!['repository', 'json'].includes(origin ?? '') || (value.sourceKind === 'community' && value.review.status === 'unreviewed'))
}
function fields(value: unknown, isManifest: (value: unknown) => value is ZbaPluginPackageManifest): value is PluginLifecyclePreview {
  if (!record(value) || !text(value.previewId) || !text(value.expiresAt) || !isReviewTimestamp(value.expiresAt)
    || !oneOf(value.recovery, ['none', 'failed-pending', 'failed-install'])
    || !text(value.revision) || !text(value.pluginId) || !oneOf(value.kind, ['update', 'rollback', 'uninstall'])
    || !isManifest(value.currentManifest) || !(value.targetManifest === null || isManifest(value.targetManifest))
    || !(value.pendingManifest === null || isManifest(value.pendingManifest))
    || !source(value.targetSource) || !metadata(value.metadata, record(value.targetSource) ? String(value.targetSource.origin) : null)
    || typeof value.enabled !== 'boolean' || !text(value.currentStatus) || typeof value.restartRequired !== 'boolean' || typeof value.canApply !== 'boolean'
    || !record(value.preservation) || value.preservation.settings !== true || value.preservation.userData !== true || value.preservation.enabledIntent !== true
    || !text(value.trustNotice) || !Array.isArray(value.changes) || value.changes.length > 32 || !Array.isArray(value.checks) || value.checks.length > 1024) return false
  return value.changes.every(change => record(change) && text(change.field) && nullableText(change.before) && nullableText(change.after))
    && value.checks.every(check => record(check) && text(check.code) && typeof check.message === 'string' && oneOf(check.status, ['passed', 'pending', 'warning', 'blocked']))
    && (!value.canApply || !value.checks.some(check => check.status === 'blocked'))
    && (value.currentGovernance === undefined || isPluginGovernance(value.currentGovernance))
    && (value.targetGovernance === undefined || value.targetGovernance === null || isPluginGovernance(value.targetGovernance))
    && (value.governanceSource === undefined || isPluginGovernanceSource(value.governanceSource))
    && (!value.canApply || value.targetGovernance?.blocked !== true)
}
export function isPluginLifecyclePreview(value: unknown, isManifest: (value: unknown) => value is ZbaPluginPackageManifest): value is PluginLifecyclePreview {
  if (!fields(value, isManifest) || value.currentManifest.id !== value.pluginId || (value.targetManifest && value.targetManifest.id !== value.pluginId)) return false
  if (value.pendingManifest && value.pendingManifest.id !== value.pluginId) return false
  if (value.targetManifest === null && value.metadata !== null) return false
  if (value.recovery === 'failed-install' && (value.kind !== 'uninstall' || !value.pendingManifest
    || !sameLifecycleManifest(value.currentManifest, value.pendingManifest))) return false
  if (value.kind === 'rollback' && value.recovery === 'none' && value.targetManifest && sameLifecycleManifest(value.currentManifest, value.targetManifest)) return false
  if (value.recovery === 'failed-pending' && (value.kind !== 'rollback' || !value.pendingManifest || !value.targetManifest || !sameLifecycleManifest(value.currentManifest, value.targetManifest))) return false
  if (value.kind === 'uninstall') {
    if (value.targetManifest !== null || value.targetSource !== null) return false
  } else if (!value.targetManifest) {
    // Missing Previous is a useful blocked rollback preview, never permission to apply.
    if (value.kind !== 'rollback' || value.canApply || value.targetSource !== null) return false
  } else if (!value.targetSource || (value.kind === 'rollback') !== (value.targetSource.origin === 'installed')) return false
  return matchesLifecycleChanges(value.changes, value.currentManifest, value.targetManifest)
}
export function matchesLifecycleSelection(value: PluginLifecyclePreview, selection: PluginLifecyclePreviewRequest): boolean {
  if (value.kind !== selection.kind || value.pluginId !== selection.pluginId || value.revision !== selection.expectedRevision) return false
  if (selection.kind !== 'update') return true
  const candidate = selection.candidate
  return value.targetSource?.origin === candidate.origin && value.targetManifest !== null
    && (candidate.origin === 'repository' ? value.targetManifest.version === candidate.version : sameLifecycleManifest(value.targetManifest, candidate.manifest))
}