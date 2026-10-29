import { describe, expect, it } from 'vitest'
import { githubSourceError, isPluginManifest } from '../../src/features/capabilities/plugin-manifest'
import { clearPluginSecretDrafts, createPluginSettingsDraft, pluginSettingsErrors } from '../../src/features/capabilities/declared-plugin-settings'
import { syntheticManifest, syntheticSettings } from '../fixtures/provider-marketplace'

describe('trusted plugin declarations', () => {
  it('accepts the flat frozen Core manifest but rejects missing hashes and executable setting types', () => {
    expect(isPluginManifest(syntheticManifest)).toBe(true)
    expect(isPluginManifest({ ...syntheticManifest, sha256: 'invalid' })).toBe(false)
    expect(isPluginManifest({ ...syntheticManifest, settings: [{ key: 'code', label: 'Code', type: 'script' }] })).toBe(false)
    expect(isPluginManifest({ ...syntheticManifest, settings: [syntheticSettings[0], syntheticSettings[0]] })).toBe(false)
  })
  it('ignores secret plaintext in a projection and validates declared values only', () => {
    const draft = createPluginSettingsDraft(syntheticSettings, { secret: 'synthetic-never-display', unknown: 'ignored' })
    expect(draft.secret).toEqual({ kind: 'keep', value: '' })
    expect(draft.unknown).toBeUndefined()
    draft.limit = { kind: 'set', value: 'Infinity' }
    draft.format = { kind: 'set', value: '<script>' }
    expect(pluginSettingsErrors(syntheticSettings, draft)).toHaveLength(2)
    draft.secret = { kind: 'set', value: 'synthetic-new-value' }
    clearPluginSecretDrafts(syntheticSettings, draft)
    expect(draft.secret).toEqual({ kind: 'keep', value: '' })
  })
  it('rejects arbitrary custom sources before the backend is called', () => {
    expect(githubSourceError('')).toBeNull()
    for (const source of ['https://example.com/plugin.zip', 'http://github.com/example/repository', 'https://user:pass@github.com/example/repository', 'https://github.com/example/repository?token=synthetic', 'https://github.com/example/repository/releases/latest', 'javascript:alert(1)']) expect(githubSourceError(source)).not.toBeNull()
  })
})
