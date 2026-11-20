import { describe, expect, it } from 'vitest'
import { canonicalSettingNumberConstraints, validSettingNumberText } from '../../src/api/plugin-setting-numbers'
import { isPluginSettingDefinition } from '../../src/features/capabilities/plugin-manifest'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import { samePreviewManifest } from '../../src/api/plugin-manifest-values'
import fixture from '../../../../tests/contracts/plugin-manifest-semantics.json'

describe('numeric declaration wire and exact integer text', () => {
  it.each(['0', '-0', '2147483648', '9007199254740991', '-9007199254740991', ' 10 '])('accepts safe JSON integer %s', text => {
    expect(validSettingNumberText(text, { integer: true })).toBe(true)
  })
  it.each(['1.0', '1e0', '+1', '01', '9007199254740992', '-9007199254740992', '0x10', 'true'])('rejects incompatible JSON integer text %s', text => {
    expect(validSettingNumberText(text, { integer: true })).toBe(false)
  })
  it('permits finite decimal and exponent text when integer is not declared', () => {
    expect(validSettingNumberText('1.25', { minimum: 0.5, maximum: 10.5 })).toBe(true)
    expect(validSettingNumberText('1e0', { minimum: 0.5, maximum: 10.5 })).toBe(true)
    expect(validSettingNumberText('1e999', {})).toBe(false)
  })
  it.each(['0', '11', '1.5', '1.0', '9007199254740993'])('rejects an incompatible default %s before import approval', defaultValue => {
    expect(isPluginSettingDefinition({ key: 'limit', label: 'Limit', type: 'number', defaultValue,
      numberConstraints: { minimum: 1, maximum: 10, integer: true } })).toBe(false)
  })
  it('maps Pascal numeric rules without dropping them and rejects mixed child fields', () => {
    const camel = fixture.baselines.bounded
    const pascal: Record<string, unknown> = Object.fromEntries(Object.entries(camel).filter(([key]) => !['settings', 'dependencies'].includes(key))
      .map(([key, value]) => [key[0]!.toUpperCase() + key.slice(1), value]))
    pascal.Settings = [{ Key: 'limit', Label: 'Result limit', Type: 'number', DefaultValue: '5', Required: true, Choices: [],
      NumberConstraints: { Minimum: 0, Maximum: 10, Integer: true } }]
    expect(parsePluginJson(JSON.stringify(pascal)).settings).toEqual(camel.settings)
    pascal.Settings = [{ Key: 'limit', Label: 'Result limit', Type: 'number', DefaultValue: '5',
      NumberConstraints: { Minimum: 0, maximum: 10, Integer: true } }]
    expect(() => parsePluginJson(JSON.stringify(pascal))).toThrow()
  })
  it('omits absent and null constraints from old comparisons and canonicalizes an empty rule object', () => {
    const manifest = parsePluginJson(JSON.stringify(fixture.baselines.dotnet))
    const setting = manifest.settings?.[0]
    if (!setting) throw new Error('The legacy fixture must declare its setting.')
    expect(samePreviewManifest(manifest, { ...manifest, settings: [{ ...setting, numberConstraints: null }] })).toBe(true)
    expect(canonicalSettingNumberConstraints({})).toEqual({ minimum: null, maximum: null, integer: false })
  })
  it.each(['"maximum":10,"maximum":12', '"maximum":10,"Maximum":12',
    '"maximum":10,"' + String.fromCharCode(92) + 'u006daximum":12'])
  ('rejects ambiguous numeric rule keys before approval', rule => {
    const input = JSON.stringify(fixture.baselines.bounded).replace('"minimum":0,"maximum":10,"integer":true', rule)
    expect(() => parsePluginJson(input)).toThrow('重复字段')
  })
})
