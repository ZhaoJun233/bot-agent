import { hasMatchingReview, type PluginPresentationMetadata } from '../../api/plugin-presentation'
import type { PluginPackageManifest } from './plugin-manifest'

/** Package identity and registry identity must never share a fabricated manifest. */
interface CatalogPresentation {
  id: string
  name: string
  summary: string
  category: string
  author?: string
  tags?: string[]
  repository?: string
  metadata?: PluginPresentationMetadata
  enabled?: boolean
  installedVersion?: string
  pendingVersion?: string
  pendingUninstall?: boolean
  restartRequired?: boolean
  status?: string
}
export interface PackageCatalogEntry extends CatalogPresentation {
  kind?: 'package'
  manifest: PluginPackageManifest
}
export interface BuiltinCatalogEntry extends CatalogPresentation {
  kind: 'builtin'
  version: string
  manifest?: never
}
export type PluginCatalogEntry = PackageCatalogEntry | BuiltinCatalogEntry
export interface CatalogFilters {
  source?: string
  runtime?: string
  review?: string
  state?: string
}
export function pluginVersion(entry: PluginCatalogEntry): string {
  return entry.kind === 'builtin' ? entry.version : entry.manifest.version
}
export function pluginSource(entry: PluginCatalogEntry): string {
  return entry.kind === 'builtin' ? 'builtin' : entry.metadata?.sourceKind === 'official' ? 'official' : 'community'
}
export function pluginRuntime(entry: PluginCatalogEntry): string {
  return entry.kind === 'builtin' ? 'builtin' : entry.metadata?.runtime === 'python' ? 'python' : 'dotnet'
}
export function pluginReviewed(entry: PluginCatalogEntry): boolean {
  return entry.kind !== 'builtin' && hasMatchingReview(entry.metadata, entry.manifest.version, entry.manifest.sha256)
}
export function pluginReviewLabel(entry: PluginCatalogEntry): string {
  if (entry.kind === 'builtin') return '随宿主提供 · 未单独审计'
  return pluginReviewed(entry) ? '已审核此版本与 ZIP' : '未审核'
}
export function pluginSourceLabel(entry: PluginCatalogEntry): string {
  return entry.kind === 'builtin' ? '内置能力' : pluginSource(entry) === 'official' ? '官方发布' : '社区来源'
}
export function filterPluginCatalog(entries: PluginCatalogEntry[], search: string, category: string, filters: CatalogFilters = {}): PluginCatalogEntry[] {
  const term = search.trim().toLocaleLowerCase()
  const match = (filter: string | undefined, value: string) => !filter || filter === 'all' || filter === value
  return entries.filter(entry => {
    const source = filters.source === 'package' ? entry.kind !== 'builtin' : match(filters.source, pluginSource(entry))
    const state = entry.pendingVersion || entry.pendingUninstall || entry.restartRequired ? 'pending'
      : entry.kind === 'builtin' || entry.installedVersion ? 'installed' : 'available'
    const stateMatches = filters.state === 'enabled' ? entry.enabled === true
      : filters.state === 'disabled' ? (entry.kind === 'builtin' || Boolean(entry.installedVersion)) && entry.enabled === false
        : match(filters.state, state)
    return match(category, entry.category) && source && stateMatches
      && match(filters.runtime, pluginRuntime(entry))
      && match(filters.review, pluginReviewed(entry) ? 'reviewed' : 'unreviewed')
      && (!term || [entry.id, entry.name, entry.summary, entry.category, entry.author ?? '', entry.metadata?.description ?? '', entry.metadata?.usage ?? '', ...(entry.tags ?? [])]
        .some(value => value.toLocaleLowerCase().includes(term)))
  })
}
export function safeGithubLink(raw?: string): string | null {
  if (!raw) return null
  try {
    const url = new URL(raw)
    if (url.protocol !== 'https:' || url.hostname !== 'github.com' || url.username || url.password || url.port || url.search || url.hash) return null
    const parts = url.pathname.split('/').filter(Boolean)
    return parts.length === 2 && parts.every((part) => /^[a-zA-Z0-9_.-]+$/.test(part)) ? url.href : null
  } catch { return null }
}
export function pluginPackageStatus(entry: PluginCatalogEntry): string {
  if (entry.kind === 'builtin') return entry.enabled ? '已内置 · 已启用' : '已内置 · 已停用'
  const waiting = entry.restartRequired ? '需重启宿主' : '等待热应用'
  if (entry.pendingUninstall) return '待卸载 · ' + waiting
  if (entry.pendingVersion) return '已暂存 ' + entry.pendingVersion + ' · ' + waiting
  if (entry.status === 'failed') return '操作失败 · 请核对宿主状态'
  if (entry.installedVersion) return '已安装 ' + entry.installedVersion + (entry.enabled === undefined ? '' : entry.enabled ? ' · 已启用' : ' · 已停用') + (entry.restartRequired ? ' · 需重启宿主' : '')
  return '未安装'
}
