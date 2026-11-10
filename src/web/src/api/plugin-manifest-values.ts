import type { ZbaPluginPackageManifest } from './zba'

/** Compare every technical field; only wire defaults and SHA letter case are equivalent. */
export function samePreviewManifest(left: ZbaPluginPackageManifest, right: ZbaPluginPackageManifest): boolean {
  return JSON.stringify(canonicalManifest(left)) === JSON.stringify(canonicalManifest(right))
}
export function canonicalManifest(manifest: ZbaPluginPackageManifest) {
  const runtime = manifest.runtime
  const target = manifest.apiVersion === 2 && runtime
    ? { runtime: { kind: runtime.kind, protocolVersion: runtime.protocolVersion, entryPoint: runtime.entryPoint,
      minimumPythonVersion: runtime.minimumPythonVersion, dependencyMode: runtime.dependencyMode, tools: [...runtime.tools] } }
    : { entryAssembly: manifest.entryAssembly, entryType: manifest.entryType, targetFramework: manifest.targetFramework ?? 'net10.0' }
  return {
    id: manifest.id, version: manifest.version, ...target,
    sha256: manifest.sha256.toLowerCase(), downloadUrl: manifest.downloadUrl,
    apiVersion: manifest.apiVersion ?? 1, minimumHostVersion: manifest.minimumHostVersion ?? null,
    permissions: manifest.permissions ?? [],
    dependencies: (manifest.dependencies ?? []).map(dependency => ({ id: dependency.id, version: dependency.version })),
    settings: (manifest.settings ?? []).map(setting => ({ key: setting.key, label: setting.label, type: setting.type,
      defaultValue: setting.defaultValue ?? null, required: setting.required ?? false, choices: setting.choices ?? [] }))
  }
}
