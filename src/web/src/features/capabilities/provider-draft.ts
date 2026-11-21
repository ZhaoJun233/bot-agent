import type {
  ZbaFieldEdit,
  ZbaProviderConfigurationView,
  ZbaProviderNameEdit,
  ZbaProviderOptions,
  ZbaReadField,
  ZbaSecretEdit,
  ZbaSecretMetadata
} from '../../api/zba'
import { endpointError, isProviderProtocol, protocolFromWire, protocolToWire } from './provider-protocols'

export interface ModelCredentialSummary {
  key: string
  configured: boolean
  keyUsable: boolean
}

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
  /** 是否让通道发送供应商缓存控制参数；默认关。 */
  promptCache: boolean
  credentialMode: 'keep' | 'primary' | 'none' | 'independent'
  /** 主供应商内联槽位 0（ModelApiKey）操作；仅 primary 使用。 */
  primarySecretAction: 'keep' | 'replace' | 'delete'
  /** 独立凭据键（非机密）：默认由 providerKey 自动派生，也可在高级区复用已有键。 */
  credentialKey: string
  /** 只在本次保存发送的新密钥；永不回显。 */
  credentialSecret: string
  /** 停用非 primary 供应商解绑时，同时显式删除该独立密钥行。 */
  deleteIndependentSecret: boolean
}

/** Display strings are intentionally never eligible for writeback. */
export function editableText(field?: ZbaReadField<string>): string | null {
  if (!field?.rule.editable) return null
  if (field.visibility === 2 && typeof field.nameRaw === 'string') return field.nameRaw
  if (field.visibility === 1 && typeof field.value === 'string') return field.value
  return null
}

/** 根据供应商标识自动派生合规的独立凭据键，用户不必手写内部技术键。 */
export function autoCredentialKey(providerKey: string): string {
  const sanitized = providerKey.trim().replace(/[^a-zA-Z0-9._-]+/g, '-').replace(/^-+|-+$/g, '')
  const base = sanitized ? `key-${sanitized}` : 'key-provider'
  return base.slice(0, 128)
}

export function isUrlLikeSecret(raw: string): boolean {
  const trimmed = raw.trim().toLowerCase()
  return trimmed.startsWith('http://') || trimmed.startsWith('https://')
}

export function providerDraft(provider?: ZbaProviderConfigurationView, providerKey = ''): ProviderDraft {
  const protocol = protocolFromWire(provider?.protocol)
  const isNew = !provider
  const defaultKey = provider?.credentialKey ?? (providerKey ? autoCredentialKey(providerKey) : '')
  return {
    name: editableText(provider?.name) ?? '',
    baseUrl: editableText(provider?.baseUrl) ?? '',
    modelName: editableText(provider?.modelName) ?? '',
    priority: String(provider?.priority ?? 0),
    enabled: provider?.desiredEnabled ?? false,
    protocol,
    azureDeployment: provider?.options?.azureDeployment ?? '',
    azureApiVersion: provider?.options?.azureApiVersion ?? '2024-10-21',
    anthropicVersion: provider?.options?.anthropicVersion ?? '2023-06-01',
    promptCache: provider?.options?.promptCache ?? false,
    // 新建供应商默认走独立 API Key（Ollama 默认无需密钥），避免默认 keep 导致后端报 credential_required。
    credentialMode: isNew ? (protocol === 'ollama' ? 'none' : 'independent') : 'keep',
    primarySecretAction: 'keep',
    credentialKey: defaultKey,
    credentialSecret: '',
    deleteIndependentSecret: false
  }
}

