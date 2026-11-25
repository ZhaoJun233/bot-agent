import { describe, expect, it } from 'vitest'
import {
  autoCredentialKey,
  hasProviderMetadataChanges,
  isUrlLikeSecret,
  providerCredentialSummary,
  providerDraft,
  providerDraftErrors,
  providerEdit,
  providerSecrets
} from '../../src/features/capabilities/provider-draft'
import { syntheticProvider } from '../fixtures/provider-marketplace'

describe('provider-draft integrated URL and API Key model', () => {
  it('derives valid default credential keys and rejects URL-shaped secrets', () => {
    expect(autoCredentialKey('provider.abc123')).toBe('key-provider.abc123')
    expect(autoCredentialKey('  my custom/key  ')).toBe('key-my-custom-key')
    expect(autoCredentialKey('').length).toBeGreaterThan(0)
    expect(autoCredentialKey('a'.repeat(200)).length).toBeLessThanOrEqual(128)
    expect(isUrlLikeSecret('https://example.com/v1')).toBe(true)
    expect(isUrlLikeSecret('HTTP://localhost:11434')).toBe(true)
    expect(isUrlLikeSecret('sk-synthetic-1234')).toBe(false)
  })

  it('defaults new providers to independent API Key with auto-derived credentialKey', () => {
    const draft = providerDraft(undefined, 'provider.demo1')
    expect(draft.credentialMode).toBe('independent')
    expect(draft.credentialKey).toBe('key-provider.demo1')
    expect(draft.credentialSecret).toBe('')
    expect(draft.primarySecretAction).toBe('keep')

    const errorsEmpty = providerDraftErrors({ ...draft, name: 'Demo', baseUrl: 'https://example.com/v1', modelName: 'gpt-4o-mini' })
    expect(errorsEmpty).toContain('请填写 API Key，或选择已存在的凭据标识。')

    const errorsUrl = providerDraftErrors({
      ...draft, name: 'Demo', baseUrl: 'https://example.com/v1', modelName: 'gpt-4o-mini',
      credentialSecret: 'https://example.com/wrong'
    })
    expect(errorsUrl).toContain('API Key 不能填写 URL，请填入服务商密钥。')

    const validDraft = {
      ...draft, name: 'Demo', baseUrl: 'https://example.com/v1', modelName: 'gpt-4o-mini',
      credentialSecret: 'sk-synthetic-demo-key'
    }
    expect(providerDraftErrors(validDraft)).toEqual([])
    expect(providerEdit('provider.demo1', validDraft)).toMatchObject({
      key: 'provider.demo1',
      operation: 1,
      changes: {
        enabled: { kind: 1, value: false },
        credential: { kind: 3, key: 'key-provider.demo1' }
      }
    })
    expect(providerSecrets(validDraft)).toEqual([
      { kind: 1, value: 'sk-synthetic-demo-key', credentialKey: 'key-provider.demo1' }
    ])
  })

  it('allows reusing an already-configured modelCredential without re-entering plaintext', () => {
    const draft = {
      ...providerDraft(undefined, 'provider.reuse'),
      name: 'Reuse',
      baseUrl: 'https://example.com/v1',
      modelName: 'gpt-4o-mini',
      credentialKey: 'existing-key',
      credentialSecret: ''
    }
    expect(providerDraftErrors(draft, undefined, {
      modelCredentials: [{ key: 'existing-key', configured: true, keyUsable: true }]
    })).toEqual([])
    expect(providerSecrets(draft)).toEqual([])
  })

  it('supports primary inline Slot 0 replace/delete with secret-only saves omitting ProviderEdit', () => {
    const primary = syntheticProvider({ key: 'primary', priority: 0, desiredEnabled: true, credential: 1, credentialConfigured: true })
    const draft = providerDraft(primary, 'primary')
    expect(draft.credentialMode).toBe('keep')
    expect(draft.primarySecretAction).toBe('keep')
    expect(hasProviderMetadataChanges('primary', draft, primary)).toBe(false)

    // Replace Slot 0 only
    const replaceDraft = { ...draft, primarySecretAction: 'replace' as const, credentialSecret: 'sk-synthetic-primary-next' }
    expect(providerDraftErrors(replaceDraft, primary, { isPrimary: true })).toEqual([])
    expect(hasProviderMetadataChanges('primary', replaceDraft, primary)).toBe(false)
    expect(providerSecrets(replaceDraft, false, primary, true)).toEqual([
      { slot: 0, kind: 1, value: 'sk-synthetic-primary-next' }
    ])

    // URL pasted into Slot 0 rejected
    expect(providerDraftErrors({ ...draft, primarySecretAction: 'replace', credentialSecret: 'https://example.com/v1' }, primary, { isPrimary: true }))
      .toContain('API Key 不能填写 URL，请填入服务商密钥。')

    // Delete Slot 0 only
    const deleteDraft = { ...draft, primarySecretAction: 'delete' as const }
    expect(providerDraftErrors(deleteDraft, primary, { isPrimary: true })).toEqual([])
    expect(hasProviderMetadataChanges('primary', deleteDraft, primary)).toBe(false)
    expect(providerSecrets(deleteDraft, false, primary, true)).toEqual([{ slot: 0, kind: 2 }])

    // Primary priority/enabled are always kept (read-only on backend)
    const edit = providerEdit('primary', { ...draft, priority: '99', enabled: false, modelName: 'new-model' }, primary)
    expect(edit.changes.priority).toEqual({ kind: 0 })
    expect(edit.changes.enabled).toEqual({ kind: 0 })
    expect(edit.changes.modelName).toEqual({ kind: 1, value: 'new-model' })
  })

  it('supports independent key replacement without metadata churn and explicit detach + delete', () => {
    const existing = syntheticProvider({
      key: 'provider.sec',
      credential: 2,
      credentialKey: 'key-sec',
      credentialConfigured: true,
      desiredEnabled: false
    })
    const draft = providerDraft(existing, 'provider.sec')
    const replaceSameKey = { ...draft, credentialMode: 'independent' as const, credentialKey: 'key-sec', credentialSecret: 'sk-rotated' }
    expect(hasProviderMetadataChanges('provider.sec', replaceSameKey, existing)).toBe(false)
    expect(providerSecrets(replaceSameKey, false, existing, false)).toEqual([
      { kind: 1, value: 'sk-rotated', credentialKey: 'key-sec' }
    ])

    const detachDelete = { ...draft, credentialMode: 'none' as const, deleteIndependentSecret: true }
    expect(hasProviderMetadataChanges('provider.sec', detachDelete, existing)).toBe(true)
    expect(providerEdit('provider.sec', detachDelete, existing).changes.credential).toEqual({ kind: 2 })
    expect(providerSecrets(detachDelete, false, existing, false)).toEqual([
      { kind: 2, credentialKey: 'key-sec' }
    ])
  })

  it('summarizes live provider credential status across primary, independent, ollama, and legacy bindings', () => {
    const primary = syntheticProvider({ key: 'primary', credential: 1, credentialConfigured: false })
    expect(providerCredentialSummary(primary, { slot: 0, configured: true, source: 2 }).label).toBe('主 Key · 环境变量')
    expect(providerCredentialSummary(primary, { slot: 0, configured: true, source: 1 }).label).toBe('主 Key · 数据库')
    expect(providerCredentialSummary(primary, { slot: 0, configured: false, source: 0 }).label).toBe('未配置')

    const secondaryShared = syntheticProvider({ key: 'backup', credential: 1 })
    expect(providerCredentialSummary(secondaryShared, { slot: 0, configured: true, source: 1 }).label).toBe('共用主 Key（数据库）')

    const independent = syntheticProvider({ key: 'ind', credential: 2, credentialKey: 'k1', credentialConfigured: true })
    expect(providerCredentialSummary(independent, undefined).label).toBe('独立 Key · 已配置')

    const ollama = syntheticProvider({ key: 'local-ollama', protocol: 5, credential: 0 })
    expect(providerCredentialSummary(ollama, undefined)).toMatchObject({ label: '无需密钥（Ollama）', configured: true })

    const legacy = syntheticProvider({ key: 'old', credential: 3 })
    expect(providerCredentialSummary(legacy, undefined)).toMatchObject({ label: '需人工核对', configured: false })
  })
})
