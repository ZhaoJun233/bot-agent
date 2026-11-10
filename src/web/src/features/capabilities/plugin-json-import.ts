import { isPluginManifest, type PluginPackageManifest } from './plugin-manifest'
const manifestFields = new Set(['id', 'version', 'targetFramework', 'entryAssembly', 'entryType', 'apiVersion', 'downloadUrl', 'sha256', 'minimumHostVersion', 'permissions', 'settings', 'dependencies', 'runtime'])
const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)
function pascalObject(value: unknown, fields: string[]): Record<string, unknown> {
  if (!record(value)) throw new Error('技术清单对象格式无效。')
  const mapped: Record<string, unknown> = {}
  for (const [key, item] of Object.entries(value)) {
    const name = key[0]?.toLowerCase() + key.slice(1)
    if (!fields.includes(name) || key !== name[0]?.toUpperCase() + name.slice(1)) throw new Error('技术清单含未知或混用大小写的字段。')
    mapped[name] = item
  }
  return mapped
}
/** Technical v2 Runtime is explicit; presentation/review/trust claims remain forbidden. */
export function parsePluginJson(input: string): PluginPackageManifest {
  if (new TextEncoder().encode(input).length > 2 * 1024 * 1024) throw new Error('技术清单超过 2 MiB 限制。')
  let value: unknown
  try { value = JSON.parse(input) } catch { throw new Error('请输入有效的 JSON 技术清单。') }
  if (record(value) && 'Id' in value) {
    const mapped = pascalObject(value, [...manifestFields])
    if (mapped.runtime !== undefined) mapped.runtime = pascalObject(mapped.runtime, ['kind', 'protocolVersion', 'entryPoint', 'minimumPythonVersion', 'dependencyMode', 'tools'])
    if (Array.isArray(mapped.dependencies)) mapped.dependencies = mapped.dependencies.map(item => pascalObject(item, ['id', 'version']))
    if (Array.isArray(mapped.settings)) mapped.settings = mapped.settings.map(item => pascalObject(item, ['key', 'label', 'type', 'defaultValue', 'required', 'choices']))
    value = mapped
  }
  if (record(value)) {
    const fields = value
    if (fields.apiVersion === 2 && ['entryAssembly', 'entryType', 'targetFramework'].some(key => key in fields)) throw new Error('Python v2 清单不能包含 .NET 入口字段。')
    if (value.apiVersion !== 2 && 'runtime' in value) throw new Error('清单含非技术字段：.NET v1 清单不能指定 runtime。')
    value = { ...value, apiVersion: value.apiVersion === undefined ? 1 : value.apiVersion,
      ...(value.apiVersion === 2 ? {} : { targetFramework: value.targetFramework ?? 'net10.0' }) }
  }
  if (!isPluginManifest(value) || Object.keys(value).some(key => !manifestFields.has(key))
    || (value.dependencies !== undefined && value.dependencies !== null && (!Array.isArray(value.dependencies) || !value.dependencies.every(dependency =>
      dependency && typeof dependency === 'object' && typeof dependency.id === 'string'
      && typeof dependency.version === 'string')))) {
    throw new Error('清单字段不完整或含非技术字段；不能指定来源、审核、预览 ID 或信任状态。')
  }
  return value
}
