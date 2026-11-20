import { describe, expect, it } from 'vitest'
import fixture from '../../../../tests/contracts/plugin-manifest-semantics.json'
import type { ZbaPluginPackageManifest, ZbaPluginDependency, ZbaPluginSettingDefinition } from '../../src/api/zba'
import type { PythonPluginRuntime } from '../../src/api/plugin-runtime'
import { canonicalManifest, samePreviewManifest } from '../../src/api/plugin-manifest-values'
import { isPluginManifest } from '../../src/features/capabilities/plugin-manifest'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import type { PluginSharedContract } from '../../src/api/plugin-shared-contracts'
import { lifecycleChanges, sameLifecycleManifest } from '../../src/api/plugin-lifecycle-diff'
import type { PluginPackageData, PluginReceiptContract } from '../../src/api/plugin-data'

// Adding a contract field requires an explicit decision in the shared field-mutation fixture.
const fields = { id: true, version: true, entryAssembly: true, entryType: true, sha256: true,
  downloadUrl: true, apiVersion: true, targetFramework: true, minimumHostVersion: true,
  permissions: true, dependencies: true, settings: true, runtime: true, sharedContracts: true, data: true, control: true,
  toolApiVersion: true, configuration: true, ui: true, adoption: true, exclusiveResources: true } satisfies Record<keyof ZbaPluginPackageManifest, boolean>
const runtimeFields = { kind: true, protocolVersion: true, entryPoint: true, minimumPythonVersion: true,
  dependencyMode: true, tools: true } satisfies Record<keyof PythonPluginRuntime, boolean>
const settingFields = { key: true, label: true, type: true, defaultValue: true, required: true,
  choices: true, numberConstraints: true } satisfies Record<keyof ZbaPluginSettingDefinition, boolean>
const numberFields = { minimum: true, maximum: true, integer: true } satisfies Record<keyof
  import('../../src/api/plugin-setting-numbers').PluginSettingNumberConstraints, boolean>
const dependencyFields = { id: true, version: true } satisfies Record<keyof ZbaPluginDependency, boolean>
const contractFields = { name: true, version: true, sha256: true, owner: true } satisfies Record<keyof PluginSharedContract, boolean>
const dataFields = { tables: true, statePrefixes: true, usage: true, audit: true, receipt: true } satisfies Record<keyof PluginPackageData, boolean>
const receiptFields = { contract: true, version: true } satisfies Record<keyof PluginReceiptContract, boolean>
const configurationFields = { apiVersion: true, bindings: true } satisfies Record<keyof
  import('../../src/api/plugin-configuration').PluginConfigurationDeclaration, boolean>
const bindingFields = { key: true, legacyFieldId: true, readOnly: true, trim: true } satisfies Record<keyof
  import('../../src/api/plugin-configuration').PluginConfigurationBinding, boolean>
function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