export function providerDraftErrors(
  draft: ProviderDraft,
  provider?: ZbaProviderConfigurationView,
  options?: {
    isPrimary?: boolean
    modelCredentials?: readonly ModelCredentialSummary[]
  }
): string[] {
  const errors: string[] = []
  const isPrimary = options?.isPrimary ?? provider?.key === 'primary'
  const knownCredentials = options?.modelCredentials ?? []
  if ((!provider || editableText(provider.name) !== null) && !draft.name.trim()) errors.push('请填写提供商名称。')
  if (!provider || editableText(provider.baseUrl) !== null) {
    const invalid = endpointError(draft.baseUrl.trim())
    if (invalid) errors.push(invalid)
  }
  if ((!provider || editableText(provider.modelName) !== null) && !draft.modelName.trim()) errors.push('请填写模型名称。')
  if (!isPrimary && (!draft.priority.trim() || !Number.isSafeInteger(Number(draft.priority)))) errors.push('优先级必须是整数。')
  if (!isProviderProtocol(draft.protocol)) errors.push('请选择受支持的协议。')
  if (draft.protocol === 'azure-openai' && (!draft.azureDeployment.trim() || !draft.azureApiVersion.trim())) errors.push('Azure 需要部署名与 API 版本。')
  if (draft.protocol === 'anthropic-messages' && !draft.anthropicVersion.trim()) errors.push('请填写 Anthropic API 版本。')

  if (isPrimary && draft.credentialMode === 'keep' && draft.primarySecretAction === 'replace') {
    if (!draft.credentialSecret.trim()) errors.push('请输入新的主模型 API Key。')
    else if (isUrlLikeSecret(draft.credentialSecret)) errors.push('API Key 不能填写 URL，请填入服务商密钥。')
  }

  if (draft.credentialMode === 'independent') {
    const key = draft.credentialKey.trim()
    if (!key) errors.push('独立凭据需要有效的凭据标识（credentialKey）。')
    else if (/\s/.test(key)) errors.push('凭据标识不能包含空白字符。')
    else if (key.length > 128) errors.push('凭据标识不能超过 128 个字符。')

    const secret = draft.credentialSecret.trim()
    if (secret && isUrlLikeSecret(secret)) {
      errors.push('API Key 不能填写 URL，请填入服务商密钥。')
    } else if (!secret) {
      const existingSameKey = Boolean(provider?.credentialKey && provider.credentialKey === key && provider.credentialConfigured)
      const reusableKey = knownCredentials.some(item => item.key === key && item.configured && item.keyUsable)
      if (!existingSameKey && !reusableKey) {
        errors.push('请填写 API Key，或选择已存在的凭据标识。')
      }
    }
  }

  return errors
}

export function providerEdit(
  key: string,
  draft: ProviderDraft,
  provider?: ZbaProviderConfigurationView,
  remove = false
): ZbaProviderNameEdit {
  const isPrimary = Boolean(provider && key === 'primary')
  const keep = (): ZbaFieldEdit => ({ kind: 0 })
  const text = (raw: string, field?: ZbaReadField<string>): ZbaFieldEdit => {
    if (remove || (provider && editableText(field) === null)) return keep()
    return !provider || raw.trim() !== editableText(field) ? { kind: 1, value: raw.trim() } : keep()
  }
  const field = (value: unknown, previous: unknown): ZbaFieldEdit => remove || (provider && value === previous) ? keep() : { kind: 1, value }
  const options: ZbaProviderOptions = {
    ...provider?.options,
    azureDeployment: draft.azureDeployment.trim() || null,
    azureApiVersion: draft.azureApiVersion.trim(),
    anthropicVersion: draft.anthropicVersion.trim()
  }
  // promptCache 默认关：未勾选且原本没有该字段时整段省略，提交内容与加开关之前逐字一致。
  if (draft.promptCache) options.promptCache = true
  else if (provider?.options?.promptCache === true) options.promptCache = false
  else if (!provider?.options || !('promptCache' in provider.options)) delete options.promptCache
  const before = providerDraft(provider, key)
  const optionsChanged = draft.azureDeployment !== before.azureDeployment
    || draft.azureApiVersion !== before.azureApiVersion
    || draft.anthropicVersion !== before.anthropicVersion
    || draft.promptCache !== before.promptCache
  // kind 3 = 绑定独立凭据；同批 secrets 里带同名 credentialKey 即可一步原子写入密钥。
  const credential = (): { kind: 0 | 1 | 2 | 3; key?: string } => {
    if (remove || draft.credentialMode === 'keep') return { kind: 0 }
    if (draft.credentialMode === 'primary') return { kind: 1 }
    if (draft.credentialMode === 'none') return { kind: 2 }
    const trimmedKey = draft.credentialKey.trim()
    // 编辑已有供应商且沿用同一独立凭据键只换密钥时，绑定本身未变更，保留 kind 0。
    if (provider && provider.credential === 2 && provider.credentialKey === trimmedKey) return { kind: 0 }
    return { kind: 3, key: trimmedKey }
  }
  return {
    key,
    operation: remove ? 2 : provider ? 0 : 1,
    changes: {
      nameRaw: text(draft.name, provider?.name),
      baseUrl: text(draft.baseUrl, provider?.baseUrl),
      modelName: text(draft.modelName, provider?.modelName),
      priority: isPrimary ? keep() : field(Number(draft.priority), provider?.priority),
      // Creation never also enables routing; primary enabled state is read-only.
      enabled: isPrimary ? keep() : field(provider ? draft.enabled : false, provider?.desiredEnabled),
      protocol: field(protocolToWire(draft.protocol), provider?.protocol ?? 0),
      options: remove || (provider && !optionsChanged) ? keep() : { kind: 1, value: options },
      credential: credential()
    }
  }
}

