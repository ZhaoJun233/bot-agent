export interface PluginUiAsset {
  path: string
  sha256: string
  contentType: string
}

export interface PluginUiDeclaration {
  contractVersion: number
  entry: string
  assets: PluginUiAsset[]
  allowedActions?: string[] | null
  kind?: string | null
  title?: string | null
}

export interface ZbaPluginUiSession {
  sessionId: string
  pluginId: string
  version: string
  releaseSha256: string
  generationId: string
  contractVersion: number
  entryUrl: string
  allowedActions: string[]
  backendReady: boolean
  versionMatched: boolean
  adoptionMode: 'cold' | 'online'
  kind: string
  title: string
  expiresAt: string
}

export interface ZbaPluginUiSessionClosed {
  sessionId: string
  pluginId: string
  closed: boolean
}

export interface ZbaPluginBundlePreview {
  previewId: string
  expiresAt: string
  revision: string
  canApply: boolean
  adoptionMode: 'cold' | 'online'
  affectedIds: string[]
  checks: Array<{ code: string; status: string; message: string }>
}

const ALLOWED_CONTENT_TYPES: Record<string, string> = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.json': 'application/json; charset=utf-8',
}
const TOKEN_PATTERN = /^[a-z][a-z0-9._-]{0,79}$/
const SHA256_PATTERN = /^[a-fA-F0-9]{64}$/
const compareOrdinal = (a: string, b: string): number => (a < b ? -1 : a > b ? 1 : 0)

function isRecord(v: unknown): v is Record<string, unknown> {
  return typeof v === 'object' && v !== null && !Array.isArray(v)
}

function isSafeRelativeWebPath(path: unknown): path is string {
  if (typeof path !== 'string' || !path.startsWith('web/') || path.includes('\\') || path.includes('\0') || path.includes('//')) return false
  const segments = path.split('/')
  if (segments.some(seg => seg.length === 0 || seg === '.' || seg === '..')) return false
  return true
}

function fileExtension(path: string): string {
  const slash = path.lastIndexOf('/')
  const dot = path.lastIndexOf('.')
  return dot > slash ? path.slice(dot).toLowerCase() : ''
}

export function isSafePluginUiEntryUrl(url: unknown, pluginId: string, releaseSha256: string): url is string {
  if (typeof url !== 'string' || !pluginId || !SHA256_PATTERN.test(releaseSha256)) return false
  if (url.includes('\\') || url.includes('?') || url.includes('#') || url.includes('//') || url.includes('\0')) return false
  const prefix = `/zba/v1/plugins/${encodeURIComponent(pluginId)}/ui/${releaseSha256.toLowerCase()}/`
  if (!url.startsWith(prefix)) return false
  const rest = url.slice(prefix.length)
  const slash = rest.indexOf('/')
  if (slash <= 0) return false
  const ticket = rest.slice(0, slash)
  const assetPath = rest.slice(slash + 1)
  if (!/^[A-Za-z0-9._-]{1,200}$/.test(ticket) || ticket.startsWith('.') || ticket.endsWith('.') || ticket.includes('..')) return false
  return isSafeRelativeWebPath(assetPath) && assetPath.toLowerCase().endsWith('.html')
}

