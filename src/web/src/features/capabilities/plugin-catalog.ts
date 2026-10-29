import type { PluginPackageManifest } from './plugin-manifest'

/** View model: catalog transport is normalized once, outside rendering. */
export interface PluginCatalogEntry {
  id: string
  name: string
  summary: string
  category: string
  author?: string
  tags?: string[]
  repository?: string
  manifest: PluginPackageManifest
  installedVersion?: string
  pendingVersion?: string
  pendingUninstall?: boolean
}
export function filterPluginCatalog(entries: PluginCatalogEntry[], search: string, category: string): PluginCatalogEntry[] {
  const term = search.trim().toLocaleLowerCase()
  return entries.filter((entry) => (category === 'all' || category === entry.category)
    && (!term || [entry.id, entry.name, entry.summary, entry.category, entry.author ?? '', ...(entry.tags ?? [])].some((value) => value.toLocaleLowerCase().includes(term))))
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
  if (entry.pendingUninstall) return '待卸载 · 需重启'
  if (entry.pendingVersion) return '已暂存 ' + entry.pendingVersion + ' · 需重启'
  if (entry.installedVersion) return '已安装 ' + entry.installedVersion
  return '未安装'
}
