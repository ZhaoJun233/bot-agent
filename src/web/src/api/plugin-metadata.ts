/** Directory observations contain stable codes, never raw metadata or parser errors. */
export interface PluginPackageMetadataFailure {
  id: string
  code: string
  fingerprint: string
}

export function isPluginMetadataFailures(value: unknown): value is PluginPackageMetadataFailure[] | undefined {
  return value === undefined || Array.isArray(value) && value.every(item =>
    item !== null && typeof item === 'object'
    && typeof item.id === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9._-]{0,95}$/.test(item.id)
    && ['plugin_metadata_invalid_id', 'plugin_metadata_unreadable', 'plugin_metadata_too_large'].includes(item.code)
    && typeof item.fingerprint === 'string' && /^(sha256:|prefix:)[a-f0-9]{64}$|^unreadable$/.test(item.fingerprint))
}
