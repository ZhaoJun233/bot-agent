import { describe, expect, it } from 'vitest'
import { basicFields, configurationCapabilities, configurationFor, fieldOwner, ownsConfiguration } from '../../src/features/capabilities/configuration-ownership'
import type { ZbaPatchSection } from '../../src/api/zba'
import fixture from '../fixtures/configuration-read-editable-result.json'
import { pluginSettings, registerPlugin } from '../../src/features/capabilities/plugin-settings'
describe('configuration UI ownership', () => {
  it('extension configuration follows registration, withdrawal and re-registration', () => {
    const spec = { pluginKey: 'ext.synthetic-review', title: 'Synthetic extension', summary: 'Synthetic settings',
      fields: [{ section: 'behavior' as const, key: 'syntheticExtension' }] }
    const undo = registerPlugin(spec)
    try {
      expect(fieldOwner('behavior', 'syntheticExtension')).toBe(spec.pluginKey)
      expect(configurationFor(spec.pluginKey)).toMatchObject({ id: spec.pluginKey, title: spec.title, sections: ['behavior'] })
    } finally { undo() }
    expect(configurationFor(spec.pluginKey)).toBeUndefined()
    expect(fieldOwner('behavior', 'syntheticExtension')).toBe('conversation')
    const undoAgain = registerPlugin({ ...spec, title: 'Synthetic replacement', fields: [{ section: 'agent', key: 'syntheticAgent' }] })
    try {
      expect(configurationFor(spec.pluginKey)).toMatchObject({ title: 'Synthetic replacement', sections: ['agent'] })
    } finally { undoAgain() }
  })
  it('each declared non-basic field and secret has one accessible capability owner', () => {
    const ids = new Set(configurationCapabilities().map(item => item.id))
    for (const section of ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting'] as ZbaPatchSection[]) {
      for (const key of Object.keys(fixture.view[section])) {
        const owner = fieldOwner(section, key)
        expect(owner === 'basic' || ids.has(owner), section + '.' + key).toBe(true)
        expect(ownsConfiguration('basic', section, key)).toBe(section === 'behavior' && basicFields.has(key))
      }
    }
    for (const secret of fixture.secrets) expect(configurationCapabilities().filter(item => item.secrets.some(slot => slot === secret.slot))).toHaveLength(1)
  })
  it('registered plugins keep their owned sections and single-field definitions accessible', () => {
    for (const plugin of pluginSettings.filter(item => item.pluginKey !== 'preset.feature.token-quota')) {
      expect(configurationFor(plugin.pluginKey)?.pluginKey).toBe(plugin.pluginKey)
      for (const field of plugin.fields ?? []) expect(fieldOwner(field.section, field.key)).toBe(plugin.pluginKey)
    }
    expect(fieldOwner('voice', 'voiceName')).toBe('preset.feature.voice')
    expect(fieldOwner('platform', 'officialEnabled')).toBe('platform')
    expect(fieldOwner('model', 'model')).toBe('models')
  })
})
