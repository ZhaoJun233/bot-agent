import type { ZbaFieldEdit, ZbaProviderConfigurationView, ZbaProviderNameEdit, ZbaReadField, ZbaSecretEdit } from '../../api/zba'
import { endpointError, isProviderProtocol, protocolFromWire, protocolToWire, type ProviderProtocol } from './provider-protocols'

export interface ProviderDraft {
  name: string
  baseUrl: string
  modelName: string
  priority: string
  enabled: boolean
  protocol: string
  azureDeployment: string
  azureApiVersion: string
  anthropicVersion: string
  credentialMode: 'keep' | 'primary' | 'none' | 'independent'
  /** 独立凭据键（非机密）：服务端用它把新密钥绑定到这条路由。 */
  credentialKey: string
  /** 只在本次保存发送的新密钥；留空表示沿用已绑定的密钥，永不回显。 */
  credentialSecret: string
}

/** Display strings are intentionally never eligible for writeback. */
export function editableText(field?: ZbaReadField<string>): string | null {
  if (!field?.rule.editable) return null
  if (field.visibility === 2 && typeof field.nameRaw === 'string') return field.nameRaw
  if (field.visibility === 1 && typeof field.value === 'string') return field.value
  return null
}

export function providerDraft(provider?: ZbaProviderConfigurationView): ProviderDraft {
  return {
    name: editableText(provider?.name) ?? '',
    baseUrl: editableText(provider?.baseUrl) ?? '',
    modelName: editableText(provider?.modelName) ?? '',
    priority: String(provider?.priority ?? 0),
    enabled: provider?.desiredEnabled ?? false,
    protocol: protocolFromWire(provider?.protocol),
    azureDeployment: provider?.options?.azureDeployment ?? '',
    azureApiVersion: provider?.options?.azureApiVersion ?? '2024-10-21',
    anthropicVersion: provider?.options?.anthropicVersion ?? '2023-06-01',
    credentialMode: 'keep',
    credentialKey: provider?.credentialKey ?? '',
    credentialSecret: ''
  }
}

export function providerDraftErrors(draft: ProviderDraft, provider?: ZbaProviderConfigurationView): string[] {
  const errors: string[] = []
  if ((!provider || editableText(provider.name) !== null) && !draft.name.trim()) errors.push('请填写提供商名称。')
  if (!provider || editableText(provider.baseUrl) !== null) {
    const invalid = endpointError(draft.baseUrl.trim())
    if (invalid) errors.push(invalid)
  }
  if ((!provider || editableText(provider.modelName) !== null) && !draft.modelName.trim()) errors.push('请填写模型名称。')
  if (!draft.priority.trim() || !Number.isSafeInteger(Number(draft.priority))) errors.push('优先级必须是整数。')
  if (!isProviderProtocol(draft.protocol)) errors.push('请选择受支持的协议。')
  if (draft.protocol === 'azure-openai' && (!draft.azureDeployment.trim() || !draft.azureApiVersion.trim())) errors.push('Azure 需要部署名与 API 版本。')
  if (draft.protocol === 'anthropic-messages' && !draft.anthropicVersion.trim()) errors.push('请填写 Anthropic API 版本。')
  if (draft.credentialMode === 'independent') {
    const key = draft.credentialKey.trim()
    if (!key) errors.push('独立凭据需要填写凭据键（credentialKey）。')
    else if (/\s/.test(key)) errors.push('凭据键不能包含空白字符。')
    else if (key.length > 128) errors.push('凭据键不能超过 128 个字符。')
  }
  return errors
}

export function providerEdit(key: string, draft: ProviderDraft, provider?: ZbaProviderConfigurationView, remove = false): ZbaProviderNameEdit {
  const keep = (): ZbaFieldEdit => ({ kind: 0 })
  const text = (raw: string, field?: ZbaReadField<string>): ZbaFieldEdit => {
    if (remove || (provider && editableText(field) === null)) return keep()
    return !provider || raw.trim() !== editableText(field) ? { kind: 1, value: raw.trim() } : keep()
  }
  const field = (value: unknown, previous: unknown): ZbaFieldEdit => remove || (provider && value === previous) ? keep() : { kind: 1, value }
  const options = {
    azureDeployment: draft.azureDeployment.trim() || null,
    azureApiVersion: draft.azureApiVersion.trim(),
    anthropicVersion: draft.anthropicVersion.trim()
  }
  const before = providerDraft(provider)
  const optionsChanged = draft.azureDeployment !== before.azureDeployment || draft.azureApiVersion !== before.azureApiVersion || draft.anthropicVersion !== before.anthropicVersion
  // kind 3 = 先按 credentialKey 绑定独立凭据，再由 secrets 里的同名 key 送出新密钥。
  const credential = (): { kind: 0 | 1 | 2 | 3; key?: string } => {
    if (remove || draft.credentialMode === 'keep') return { kind: 0 }
    if (draft.credentialMode === 'primary') return { kind: 1 }
    if (draft.credentialMode === 'none') return { kind: 2 }
    return { kind: 3, key: draft.credentialKey.trim() }
  }
  return {
    key, operation: remove ? 2 : provider ? 0 : 1,
    changes: {
      nameRaw: text(draft.name, provider?.name), baseUrl: text(draft.baseUrl, provider?.baseUrl),
      modelName: text(draft.modelName, provider?.modelName),
      priority: field(Number(draft.priority), provider?.priority),
      // Creation never also enables routing. This requires a separate explicit save.
      enabled: field(provider ? draft.enabled : false, provider?.desiredEnabled),
      protocol: field(protocolToWire(draft.protocol), provider?.protocol ?? 0),
      options: remove || (provider && !optionsChanged) ? keep() : { kind: 1, value: options },
      credential: credential()
    }
  }
}

/**
 * 独立凭据的密钥走 secrets 而不是 changes：值只在本次请求里出现，
 * 响应与后续读取都只有 credentialConfigured，绝不回显。
 */
export function providerSecrets(draft: ProviderDraft, remove = false): ZbaSecretEdit[] {
  const key = draft.credentialKey.trim()
  if (remove || draft.credentialMode !== 'independent' || !key || !draft.credentialSecret) return []
  return [{ kind: 1, value: draft.credentialSecret, credentialKey: key }]
}
