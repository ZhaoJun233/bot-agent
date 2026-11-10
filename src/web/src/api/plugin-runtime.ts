export interface PythonPluginRuntime {
  kind: 'python'
  protocolVersion: 1
  entryPoint: string
  minimumPythonVersion: string
  dependencyMode: 'stdlib-only'
  tools: string[]
}

const runtimeFields = ['kind', 'protocolVersion', 'entryPoint', 'minimumPythonVersion', 'dependencyMode', 'tools']
const record = (value: unknown): value is Record<string, unknown> => typeof value === 'object' && value !== null && !Array.isArray(value)
export function isPythonPluginRuntime(value: unknown): value is PythonPluginRuntime {
  if (!record(value) || Object.keys(value).some(key => !runtimeFields.includes(key))
    || value.kind !== 'python' || value.protocolVersion !== 1 || value.dependencyMode !== 'stdlib-only'
    || typeof value.minimumPythonVersion !== 'string' || !/^3\.(1[1-9]|[2-9][0-9])(?![\s\S])/.test(value.minimumPythonVersion)
    || typeof value.entryPoint !== 'string' || value.entryPoint.length > 240
    || !/^[A-Za-z0-9_./-]+\.py(?![\s\S])/.test(value.entryPoint)
    || value.entryPoint === 'botagent_worker.py'
    || value.entryPoint.split('/').some(part => !part || part === '.' || part === '..' || part.endsWith('.') || /^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|$)/i.test(part))
    || !Array.isArray(value.tools) || value.tools.length < 1 || value.tools.length > 64) return false
  return value.tools.every(tool => typeof tool === 'string' && /^[A-Za-z0-9_-]{1,64}(?![\s\S])/.test(tool))
    && new Set(value.tools).size === value.tools.length
}

/** API projections may omit/null managed fields, but never disguise a Python script as a DLL. */
export function hasValidPluginRuntime(value: Record<string, unknown>): boolean {
  if (value.apiVersion === 2) return isPythonPluginRuntime(value.runtime)
    && value.entryAssembly == null && value.entryType == null && value.targetFramework == null
  return value.apiVersion === 1 && value.runtime == null
    && typeof value.entryAssembly === 'string' && typeof value.entryType === 'string'
    && typeof value.targetFramework === 'string'
}

export function pluginTargetLabel(manifest: { apiVersion: number; runtime?: PythonPluginRuntime | null; targetFramework?: string | null }): string {
  return manifest.apiVersion === 2 && manifest.runtime
    ? 'Python >=' + manifest.runtime.minimumPythonVersion + '、<4 · 标准库'
    : manifest.targetFramework || '运行时未声明'
}
