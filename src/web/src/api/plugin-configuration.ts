export interface PluginConfigurationBinding {
  key: string; legacyFieldId?: number | null; readOnly?: boolean; trim?: boolean
}
export interface PluginConfigurationDeclaration { apiVersion: number; bindings: PluginConfigurationBinding[] }

const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object'
  && value !== null && !Array.isArray(value)

/** Validate the declaration before it participates in preview equivalence or field ownership. */
export function hasValidPluginConfiguration(manifest: Record<string, unknown>): boolean {
  if ('configuration' in manifest && manifest.apiVersion !== 1) return false
  const definition = manifest.configuration
  if (definition === undefined || definition === null) return true
  if (!record(definition) || Object.keys(definition).some(key => !['apiVersion', 'bindings'].includes(key))
    || definition.apiVersion !== 1 || !Array.isArray(definition.bindings)
    || definition.bindings.length < 1 || definition.bindings.length > 128) return false
  if (manifest.settings != null && !Array.isArray(manifest.settings)) return false
  const fields = new Map<string, Record<string, unknown>>()
  for (const field of manifest.settings ?? []) {
    if (!record(field) || typeof field.key !== 'string' || fields.has(field.key)) return false
    fields.set(field.key, field)
  }
  const keys = new Set<string>(); const identities = new Set<number>()
  for (const binding of definition.bindings) {
    if (!record(binding) || Object.keys(binding).some(key => !['key', 'legacyFieldId', 'readOnly', 'trim'].includes(key))
      || typeof binding.key !== 'string' || !/^[a-zA-Z][a-zA-Z0-9_.-]{0,79}$/.test(binding.key)
      || keys.has(binding.key) || binding.readOnly !== undefined && typeof binding.readOnly !== 'boolean'
      || binding.trim !== undefined && typeof binding.trim !== 'boolean') return false
    keys.add(binding.key)
    if (binding.readOnly === true) {
      if (binding.legacyFieldId != null || binding.trim === true || fields.has(binding.key)) return false
    } else {
      const id = binding.legacyFieldId; const field = fields.get(binding.key)
      if (typeof id !== 'number' || !Number.isSafeInteger(id) || id < 0 || identities.has(id)
        || !field || !['string', 'boolean', 'number'].includes(String(field.type))
        || binding.trim === true && field.type !== 'string') return false
      identities.add(id); fields.delete(binding.key)
    }
  }
  return fields.size === 0
}

export function canonicalPluginConfiguration(definition: PluginConfigurationDeclaration) {
  return { apiVersion: definition.apiVersion, bindings: definition.bindings.map(binding => ({
    key: binding.key, legacyFieldId: binding.legacyFieldId ?? null, readOnly: binding.readOnly ?? false,
    trim: binding.trim ?? false
  })).sort((left, right) => left.key < right.key ? -1 : left.key > right.key ? 1 : 0) }
}
