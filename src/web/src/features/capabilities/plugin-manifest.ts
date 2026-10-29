export const pluginSettingTypes = ['string', 'boolean', 'number', 'secret', 'select'] as const
export type PluginSettingType = typeof pluginSettingTypes[number]
export interface PluginSettingDefinition {
  key: string
  label: string
  type: PluginSettingType
  defaultValue?: string | null
  required?: boolean
  choices?: string[] | null
}
export interface PluginPackageManifest {
  id: string
  version: string
  entryAssembly: string
  entryType: string
  sha256: string
  downloadUrl: string
  apiVersion: number
  targetFramework: string
  minimumHostVersion?: string | null
  permissions?: string[] | null
  dependencies?: Array<{ id: string; version: string }> | null
  settings?: PluginSettingDefinition[] | null
}
function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
export function isPluginSettingDefinition(value: unknown): value is PluginSettingDefinition {
  return record(value) && typeof value.key === 'string' && /^[a-zA-Z][a-zA-Z0-9_.-]{0,79}$/.test(value.key)
    && typeof value.label === 'string' && pluginSettingTypes.some((kind) => kind === value.type)
    && (value.required === undefined || typeof value.required === 'boolean')
    && (value.defaultValue === undefined || value.defaultValue === null || typeof value.defaultValue === 'string')
    && (value.choices === undefined || value.choices === null || (Array.isArray(value.choices) && value.choices.every((item) => typeof item === 'string')))
}
export function isPluginManifest(value: unknown): value is PluginPackageManifest {
  return record(value) && typeof value.id === 'string' && typeof value.version === 'string'
    && typeof value.entryAssembly === 'string' && typeof value.entryType === 'string'
    && typeof value.sha256 === 'string' && /^[a-fA-F0-9]{64}$/.test(value.sha256)
    && typeof value.downloadUrl === 'string' && Number.isSafeInteger(value.apiVersion)
    && typeof value.targetFramework === 'string'
    && (value.minimumHostVersion === undefined || value.minimumHostVersion === null || typeof value.minimumHostVersion === 'string')
    && (value.permissions === undefined || value.permissions === null || (Array.isArray(value.permissions) && value.permissions.every((item) => typeof item === 'string')))
    && (value.settings === undefined || value.settings === null || (Array.isArray(value.settings) && value.settings.every(isPluginSettingDefinition)
      && new Set(value.settings.map((item) => item.key)).size === value.settings.length))
}

/** Validate locally, but never fetch this URL in the browser or append the ZBA token. */
export function githubSourceError(raw: string): string | null {
  if (!raw.trim()) return null
  try {
    const url = new URL(raw.trim())
    if (url.protocol !== 'https:' || url.hostname !== 'github.com' || url.port || url.username || url.password || url.search || url.hash) return '仅接受公开 GitHub 仓库的 HTTPS Release 地址，不允许凭据或参数。'
    const parts = url.pathname.split('/').filter(Boolean)
    if (parts.length < 2 || !parts.slice(0, 2).every((part) => /^[a-zA-Z0-9_.-]+$/.test(part))) return '请填写完整的 GitHub 仓库或固定版本 Release 地址。'
    if (parts.length === 2) return null
    if (parts.length === 5 && parts[2] === 'releases' && parts[3] === 'tag' && parts[4] !== 'latest') return null
    return '请填写仓库主页或 /releases/tag/固定版本，不支持任意下载地址。'
  } catch { return '请填写完整的 GitHub HTTPS 地址。' }
}
