import { describe, expect, it } from 'vitest'
import fixture from '../../../../tests/contracts/plugin-manifest-semantics.json'
import { canonicalPluginConfiguration, hasValidPluginConfiguration } from '../../src/api/plugin-configuration'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import { lifecycleChanges, sameLifecycleManifest } from '../../src/api/plugin-lifecycle-diff'
import { ZbaRequestError } from '../../src/api/zba'
import { isPluginWriteOutcomeUnknown } from '../../src/features/capabilities/plugin-write-outcome'

describe('declared owner configuration approval', () => {
  const baseline = fixture.baselines.configuration
  it('keeps every binding and makes order and omitted defaults equivalent', () => {
    const manifest = parsePluginJson(JSON.stringify(baseline))
    const configuration = manifest.configuration!
    expect(canonicalPluginConfiguration(configuration)).toEqual(canonicalPluginConfiguration({
      ...configuration, bindings: [...configuration.bindings].reverse().map(binding => ({
        key: binding.key, legacyFieldId: binding.legacyFieldId, ...(binding.readOnly ? { readOnly: true } : {})
      }))
    }))
    expect(sameLifecycleManifest(manifest, { ...manifest, configuration: {
      ...configuration, bindings: [...configuration.bindings].reverse()
    } })).toBe(true)
    expect(lifecycleChanges({ ...manifest, configuration: undefined }, manifest)[0]?.field).toBe('configuration')
  })
  it.each([
    { name: 'duplicate key', bindings: [{ ...baseline.configuration.bindings[0] }, { ...baseline.configuration.bindings[0] }] },
    { name: 'duplicate identity', bindings: baseline.configuration.bindings.map(binding => ({ ...binding, legacyFieldId: 138 })) },
    { name: 'unknown field', bindings: baseline.configuration.bindings.map(binding => ({ ...binding, futurePolicy: true })) },
    { name: 'null flag', bindings: baseline.configuration.bindings.map(binding => ({ ...binding, readOnly: null })) },
    { name: 'fractional identity', bindings: baseline.configuration.bindings.map(binding => ({ ...binding, legacyFieldId: 1.5 })) },
  ])('rejects $name binding shapes', ({ bindings }) => {
    expect(hasValidPluginConfiguration({ ...baseline, configuration: { ...baseline.configuration, bindings } })).toBe(false)
  })
  it('rejects unbound editable fields, secrets and readonly edits', () => {
    for (const field of [
      { key: 'extra', label: 'Extra', type: 'string' },
      { key: 'webSearchReadMaxChars', label: 'Read limit', type: 'number' }
    ]) expect(hasValidPluginConfiguration({ ...baseline, settings: [...baseline.settings, field] })).toBe(false)
    expect(hasValidPluginConfiguration({ ...baseline, settings: baseline.settings.map(field => ({ ...field, type: 'secret' })) })).toBe(false)
    expect(hasValidPluginConfiguration({ ...baseline, apiVersion: 2 })).toBe(false)
  })
  it.each([
    ['"apiVersion":1,"bindings"', '"apiVersion":1,"ApiVersion":2,"bindings"'],
    ['"legacyFieldId":138', '"legacyFieldId":138,"legacyFieldId":139'],
    ['"legacyFieldId":138', '"legacyFieldId":138,"LegacyFieldId":139'],
    ['"legacyFieldId":138', '"legacyFieldId":138,"' + String.fromCharCode(92) + 'u006cegacyFieldId":139'],
    ['"readOnly":true', '"readOnly":true,"readOnly":false'],
    ['"configuration":{', '"configuration":null,"configuration":{'],
  ])('rejects ambiguous wire keys before parsing can erase them', (from, to) => {
    expect(() => parsePluginJson(JSON.stringify(baseline).replace(from, to))).toThrow('重复字段')
  })
  it('reads Pascal declarations and rejects mixed nested casing', () => {
    const value: Record<string, unknown> = Object.fromEntries(Object.entries(baseline)
      .filter(([key]) => !['configuration', 'settings'].includes(key))
      .map(([key, item]) => [key[0]!.toUpperCase() + key.slice(1), item]))
    value.Settings = baseline.settings.map(field => Object.fromEntries(Object.entries(field)
      .map(([key, item]) => [key[0]!.toUpperCase() + key.slice(1), item])))
    value.Configuration = { ApiVersion: 1, Bindings: baseline.configuration.bindings.map(binding =>
      Object.fromEntries(Object.entries(binding).map(([key, item]) => [key[0]!.toUpperCase() + key.slice(1), item]))) }
    expect(parsePluginJson(JSON.stringify(value)).configuration).toEqual(baseline.configuration)
    value.Configuration = { ApiVersion: 1, bindings: baseline.configuration.bindings }
    expect(() => parsePluginJson(JSON.stringify(value))).toThrow()
  })
  it('keeps a known committed application failure distinct from an uncertain transport', () => {
    expect(isPluginWriteOutcomeUnknown(new ZbaRequestError(503, 'plugin_settings_application_failed', 'synthetic'))).toBe(false)
    expect(isPluginWriteOutcomeUnknown(new ZbaRequestError(503, 'plugin_settings_unavailable', 'synthetic'))).toBe(true)
  })
})
