import type { ZbaPluginPackageManifest } from './zba'
import { canonicalPluginData } from './plugin-data'
import { canonicalSettingNumberConstraints } from './plugin-setting-numbers'
import { canonicalPluginConfiguration } from './plugin-configuration'
import { canonicalPluginUi } from './plugin-ui'

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
    ...(manifest.control === true ? { control: true } : {}),
    ...(manifest.toolApiVersion != null ? { toolApiVersion: manifest.toolApiVersion } : {}),
    ...(manifest.configuration ? { configuration: canonicalPluginConfiguration(manifest.configuration) } : {}),
    ...(manifest.ui ? { ui: canonicalPluginUi(manifest.ui) } : {}),
    ...(manifest.adoption ? { adoption: manifest.adoption } : {}),
    ...(manifest.exclusiveResources?.length ? { exclusiveResources: [...manifest.exclusiveResources].sort() } : {}),
    ...(manifest.data ? { data: canonicalPluginData(manifest.data) } : {}),
    ...(manifest.sharedContracts?.length ? { sharedContracts: manifest.sharedContracts.map(c =>
      ({ name: c.name, version: c.version, sha256: c.sha256.toLowerCase(), owner: c.owner }))
      .sort((a, b) => a.name < b.name ? -1 : a.name > b.name ? 1 : 0) } : {}),
    permissions: manifest.permissions ?? [],
    dependencies: (manifest.dependencies ?? []).map(dependency => ({ id: dependency.id, version: dependency.version })),
    settings: (manifest.settings ?? []).map(setting => ({ key: setting.key, label: setting.label, type: setting.type,
      defaultValue: setting.defaultValue ?? null, required: setting.required ?? false, choices: setting.choices ?? [],
      ...(setting.numberConstraints ? { numberConstraints: canonicalSettingNumberConstraints(setting.numberConstraints) } : {}) }))
  }
}
