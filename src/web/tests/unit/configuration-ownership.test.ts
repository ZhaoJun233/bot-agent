import { describe, expect, it } from 'vitest'
import { computed } from 'vue'
import { basicFields, configurationCapabilities, configurationFor, fieldOwner, ownsConfiguration, secretOwner } from '../../src/features/capabilities/configuration-ownership'
import type { ZbaPatchSection } from '../../src/api/zba'
import fixture from '../fixtures/configuration-read-editable-result.json'
import { pluginSettings, registerPlugin, settingsFor, unregisterPlugin } from '../../src/features/capabilities/plugin-settings'
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
    for (const plugin of pluginSettings.filter(item => item.kind === 'plugin')) {
      expect(configurationFor(plugin.pluginKey)?.pluginKey).toBe(plugin.pluginKey)
      for (const field of plugin.fields ?? []) expect(fieldOwner(field.section, field.key)).toBe(plugin.pluginKey)
    }
    expect(fieldOwner('voice', 'voiceName')).toBe('preset.feature.voice')
    expect(fieldOwner('platform', 'officialEnabled')).toBe('platform')
    expect(fieldOwner('model', 'model')).toBe('models')
  })
})

describe('unified ownership metadata', () => {
  it('keeps exactly the seven existing basic fields', () => {
    expect([...basicFields].sort()).toEqual([
      'aiModeEnabled', 'botPersona', 'groupCooldownSeconds', 'privateCooldownSeconds',
      'maxConcurrentReplies', 'maxContextMessages', 'maxMessagesPerConversation'
    ].sort())
    for (const key of basicFields) expect(fieldOwner('behavior', key)).toBe('basic')
    expect(fieldOwner('behavior', 'syntheticFutureField')).toBe('conversation')
    expect(fieldOwner('agent', 'botPersona')).toBe('agent')
  })

  it('preserves system IDs and secrets without presenting them as backend plugin keys', () => {
    const systems = configurationCapabilities().filter(spec => spec.kind === 'system')
    expect(systems.map(spec => spec.id)).toEqual(['conversation', 'models', 'agent', 'platform', 'reporting'])
    for (const spec of systems) {
      expect(spec.pluginKey).toBeUndefined()
      expect(spec.editor).toEqual({ type: 'fields' })
      expect(settingsFor(spec.id)?.kind).toBe('system')
    }
    expect(configurationFor('models')).toMatchObject({ secrets: [0], providers: true, sections: ['model'] })
    expect(configurationFor('agent')?.secrets).toEqual([1])
    expect(configurationFor('preset.feature.voice')?.secrets).toEqual([2])
    expect(configurationFor('platform')?.secrets).toEqual([3, 4, 5])
    expect(configurationFor('reporting')?.secrets).toEqual([])
    expect(secretOwner(2)).toBe('preset.feature.voice')
    expect(configurationFor('preset.feature.token-quota')?.editor.type).toBe('component')
  })

  it('explicit claims override basic fields, which override section fallback', () => {
    const owner = computed(() => fieldOwner('behavior', 'aiModeEnabled'))
    expect(owner.value).toBe('basic')
    const undo = registerPlugin({ pluginKey: 'ext.explicit-basic', title: 'Basic override', summary: '', fields: [{ section: 'behavior', key: 'aiModeEnabled' }] })
    try {
      expect(owner.value).toBe('ext.explicit-basic')
      expect(ownsConfiguration('basic', 'behavior', 'aiModeEnabled')).toBe(false)
    } finally { undo() }
    expect(owner.value).toBe('basic')
  })

  it.each(['music', 'voice'] as const)('%s fields retain an accessible system owner after plugin withdrawal', section => {
    const key = 'preset.feature.' + section
    const spec = settingsFor(key)!
    const owner = computed(() => fieldOwner(section, 'syntheticFutureField'))
    expect(owner.value).toBe(key)
    expect(unregisterPlugin(key)).toBe(true)
    try {
      expect(owner.value).toBe('conversation')
      expect(configurationFor(owner.value)).toMatchObject({ kind: 'system', pluginKey: undefined })
      expect(configurationFor('conversation')?.sections).toContain(section)
      expect(ownsConfiguration('conversation', section, 'syntheticFutureField')).toBe(true)
    } finally { registerPlugin(spec) }
    expect(owner.value).toBe(key)
  })

  it('late system registration and disposal update capabilities computed from the registry', () => {
    const all = computed(() => configurationCapabilities())
    const entry = computed(() => configurationFor('synthetic-system'))
    expect(all.value.some(spec => spec.id === 'synthetic-system')).toBe(false)
    expect(entry.value).toBeUndefined()
    const undo = registerPlugin({ pluginKey: 'synthetic-system', kind: 'system', title: 'Synthetic system', summary: '', fields: [{ section: 'reporting', key: 'syntheticFlag' }] })
    try {
      expect(all.value.some(spec => spec.id === 'synthetic-system')).toBe(true)
      expect(entry.value).toMatchObject({ pluginKey: undefined, kind: 'system', editor: { type: 'fields' }, sections: ['reporting'] })
      expect(fieldOwner('reporting', 'syntheticFlag')).toBe('synthetic-system')
    } finally { undo() }
    expect(entry.value).toBeUndefined()
    expect(all.value.some(spec => spec.id === 'synthetic-system')).toBe(false)
    expect(fieldOwner('reporting', 'syntheticFlag')).toBe('reporting')
  })

  it('capability descriptors are detached snapshots of nested registration metadata', () => {
    const capability = configurationFor('preset.feature.voice')!
    capability.sections.push('agent')
    capability.secrets.push(0)
    capability.enableField!.key = 'syntheticChanged'
    capability.editor.type = 'declared'
    const next = configurationFor('preset.feature.voice')!
    expect(next.sections).toEqual(['voice'])
    expect(next.secrets).toEqual([2])
    expect(next.enableField?.key).toBe('enableVoice')
    expect(next.editor.type).toBe('fields')
  })
})
