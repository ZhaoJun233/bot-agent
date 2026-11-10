import { isPluginGovernance, isPluginGovernanceSource, type PluginGovernance, type PluginGovernanceSource } from './plugin-governance'
import type { ZbaPluginPackageManifest } from './zba'
import { isPluginPresentation, isReviewTimestamp, type PluginPresentationMetadata } from './plugin-presentation'

export interface PluginSourceCandidate { pluginId: string; version: string; metadata?: PluginPresentationMetadata }
export interface PluginSourceDiscovery {
  discoveryId: string; sourceUrl: string; sourceRevision: string; expiresAt: string; candidates: PluginSourceCandidate[]
}
export type PluginPreviewSelection =
  | { origin: 'repository'; discoveryId: string; pluginId: string; version: string; manifest?: never }
  | { origin: 'catalog' | 'json'; manifest: ZbaPluginPackageManifest; discoveryId?: never; pluginId?: never; version?: never }
export type PluginPreviewRequest = PluginPreviewSelection & { expectedRevision: string }
export interface PluginInstallPreview {
  governance?: PluginGovernance
  governanceSource?: PluginGovernanceSource
  previewId: string; expiresAt: string; revision: string; manifest: ZbaPluginPackageManifest
  source: { kind: 'repository' | 'catalog' | 'json'; url?: string; revision?: string }
  metadata?: PluginPresentationMetadata; installedVersion?: string
  enabledAfterInstall: false; canInstall: boolean
  checks: Array<{ code: string; status: 'passed' | 'pending' | 'warning' | 'blocked'; message: string }>
  trustNotice: string
}
const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)
const text = (value: unknown): value is string => typeof value === 'string' && value.trim().length > 0
const optionalText = (value: unknown): boolean => value === undefined || text(value)
const timestamp = (value: unknown): boolean => text(value) && isReviewTimestamp(value)
export function isImportMetadata(value: unknown, external: boolean): boolean {
  return isPluginPresentation(value) && (!external || (value.sourceKind === 'community' && value.runtime !== 'builtin' && value.review.status === 'unreviewed'))
}
export function isPluginSourceDiscovery(value: unknown): value is PluginSourceDiscovery {
  if (!record(value) || !text(value.discoveryId) || !text(value.sourceUrl) || !text(value.sourceRevision) || !timestamp(value.expiresAt)
    || !Array.isArray(value.candidates) || value.candidates.length < 1 || value.candidates.length > 1024) return false
  const identities = new Set<string>()
  return value.candidates.every(candidate => {
    if (!record(candidate) || !text(candidate.pluginId) || !text(candidate.version)
      || (candidate.metadata !== undefined && !isImportMetadata(candidate.metadata, true))) return false
    const identity = JSON.stringify([candidate.pluginId, candidate.version])
    if (identities.has(identity)) return false
    identities.add(identity)
    return true
  })
}
/** Reuse the established technical-manifest decoder instead of interpreting it twice. */
export function isPluginInstallPreview(value: unknown, isManifest: (value: unknown) => value is ZbaPluginPackageManifest): value is PluginInstallPreview {
  return record(value) && text(value.previewId) && timestamp(value.expiresAt) && text(value.revision) && isManifest(value.manifest)
    && record(value.source) && (value.source.kind === 'repository' || value.source.kind === 'catalog' || value.source.kind === 'json')
    && optionalText(value.source.url) && optionalText(value.source.revision)
    && (value.metadata === undefined || isImportMetadata(value.metadata, value.source.kind !== 'catalog')) && optionalText(value.installedVersion)
    && value.enabledAfterInstall === false && typeof value.canInstall === 'boolean'
    && Array.isArray(value.checks) && value.checks.length <= 1024
    && value.checks.every(check => record(check) && text(check.code) && typeof check.message === 'string'
      && ['passed', 'pending', 'warning', 'blocked'].some(status => status === check.status))
    && (!value.canInstall || !value.checks.some(check => check.status === 'blocked'))
    && typeof value.trustNotice === 'string'
    && (value.governance === undefined || isPluginGovernance(value.governance))
    && (value.governanceSource === undefined || isPluginGovernanceSource(value.governanceSource))
    && (!value.canInstall || value.governance?.blocked !== true)
}