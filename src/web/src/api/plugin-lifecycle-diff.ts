import type { ZbaPluginPackageManifest } from './zba'
import { canonicalManifest } from './plugin-manifest-values'

export interface PluginManifestChange { field: string; before: string | null; after: string | null }
const compare = (left: string, right: string): number => left < right ? -1 : left > right ? 1 : 0
function canonicalUrl(value: string): string {
  try { return new URL(value).href } catch { return value }
}
export function lifecycleManifest(manifest: ZbaPluginPackageManifest) {
  const value = canonicalManifest(manifest)
  return { ...value, downloadUrl: canonicalUrl(value.downloadUrl), permissions: [...value.permissions].sort(compare),
    dependencies: value.dependencies.sort((a, b) => compare(a.id, b.id)),
    settings: value.settings.sort((a, b) => compare(a.key, b.key)) }
}
export function sameLifecycleManifest(left: ZbaPluginPackageManifest, right: ZbaPluginPackageManifest): boolean {
  return JSON.stringify(lifecycleManifest(left)) === JSON.stringify(lifecycleManifest(right))
}
export function lifecycleChanges(current: ZbaPluginPackageManifest, target: ZbaPluginPackageManifest | null): PluginManifestChange[] {
  const before = lifecycleManifest(current)
  const after = target && lifecycleManifest(target)
  return (Object.keys(before) as Array<keyof typeof before>).sort(compare)
    .filter(field => !after || JSON.stringify(before[field]) !== JSON.stringify(after[field]))
    .map(field => ({ field, before: JSON.stringify(before[field]), after: after ? JSON.stringify(after[field]) : null }))
}
function canonicalJson(value: unknown): string {
  if (Array.isArray(value)) return '[' + value.map(canonicalJson).join(',') + ']'
  if (typeof value === 'object' && value !== null) return '{' + Object.keys(value).sort(compare)
    .map(key => JSON.stringify(key) + ':' + canonicalJson((value as Record<string, unknown>)[key])).join(',') + '}'
  return JSON.stringify(value)
}
/** JSON whitespace, escaping and property order are not technical changes. Missing or invented fields are. */
export function matchesLifecycleChanges(changes: PluginManifestChange[], current: ZbaPluginPackageManifest, target: ZbaPluginPackageManifest | null): boolean {
  const expected = lifecycleChanges(current, target)
  if (expected.length !== changes.length) return false
  const equivalent = (left: string | null, right: string | null): boolean => {
    if (left === null || right === null) return left === right
    try { return canonicalJson(JSON.parse(left)) === canonicalJson(JSON.parse(right)) } catch { return false }
  }
  return expected.every((item, index) => item.field === changes[index]?.field
    && equivalent(item.before, changes[index]!.before) && equivalent(item.after, changes[index]!.after))
}