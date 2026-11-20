import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import DeclaredPluginSettings from '../../src/features/capabilities/DeclaredPluginSettings.vue'
import { createPluginSettingsDraft, pluginSettingsErrors } from '../../src/features/capabilities/declared-plugin-settings'
import { isPluginSettingDefinition } from '../../src/features/capabilities/plugin-manifest'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import { samePreviewManifest } from '../../src/api/plugin-manifest-values'
import { lifecycleChanges } from '../../src/api/plugin-lifecycle-diff'
import fixture from '../../../../tests/contracts/plugin-manifest-semantics.json'

const field = { key: 'limit', label: '搜索結果数量', type: 'number' as const, defaultValue: '5',
  numberConstraints: { minimum: 1, maximum: 10, integer: true } }
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })

describe('declared numeric settings constraints', () => {
  it('renders the approved limits and integer step with an accessible hint', () => {
    const wrapper = mount(DeclaredPluginSettings, { props: { definitions: [field], draft: createPluginSettingsDraft([field]) } })
    mounted.push(wrapper)
    const input = wrapper.get('[data-testid="plugin-setting-limit"]')
    expect(input.attributes('min')).toBe('1')
    expect(input.attributes('max')).toBe('10')
    expect(input.attributes('step')).toBe('1')
    expect(input.attributes('aria-describedby')).toBe('plugin-number-help-limit')
    expect(wrapper.get('#plugin-number-help-limit').text()).toContain('整数')
    expect(wrapper.get('#plugin-number-help-limit').text()).toContain('1 至 10')
  })
  it.each(['0', '11', '1.5', '9007199254740993'])('blocks %s before preparing a write', value => {
    expect(pluginSettingsErrors([field], { limit: { kind: 'set', value } })).toHaveLength(1)
  })
  it.each(['1', '5', '10'])('accepts inclusive integer value %s', value => {
    expect(pluginSettingsErrors([field], { limit: { kind: 'set', value } })).toEqual([])
  })
  it('preserves unconstrained numbers, keep and optional delete', () => {
    const unbounded = { key: 'ratio', label: '比例', type: 'number' as const }
    expect(pluginSettingsErrors([unbounded], { ratio: { kind: 'set', value: '1.25' } })).toEqual([])
    expect(pluginSettingsErrors([field], { limit: { kind: 'keep', value: 'bad' } })).toEqual([])
    expect(pluginSettingsErrors([field], { limit: { kind: 'delete', value: '' } })).toEqual([])
  })
  it.each([{ minimum: 10, maximum: 1 }, { integer: 'true' }, { minimum: '1' },
    { minimum: Number.POSITIVE_INFINITY }, { unknown: true }])('rejects malformed numeric declarations without dropping them', bounds => {
    expect(isPluginSettingDefinition({ ...field, numberConstraints: bounds })).toBe(false)
  })
  it('rejects numeric constraints on another setting type', () => {
    expect(isPluginSettingDefinition({ ...field, type: 'string' })).toBe(false)
  })
  it('preserves bounds through import and binds preview and lifecycle differences', () => {
    const manifest = { ...fixture.baselines.dotnet, settings: [field] }
    const a = parsePluginJson(JSON.stringify(manifest))
    const b = parsePluginJson(JSON.stringify({ ...manifest, settings: [{ ...field, numberConstraints: { ...field.numberConstraints, maximum: 12 } }] }))
    expect(a.settings).toEqual([field])
    expect(samePreviewManifest(a, b)).toBe(false)
    expect(lifecycleChanges(a, b)).toHaveLength(1)
    expect(lifecycleChanges(a, b)[0]?.field).toBe('settings')
  })
})