/** 判断本次保存是否包含供应商元数据变更（用于区分纯密钥替换/删除）。 */
export function hasProviderMetadataChanges(
  key: string,
  draft: ProviderDraft,
  provider?: ZbaProviderConfigurationView,
  remove = false
): boolean {
  if (remove || !provider) return true
  const edit = providerEdit(key, draft, provider, false)
  const c = edit.changes
  return c.nameRaw.kind !== 0
    || c.baseUrl.kind !== 0
    || c.modelName.kind !== 0
    || c.priority.kind !== 0
    || c.enabled.kind !== 0
    || (c.protocol !== undefined && c.protocol.kind !== 0)
    || (c.options !== undefined && c.options.kind !== 0)
    || c.credential.kind !== 0
}

/**
 * 供应商凭据的密钥编辑统一走 secrets：值只在本次请求里发送一次，绝不回显。
 * - primary 在默认模式下直接操作固定槽位 0（ModelApiKey）的替换或删除；
 * - 独立凭据通过 credentialKey 原子写入/替换或显式删除。
 */
export function providerSecrets(
  draft: ProviderDraft,
  remove = false,
  provider?: ZbaProviderConfigurationView,
  isPrimary = provider?.key === 'primary'
): ZbaSecretEdit[] {
  if (remove) return []
  const edits: ZbaSecretEdit[] = []

  if (isPrimary && draft.credentialMode === 'keep') {
    if (draft.primarySecretAction === 'replace' && draft.credentialSecret.trim()) {
      edits.push({ slot: 0, kind: 1, value: draft.credentialSecret.trim() })
    } else if (draft.primarySecretAction === 'delete') {
      edits.push({ slot: 0, kind: 2 })
    }
    return edits
  }

  if (draft.credentialMode === 'independent') {
    const key = draft.credentialKey.trim()
    const secret = draft.credentialSecret.trim()
    if (key && secret) edits.push({ kind: 1, value: secret, credentialKey: key })
    return edits
  }

  if (draft.credentialMode === 'none' && draft.deleteIndependentSecret && provider?.credential === 2 && provider.credentialKey) {
    edits.push({ kind: 2, credentialKey: provider.credentialKey })
  }

  return edits
}

/** 统一计算卡片、详情抽屉与编辑抽屉的凭据实时状态文案。 */
export function providerCredentialSummary(
  provider: ZbaProviderConfigurationView | undefined,
  primarySlot: ZbaSecretMetadata | undefined,
  descriptorConfigured = false
): { label: string; detail: string; configured: boolean } {
  const sourceLabel = (source?: number): string => {
    if (source === 1) return '数据库'
    if (source === 2) return '环境变量'
    return '未配置'
  }
  if (!provider) {
    return {
      label: descriptorConfigured ? '已配置' : '未配置',
      detail: descriptorConfigured ? '已配置凭据' : '尚未配置凭据',
      configured: descriptorConfigured
    }
  }
  const isPrimary = provider.key === 'primary'
  if (provider.credential === 1) {
    const configured = primarySlot ? primarySlot.configured : (provider.credentialConfigured ?? descriptorConfigured)
    const source = primarySlot ? sourceLabel(primarySlot.source) : (configured ? '已配置' : '未配置')
    if (isPrimary) {
      return {
        label: configured ? `主 Key · ${source}` : '未配置',
        detail: configured ? `已配置主模型 API Key（来源：${source}）` : '尚未配置主模型 API Key',
        configured
      }
    }
    return {
      label: configured ? `共用主 Key（${source}）` : '共用主 Key（未配置）',
      detail: configured ? `共用主供应商（primary）API Key（来源：${source}）` : '绑定到主供应商凭据，但主 API Key 尚未配置',
      configured
    }
  }
  if (provider.credential === 2) {
    const configured = Boolean(provider.credentialConfigured)
    return {
      label: configured ? '独立 Key · 已配置' : '独立 Key · 未配置',
      detail: configured
        ? `已绑定独立 API Key${provider.credentialKey ? `（标识：${provider.credentialKey}）` : ''}`
        : `已绑定独立凭据标识${provider.credentialKey ? `（${provider.credentialKey}）` : ''}，但密钥缺失`,
      configured
    }
  }
  if (provider.credential === 0) {
    if (provider.protocol === 5) {
      return { label: '无需密钥（Ollama）', detail: '本地 Ollama 协议无需 API Key', configured: true }
    }
    return { label: '未配置', detail: '尚未配置 API Key', configured: false }
  }
  return { label: '需人工核对', detail: '旧凭据引用无法识别，请重新选择凭据模式', configured: false }
}