describe('manifest semantic fields shared with the host', () => {
  it('parses reserved host contract references for server approval without inventing a runtime dependency', () => {
    const shared = { ...fixture.baselines.shared, dependencies: [], sharedContracts: [
      { name: 'BotAgent.Research.Contracts', version: '1.0.0.0', sha256: 'a'.repeat(64), owner: 'preset.feature.websearch' },
    ] }
    expect(parsePluginJson(JSON.stringify(shared))).toEqual(shared)
    for (const owner of ['probe.undeclared', 'preset..invalid', 'preset.feature.', 'preset.BAD']) {
      expect(isPluginManifest({ ...shared, sharedContracts: [{ ...shared.sharedContracts[0], owner }] })).toBe(false)
    }
  })
  it('binds tool protocol approval and rejects unsupported or cross-runtime declarations', () => {
    const { dotnet, python } = fixture.baselines
    if (!isPluginManifest(dotnet)) throw new Error('invalid fixture')
    const tools = { ...dotnet, toolApiVersion: 1 }
    expect(parsePluginJson(JSON.stringify(tools))).toEqual(tools)
    const pascalManifest = Object.fromEntries(Object.entries(tools)
      .filter(([key]) => !['settings', 'dependencies'].includes(key))
      .map(([key, value]) => [key[0]!.toUpperCase() + key.slice(1), value]))
    expect(parsePluginJson(JSON.stringify(pascalManifest))).toMatchObject({ toolApiVersion: 1 })
    expect(lifecycleChanges(dotnet, tools)).toEqual([{ field: 'toolApiVersion', before: null, after: '1' }])
    expect(samePreviewManifest(dotnet, { ...dotnet, toolApiVersion: null })).toBe(true)
    for (const value of [0, 2, -1, 1.5, '1', true, {}]) {
      expect(isPluginManifest({ ...dotnet, toolApiVersion: value })).toBe(false)
      expect(() => parsePluginJson(JSON.stringify({ ...dotnet, toolApiVersion: value }))).toThrow()
    }
    for (const value of [null, 1]) {
      expect(isPluginManifest({ ...python, toolApiVersion: value })).toBe(false)
      expect(() => parsePluginJson(JSON.stringify({ ...python, toolApiVersion: value }))).toThrow()
    }
  })
  it('covers the declared top-level and nested contracts', () => {
    expect(Object.keys(fields).sort()).toEqual([...new Set(Object.values(fixture.baselines).flatMap(value => Object.keys(value)))].sort())
    expect(Object.keys(runtimeFields).sort()).toEqual(Object.keys(fixture.baselines.python.runtime).sort())
    expect(Object.keys(settingFields).sort()).toEqual([...new Set(Object.values(fixture.baselines)
      .flatMap(value => value.settings.flatMap(setting => Object.keys(setting))))].sort())
    expect(Object.keys(numberFields).sort()).toEqual(Object.keys(fixture.baselines.bounded.settings[0]!.numberConstraints).sort())
    expect(Object.keys(dependencyFields).sort()).toEqual(Object.keys(fixture.baselines.dotnet.dependencies[0]!).sort())
    expect(Object.keys(contractFields).sort()).toEqual(Object.keys(fixture.baselines.shared.sharedContracts[0]!).sort())
    expect(Object.keys(dataFields).sort()).toEqual(Object.keys(fixture.baselines.data.data).sort())
    expect(Object.keys(receiptFields).sort()).toEqual(Object.keys(fixture.baselines.data.data.receipt).sort())
    expect(Object.keys(configurationFields).sort()).toEqual(Object.keys(fixture.baselines.configuration.configuration).sort())
    expect(Object.keys(bindingFields).sort()).toEqual([...new Set(fixture.baselines.configuration.configuration.bindings
      .flatMap(binding => Object.keys(binding)))].sort())
  })
  for (const test of fixture.cases) it('compares or rejects ' + test.base + '.' + test.path, () => {
    if (!(test.base in fixture.baselines)) throw new Error('unknown fixture baseline')
    const baseline = fixture.baselines[test.base as keyof typeof fixture.baselines]
    const changed: Record<string, unknown> = structuredClone(baseline)
    const path = test.path.split('.')
    let parent: unknown = changed
    for (const part of path.slice(0, -1)) {
      if (Array.isArray(parent)) parent = parent[Number(part)]
      else if (record(parent)) parent = parent[part]
      else throw new Error('invalid fixture path')
    }
    if (!record(parent)) throw new Error('invalid fixture target')
    parent[path.at(-1)!] = structuredClone(test.value)
    // The host alone authorizes legacy owner identities; the client binds structurally
    // valid changes into approval without inventing a second owner/field allowlist.
    expect(isPluginManifest(changed)).toBe('frontendValid' in test ? test.frontendValid : test.valid)
    if (!isPluginManifest(baseline)) throw new Error('invalid baseline')
    if (isPluginManifest(changed)) {
      expect(samePreviewManifest(baseline, changed)).toBe(false)
      expect(canonicalManifest(changed)).not.toEqual(canonicalManifest(baseline))
    }
  })
  it('requires explicit boolean control semantics and exposes approval changes', () => {
    const { dotnet, python } = fixture.baselines
    if (!isPluginManifest(dotnet)) throw new Error('invalid fixture')
    const enabled = { ...dotnet, control: true }
    expect(lifecycleChanges(dotnet, enabled)).toEqual([{ field: 'control', before: null, after: 'true' }])
    expect(lifecycleChanges(enabled, dotnet)).toEqual([{ field: 'control', before: 'true', after: null }])
    expect(samePreviewManifest(dotnet, { ...dotnet, control: undefined })).toBe(true)
    expect(isPluginManifest({ ...dotnet, control: 'true' })).toBe(false)
    expect(isPluginManifest({ ...dotnet, control: null })).toBe(false)
    expect(isPluginManifest({ ...python, control: false })).toBe(false)
  })
  it('compares the runtime discriminator' , () => {
    const { dotnet, python } = fixture.baselines
    if (!isPluginManifest(dotnet) || !isPluginManifest(python)) throw new Error('invalid runtime fixture')
    expect(samePreviewManifest(dotnet, python)).toBe(false)
  })
  it('shows adding and removing shared contracts in lifecycle approval', () => {
    const { shared } = fixture.baselines
    if (!isPluginManifest(shared)) throw new Error('invalid shared fixture')
    const absent = { ...shared, sharedContracts: undefined }
    expect(lifecycleChanges(absent, shared)).toEqual([{ field: 'sharedContracts', before: null,
      after: JSON.stringify(canonicalManifest(shared).sharedContracts) }])
    expect(lifecycleChanges(shared, absent)).toEqual([{ field: 'sharedContracts',
      before: JSON.stringify(canonicalManifest(shared).sharedContracts), after: null }])
    expect(sameLifecycleManifest(absent, { ...absent, sharedContracts: [] })).toBe(true)
    expect(samePreviewManifest(absent, { ...absent, sharedContracts: [] })).toBe(true)
  })
  it('normalizes contract order and hash casing while retaining exact identities', () => {
    const { shared } = fixture.baselines
    if (!isPluginManifest(shared)) throw new Error('invalid shared fixture')
    const first = shared.sharedContracts[0]!
    const second = { ...first, name: 'Zeta.Contracts' }
    const left = { ...shared, sharedContracts: [first, second] }
    const right = { ...shared, sharedContracts: [second, { ...first, sha256: first.sha256.toUpperCase() }] }
    expect(samePreviewManifest(left, right)).toBe(true)
    expect(sameLifecycleManifest(left, right)).toBe(true)
    expect(lifecycleChanges(left, right)).toEqual([])
  })
  it('rejects future nested semantics and Python contract fields', () => {
    const { shared, python } = fixture.baselines
    expect(isPluginManifest({ ...shared, sharedContracts: [{ ...shared.sharedContracts[0], futureContractPolicy: 'synthetic' }] })).toBe(false)
    expect(isPluginManifest({ ...python, sharedContracts: null })).toBe(false)
  })
  it('shows resource declarations and compares normalized resource sets', () => {
    const { data } = fixture.baselines
    if (!isPluginManifest(data) || !data.data) throw new Error('invalid data fixture')
    const absent = { ...data, data: null }
    expect(lifecycleChanges(absent, data)).toEqual([{ field: 'data', before: null, after: JSON.stringify(canonicalManifest(data).data) }])
    expect(lifecycleChanges(data, absent)).toEqual([{ field: 'data', before: JSON.stringify(canonicalManifest(data).data), after: null }])
    const left = { ...data, data: { ...data.data, tables: ['a', 'b'], statePrefixes: ['a:', 'b:'] } }
    const right = { ...data, data: { ...data.data, tables: ['b', 'a'], statePrefixes: ['b:', 'a:'] } }
    expect(samePreviewManifest(left, right)).toBe(true)
    expect(sameLifecycleManifest(left, right)).toBe(true)
  })
  it('rejects unknown resource policies and cross-runtime fields', () => {
    const { data, python } = fixture.baselines
    expect(isPluginManifest({ ...data, data: { ...data.data, futureGrant: true } })).toBe(false)
    expect(isPluginManifest({ ...data, data: { ...data.data, receipt: { ...data.data.receipt, futureGrant: true } } })).toBe(false)
    expect(isPluginManifest({ ...python, data: null })).toBe(false)
    expect(isPluginManifest({ ...data, data: { ...data.data, tables: null } })).toBe(false)
  })
})
