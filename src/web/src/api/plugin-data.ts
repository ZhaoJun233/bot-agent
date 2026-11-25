export interface PluginReceiptContract { contract: string; version: string }
export interface PluginPackageData {
  tables: string[]; statePrefixes: string[]; usage?: boolean; audit?: boolean; receipt?: PluginReceiptContract | null
}
const record = (v: unknown): v is Record<string, unknown> => typeof v === 'object' && v !== null && !Array.isArray(v)
export function hasValidPluginData(manifest: Record<string, unknown>): boolean {
  if ('control' in manifest && (manifest.apiVersion !== 1 || typeof manifest.control !== 'boolean')) return false
  if ('data' in manifest && manifest.apiVersion === 2) return false
  const d = manifest.data
  if (d === undefined || d === null) return true
  if (manifest.apiVersion !== 1 || !record(d) || Object.keys(d).some(k => !['tables', 'statePrefixes', 'usage', 'audit', 'receipt'].includes(k))
    || !Array.isArray(d.tables) || d.tables.length > 32 || !Array.isArray(d.statePrefixes) || d.statePrefixes.length > 32
    || !d.tables.every(t => typeof t === 'string' && /^[a-z][a-z0-9_]{0,79}$/.test(t) && !t.startsWith('sqlite_') && !['meta', 'token_usage'].includes(t))
    || new Set(d.tables).size !== d.tables.length
    || !d.statePrefixes.every(p => typeof p === 'string' && /^[a-z][a-z0-9:.-]{0,118}:$/.test(p)
      && !p.startsWith('operation-recovery:') && !'operation-recovery:v1:'.startsWith(p))
    || new Set(d.statePrefixes).size !== d.statePrefixes.length
    || d.usage !== undefined && typeof d.usage !== 'boolean' || d.audit !== undefined && typeof d.audit !== 'boolean') return false
  return d.receipt === undefined || d.receipt === null || record(d.receipt)
    && Object.keys(d.receipt).sort().join(',') === 'contract,version'
    && typeof d.receipt.contract === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,119}$/.test(d.receipt.contract)
    && typeof d.receipt.version === 'string' && /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/.test(d.receipt.version)
}
export function canonicalPluginData(data: PluginPackageData) {
  return { tables: [...data.tables].sort(), statePrefixes: [...data.statePrefixes].sort(),
    usage: data.usage ?? false, audit: data.audit ?? false,
    receipt: data.receipt ? { contract: data.receipt.contract, version: data.receipt.version } : null }
}
