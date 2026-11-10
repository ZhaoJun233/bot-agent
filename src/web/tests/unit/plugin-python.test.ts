import { describe, expect, it } from 'vitest'
import vectors from '../fixtures/python-contract.json'
import { pythonManifest } from '../fixtures/python-plugin'
import { syntheticManifest } from '../fixtures/provider-marketplace'
import { isPythonPluginRuntime } from '../../src/api/plugin-runtime'
import { canonicalManifest, samePreviewManifest } from '../../src/api/plugin-manifest-values'
import { isPluginManifest } from '../../src/features/capabilities/plugin-manifest'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import { pluginRuntime, pluginAcquisitionUnavailable } from '../../src/features/capabilities/plugin-catalog'
import { catalogEntry, mergePluginCatalog } from '../../src/features/capabilities/plugin-marketplace'
import { packageItem, presentation } from '../fixtures/plugin-presentation'
import { ZbaClient } from '../../src/api/zba'

describe('Python v2 technical contract', () => {
  it.each(vectors.runtimeCases)('shares producer boundary $name', vector => {
    const key = vector.field.charAt(0).toLowerCase() + vector.field.slice(1)
    const runtime = { ...pythonManifest.runtime, [key]: vector.value }
    expect(isPythonPluginRuntime(runtime)).toBe(vector.accepted)
    const raw = { ...vectors.manifest, Runtime: { ...vectors.manifest.Runtime, [vector.field]: vector.value } }
    if (vector.accepted) expect(parsePluginJson(JSON.stringify(raw)).runtime).toEqual(runtime)
    else expect(() => parsePluginJson(JSON.stringify(raw))).toThrow()
  })
  it('accepts camel/Pascal Python without fabricated managed target', () => {
    expect(parsePluginJson(JSON.stringify(vectors.manifest))).toEqual(pythonManifest)
    expect(parsePluginJson(JSON.stringify(pythonManifest))).toEqual(pythonManifest)
    expect(isPluginManifest({ ...pythonManifest, entryAssembly: null, entryType: null, targetFramework: null })).toBe(true)
    expect(canonicalManifest(pythonManifest)).not.toHaveProperty('targetFramework')
    expect(canonicalManifest(pythonManifest)).not.toHaveProperty('entryAssembly')
  })
  it.each(['entryAssembly', 'entryType', 'targetFramework'])('rejects raw Python managed field %s even null', key => {
    expect(() => parsePluginJson(JSON.stringify({ ...pythonManifest, [key]: null }))).toThrow()
    const pascal = key.charAt(0).toUpperCase() + key.slice(1)
    expect(() => parsePluginJson(JSON.stringify({ ...vectors.manifest, [pascal]: null }))).toThrow()
    expect(isPluginManifest({ ...pythonManifest, [key]: 'fake.dll' })).toBe(false)
  })
  it('does not broaden .NET v1 or accept unknown runtime claims', () => {
    expect(isPluginManifest({ ...syntheticManifest, runtime: null })).toBe(true)
    expect(isPluginManifest({ ...syntheticManifest, runtime: pythonManifest.runtime })).toBe(false)
    expect(() => parsePluginJson(JSON.stringify({ ...syntheticManifest, runtime: null }))).toThrow()
    expect(isPythonPluginRuntime({ ...pythonManifest.runtime, sandboxed: true })).toBe(false)
    expect(() => parsePluginJson(JSON.stringify({ ...vectors.manifest, runtime: vectors.manifest.Runtime }))).toThrow()
  })
  it.each([
    { entryPoint: 'other.py' }, { minimumPythonVersion: '3.12' }, { tools: ['different'] },
    { protocolVersion: 2 }, { dependencyMode: 'pip' }, { kind: 'other' }
  ])('binds every runtime field into confirmation identity %j', delta => {
    const changed = structuredClone(pythonManifest)
    Object.assign(changed.runtime!, delta)
    expect(samePreviewManifest(pythonManifest, changed)).toBe(false)
  })
  it('preserves declared tool order in confirmation identity', () => {
    const a = { ...pythonManifest, runtime: { ...pythonManifest.runtime!, tools: ['one', 'two'] } }
    const b = { ...pythonManifest, runtime: { ...pythonManifest.runtime!, tools: ['two', 'one'] } }
    expect(samePreviewManifest(a, b)).toBe(false)
  })
  it('derives runtime only from technical manifest and requires checked readiness', () => {
    expect(pluginRuntime(catalogEntry(packageItem({ metadata: presentation({ runtime: 'python' }) })))).toBe('dotnet')
    const item = packageItem({ manifest: pythonManifest, metadata: presentation({ runtime: 'dotnet' }) })
    expect(pluginRuntime(catalogEntry(item))).toBe('python')
    expect(pluginAcquisitionUnavailable(catalogEntry(item))).toBe(true)
    expect(pluginAcquisitionUnavailable(catalogEntry({ ...item, isCompatible: false }))).toBe(true)
    expect(pluginAcquisitionUnavailable(catalogEntry({ ...item, isCompatible: true }))).toBe(false)
    const merged = mergePluginCatalog([{ ...item, isCompatible: false }], { revision: 'r1', plugins: [{ ...item, installedVersion: '0.9.0', isCompatible: true }] }, [])
    expect(pluginAcquisitionUnavailable(merged[0]!)).toBe(true)
  })
  it('decodes actual catalog Python and rejects malformed readiness', async () => {
    const payload = { revision: 'r1', source: 'bundled', trustNotice: 'Not a sandbox', categories: [], plugins: [packageItem({ manifest: pythonManifest, isCompatible: true, compatibilityMessage: null })] }
    const client = new ZbaClient({ baseUrl: 'https://example.com', fetchImpl: async () => new Response(JSON.stringify(payload)) })
    expect((await client.pluginCatalog()).plugins[0]?.manifest.runtime).toEqual(pythonManifest.runtime)
    const invalid = new ZbaClient({ baseUrl: 'https://example.com', fetchImpl: async () => new Response(JSON.stringify({ ...payload, plugins: [{ ...payload.plugins[0], isCompatible: 'true' }] })) })
    await expect(invalid.pluginCatalog()).rejects.toMatchObject({ code: 'invalid_json' })
  })
})