export function hasValidPluginUi(manifest: Record<string, unknown>): boolean {
  if (manifest.adoption !== undefined && manifest.adoption !== null) {
    if (manifest.adoption !== 'cold' && manifest.adoption !== 'online') return false
  }
  if (manifest.exclusiveResources !== undefined && manifest.exclusiveResources !== null) {
    if (!Array.isArray(manifest.exclusiveResources) || manifest.exclusiveResources.length > 32) return false
    const seenResources = new Set<string>()
    for (const item of manifest.exclusiveResources) {
      if (typeof item !== 'string' || !TOKEN_PATTERN.test(item) || seenResources.has(item)) return false
      seenResources.add(item)
    }
    if (manifest.exclusiveResources.length > 0 && manifest.adoption === 'online') return false
  }
  if (manifest.ui === undefined || manifest.ui === null) return true
  if (manifest.apiVersion !== 1 || !isRecord(manifest.ui)) return false
  const ui = manifest.ui
  if (typeof ui.contractVersion !== 'number' || !Number.isInteger(ui.contractVersion) || ui.contractVersion !== 1) return false
  if (!isSafeRelativeWebPath(ui.entry) || !ui.entry.toLowerCase().endsWith('.html')) return false
  if (!Array.isArray(ui.assets) || ui.assets.length === 0 || ui.assets.length > 64) return false
  const seenAssets = new Set<string>()
  let entryFound = false
  for (const asset of ui.assets) {
    if (!isRecord(asset)
      || !isSafeRelativeWebPath(asset.path)
      || typeof asset.sha256 !== 'string' || !SHA256_PATTERN.test(asset.sha256)
      || typeof asset.contentType !== 'string' || asset.contentType.length === 0) return false
    const loweredPath = asset.path.toLowerCase()
    if (seenAssets.has(loweredPath)) return false
    seenAssets.add(loweredPath)
    const expectedType = ALLOWED_CONTENT_TYPES[fileExtension(asset.path)]
    if (!expectedType || asset.contentType !== expectedType) return false
    if (asset.path === ui.entry) entryFound = true
  }
  if (!entryFound) return false
  if (ui.allowedActions !== undefined && ui.allowedActions !== null) {
    if (!Array.isArray(ui.allowedActions) || ui.allowedActions.length > 32) return false
    const seenActions = new Set<string>()
    for (const action of ui.allowedActions) {
      if (typeof action !== 'string' || !TOKEN_PATTERN.test(action) || seenActions.has(action)) return false
      if (action.startsWith('quotas.') && manifest.id !== 'preset.feature.token-quota') return false
      seenActions.add(action)
    }
  }
  if (ui.kind !== undefined && ui.kind !== null && ui.kind !== 'dialog' && ui.kind !== 'panel' && ui.kind !== 'page') return false
  if (ui.title !== undefined && ui.title !== null) {
    if (typeof ui.title !== 'string' || ui.title.trim().length === 0 || ui.title.length > 120 || /[\u0000-\u001f\u007f]/.test(ui.title)) return false
  }
  return true
}

export function canonicalPluginUi(ui: PluginUiDeclaration) {
  return {
    contractVersion: ui.contractVersion,
    entry: ui.entry,
    assets: [...ui.assets]
      .map(asset => ({ path: asset.path, sha256: asset.sha256.toLowerCase(), contentType: asset.contentType }))
      .sort((a, b) => compareOrdinal(a.path, b.path)),
    allowedActions: ui.allowedActions?.length ? [...ui.allowedActions].sort(compareOrdinal) : null,
    kind: ui.kind ?? null,
    title: ui.title ?? null,
  }
}

export function isPluginUiSession(value: unknown): value is ZbaPluginUiSession {
  return isRecord(value)
    && typeof value.sessionId === 'string' && value.sessionId.length > 0
    && typeof value.pluginId === 'string' && value.pluginId.length > 0
    && typeof value.version === 'string' && value.version.length > 0
    && typeof value.releaseSha256 === 'string' && SHA256_PATTERN.test(value.releaseSha256)
    && typeof value.generationId === 'string' && value.generationId.length > 0
    && typeof value.contractVersion === 'number' && Number.isInteger(value.contractVersion)
    && isSafePluginUiEntryUrl(value.entryUrl, value.pluginId, value.releaseSha256)
    && Array.isArray(value.allowedActions) && value.allowedActions.every(a => typeof a === 'string' && a.length > 0)
    && typeof value.backendReady === 'boolean'
    && typeof value.versionMatched === 'boolean'
    && (value.adoptionMode === 'cold' || value.adoptionMode === 'online')
    && typeof value.kind === 'string'
    && typeof value.title === 'string'
    && typeof value.expiresAt === 'string'
}

export function isPluginUiSessionClosed(value: unknown): value is ZbaPluginUiSessionClosed {
  return isRecord(value) && typeof value.sessionId === 'string' && typeof value.pluginId === 'string' && typeof value.closed === 'boolean'
}

export function isPluginBundlePreview(value: unknown): value is ZbaPluginBundlePreview {
  return isRecord(value)
    && typeof value.previewId === 'string'
    && typeof value.expiresAt === 'string'
    && typeof value.revision === 'string'
    && typeof value.canApply === 'boolean'
    && (value.adoptionMode === 'cold' || value.adoptionMode === 'online')
    && Array.isArray(value.affectedIds)
    && Array.isArray(value.checks)
}
