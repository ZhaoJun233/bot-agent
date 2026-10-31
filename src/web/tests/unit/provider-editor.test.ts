import { describe, expect, it } from 'vitest'
import { editableText, providerDraft, providerDraftErrors, providerEdit } from '../../src/features/capabilities/provider-draft'
import { endpointError, providerPresets, providerProtocols, protocolLabel } from '../../src/features/capabilities/provider-protocols'
import { syntheticProvider, textField } from '../fixtures/provider-marketplace'

describe('provider transport editor', () => {
  it('lists exactly six protocols and defaults legacy providers to OpenAI Chat', () => {
    expect(providerProtocols.map((item) => item.id)).toEqual(['openai-chat', 'openai-responses', 'anthropic-messages', 'gemini-generate-content', 'azure-openai', 'ollama'])
    expect(providerDraft(syntheticProvider()).protocol).toBe('openai-chat')
    expect(providerDraft().enabled).toBe(false)
    expect(protocolLabel(undefined)).toContain('OpenAI')
    for (const preset of providerPresets) expect(providerProtocols.some((item) => item.id === preset.protocol)).toBe(true)
  })
  it('creates a disabled route even if a caller attempts to enable during creation', () => {
    const draft = { ...providerDraft(), name: '示例', baseUrl: 'https://example.com/v1', modelName: 'model-example', enabled: true }
    expect(providerDraftErrors(draft)).toEqual([])
    expect(providerEdit('provider-new', draft)).toMatchObject({ operation: 1, changes: {
      nameRaw: { kind: 1, value: '示例' }, enabled: { kind: 1, value: false }, protocol: { kind: 1, value: 0 }, credential: { kind: 0 }
    } })
  })
  it('uses nameRaw/value rather than display and leaves unchanged fields Keep', () => {
    const provider = syntheticProvider()
    const draft = providerDraft(provider)
    expect(draft.name).toBe('示例提供商')
    expect(draft.baseUrl).toBe('https://example.com/v1')
    const edit = providerEdit(provider.key, { ...draft, priority: '2' }, provider)
    expect(edit.changes.nameRaw).toEqual({ kind: 0 })
    expect(edit.changes.baseUrl).toEqual({ kind: 0 })
    expect(edit.changes.priority).toEqual({ kind: 1, value: 2 })
    expect(edit.changes.protocol).toEqual({ kind: 0 })
    expect(edit.changes.options).toEqual({ kind: 0 })
  })
  it('never writes a masked projection and deletion has no configuration mutations', () => {
    const hidden = { ...textField('not-editable'), visibility: 0 as const, rule: { ...textField('').rule, editable: false } }
    const provider = syntheticProvider({ name: hidden, baseUrl: hidden, modelName: hidden })
    expect(editableText(hidden)).toBeNull()
    const draft = providerDraft(provider)
    expect(draft.name).toBe('')
    expect(providerEdit(provider.key, { ...draft, name: 'masked' }, provider).changes.nameRaw).toEqual({ kind: 0 })
    const edit = providerEdit(provider.key, draft, provider, true)
    expect(edit.operation).toBe(2)
    expect(Object.values(edit.changes).every((field) => field?.kind === 0)).toBe(true)
  })
  it('preserves all existing options when changing one protocol option', () => {
    const provider = syntheticProvider({ protocol: 4, options: { azureDeployment: 'deployment-before', azureApiVersion: 'version-before', anthropicVersion: 'version-other', timeoutSeconds: 5 } })
    const draft = providerDraft(provider)
    draft.azureDeployment = 'deployment-after'
    expect(providerEdit(provider.key, draft, provider).changes.options).toEqual({ kind: 1, value: { ...provider.options, azureDeployment: 'deployment-after' } })
    expect(provider.options?.azureDeployment).toBe('deployment-before')
  })
  it('validates transport-specific options and allows free-text models for every protocol', () => {
    for (const protocol of providerProtocols) {
      const draft = { ...providerDraft(syntheticProvider()), protocol: protocol.id, azureDeployment: 'deployment-example' }
      expect(providerDraftErrors(draft)).toEqual([])
      if (protocol.id === 'azure-openai') expect(providerDraftErrors({ ...draft, azureDeployment: '' }).length).toBe(1)
    }
    expect(providerDraftErrors({ ...providerDraft(syntheticProvider()), priority: '1.5' })).toContain('优先级必须是整数。')
  })
  it('rejects endpoint credentials, query keys and unsupported schemes without making requests', () => {
    for (const endpoint of ['javascript:alert(1)', 'ftp://example.com', 'https://user:password@example.com', 'https://example.com/v1?key=synthetic', 'https://example.com/#key', 'invalid']) expect(endpointError(endpoint)).not.toBeNull()
    expect(endpointError('https://example.com/v1')).toBeNull()
    expect(endpointError('http://localhost:11434')).toBeNull()
  })
})
