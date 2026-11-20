export interface PluginSharedContract { name: string; version: string; sha256: string; owner: string }

export function hasValidSharedContracts(manifest: Record<string, unknown>): boolean {
  const contracts = manifest.sharedContracts
  if ('sharedContracts' in manifest && manifest.apiVersion === 2) return false
  if (contracts === undefined || contracts === null) return true
  if (manifest.apiVersion !== 1 || !Array.isArray(contracts) || contracts.length > 32) return false
  const names = new Set<string>()
  return contracts.every((value: unknown) => {
    if (typeof value !== 'object' || value === null || Array.isArray(value)) return false
    const c = value as Record<string, unknown>
    if (Object.keys(c).sort().join(',') !== 'name,owner,sha256,version'
      || typeof c.name !== 'string' || !/^[A-Za-z][A-Za-z0-9_.-]{0,119}$/.test(c.name)
      || ['botagent.core', 'botagent.plugin.contracts'].includes(c.name.toLowerCase())
      || typeof manifest.entryAssembly === 'string' && manifest.entryAssembly.toLowerCase() === c.name.toLowerCase() + '.dll'
      || typeof c.version !== 'string' || !/^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)$/.test(c.version)
      || c.version.split('.').some(part => Number(part) > 65534)
      || typeof c.sha256 !== 'string' || !/^[a-fA-F0-9]{64}$/.test(c.sha256)
      || typeof c.owner !== 'string' || names.has(c.name.toLowerCase())) return false
    // Host contract identity is checked against the server's compiled authority during
    // preview/staging. This parser can check only the reserved owner syntax.
    const hostOwner = /^(?:preset|system)\.[a-z0-9.-]+$/.test(c.owner) && c.owner.length <= 80
      && !c.owner.includes('..') && !c.owner.endsWith('.')
    if (c.owner !== manifest.id && !hostOwner && !(Array.isArray(manifest.dependencies) && manifest.dependencies.some(d =>
      typeof d === 'object' && d !== null && 'id' in d && d.id === c.owner))) return false
    names.add(c.name.toLowerCase())
    return true
  })
}
