import type {
  ZbaConfigurationPatch,
  ZbaConfigurationView,
  ZbaConversationCapabilityOverride,
  ZbaConversationPacingOverride,
  ZbaConversationProfile,
  ZbaConversationProfileEdit,
  ZbaConversationProfileKey,
  ZbaConversationProfileView,
  ZbaConversationStyleOverride,
  ZbaConversationToolTier,
  ZbaDriverKey,
  ZbaFeatureOverride,
  ZbaPlatformPolicyEntryView
} from '../../api/zba'

/** Phase 3 实际生效的 11 项会话级功能覆盖（含文档解析、会话纪要、知识库与社区记忆）。 */
export const CONVERSATION_PROFILE_FEATURES = [
  { id: 2, label: '语音回复' },
  { id: 6, label: '表情包' },
  { id: 7, label: '听歌 / 点歌' },
  { id: 8, label: '戳一戳' },
  { id: 9, label: '链接预览' },
  { id: 10, label: '联网搜索' },
  { id: 11, label: '文档解析' },
  { id: 12, label: '会话纪要' },
  { id: 13, label: '轻量知识库' },
  { id: 14, label: '情景记忆' },
  { id: 15, label: '圈子黑话' },
  { id: 16, label: '定时与提醒' },
  { id: 17, label: '个人待办' }
] as const

export const CONVERSATION_TOOL_TIERS: ReadonlyArray<{ value: ZbaConversationToolTier; label: string; hint: string }> = [
  { value: 0, label: '继承全局默认', hint: '沿用全局开关与能力白名单' },
  { value: 1, label: '仅聊天与基础媒体（ChatOnly）', hint: '禁用联网搜索、工具目录、自然 Agent 与审批/提问工具' },
  { value: 2, label: '允许只读工具（ReadOnlyTools）', hint: '放行搜索与只读工具，阻断自然 Agent 与审批写请求' },
  { value: 3, label: '允许受控工具（ManagedTools）', hint: '在既有白名单与审批闸门约束下允许自然 Agent 与审批请求' }
]

export interface ConversationProfileRowDraft {
  key: ZbaConversationProfileKey
  displayLabel: string
  personaOverride: string
  personaAppend: string
  /** 'inherit' | 'on' | 'off' */
  filterActionNarration: 'inherit' | 'on' | 'off'
  voiceMaxChars: string
  voiceEagerness: string
  groupInterjectionProbability: string
  groupInterjectionCooldownSeconds: string
  groupContinuationWaitSeconds: string
  privateContinuationWaitSeconds: string
  suitabilityThreshold: string
  maxContextMessages: string
  /** 0=继承 1=启用 2=禁用 */
  proactiveEnabled: number
  proactiveQuietSeconds: string
  features: ZbaFeatureOverride[]
  toolTier: ZbaConversationToolTier
}

export type ConversationProfileDraftEntry =
  | { kind: 'upsert'; draft: ConversationProfileRowDraft }
  | { kind: 'delete'; key: ZbaConversationProfileKey }

export type ConversationProfileDrafts = Record<string, ConversationProfileDraftEntry>

export interface ConversationProfilePreset {
  id: 'active-group' | 'restrained-group' | 'personal-assistant'
  label: string
  summary: string
  apply: (draft: ConversationProfileRowDraft) => ConversationProfileRowDraft
}

/** 三套纯前端一键预填模板（全部对应真实生效字段，提交后仍可逐项微调）。 */
export const CONVERSATION_PROFILE_PRESETS: ReadonlyArray<ConversationProfilePreset> = [
  {
    id: 'active-group',
    label: '活跃综合群',
    summary: '插话 20% / 冷却 30s，开启语音、表情包、戳一戳、点歌、搜索、链接预览、文档解析、会话纪要、知识库与社区记忆，只读工具档位',
    apply: (draft) => ({
      ...draft,
      groupInterjectionProbability: '20',
      groupInterjectionCooldownSeconds: '30',
      proactiveEnabled: 0,
      features: [
        { feature: 2, enabled: true },
        { feature: 6, enabled: true },
        { feature: 7, enabled: true },
        { feature: 8, enabled: true },
        { feature: 9, enabled: true },
        { feature: 10, enabled: true },
        { feature: 11, enabled: true },
        { feature: 12, enabled: true },
        { feature: 13, enabled: true },
        { feature: 14, enabled: true },
        { feature: 15, enabled: true }
      ],
      toolTier: 2
    })
  },
  {
    id: 'restrained-group',
    label: '克制协作群',
    summary: '插话 0%（仅 @/引用触发），关闭表情包/戳一戳/点歌/主动发言/黑话，开启搜索、链接预览、文档解析、会话纪要、知识库与情景记忆，追加简明专业风格',
    apply: (draft) => ({
      ...draft,
      personaAppend: '简明直接、结论先行、先给要点再给细节。',
      groupInterjectionProbability: '0',
      proactiveEnabled: 2,
      features: [
        { feature: 6, enabled: false },
        { feature: 7, enabled: false },
        { feature: 8, enabled: false },
        { feature: 9, enabled: true },
        { feature: 10, enabled: true },
        { feature: 11, enabled: true },
        { feature: 12, enabled: true },
        { feature: 13, enabled: true },
        { feature: 14, enabled: true },
        { feature: 15, enabled: false }
      ],
      toolTier: 2
    })
  },
  {
    id: 'personal-assistant',
    label: '全能私人助手',
    summary: '私聊续话等待 1.5s，开启语音、搜索、链接预览、文档解析、会话纪要、知识库与情景记忆，启用受控工具档位（仍受既有授权与审批闸门约束）',
    apply: (draft) => ({
      ...draft,
      privateContinuationWaitSeconds: '1.5',
      proactiveEnabled: 0,
      features: [
        { feature: 2, enabled: true },
        { feature: 9, enabled: true },
        { feature: 10, enabled: true },
        { feature: 11, enabled: true },
        { feature: 12, enabled: true },
        { feature: 13, enabled: true },
        { feature: 14, enabled: true },
        { feature: 15, enabled: false },
        { feature: 16, enabled: true },
        { feature: 17, enabled: true }
      ],
      toolTier: 3
    })
  }
]

export function conversationProfileId(key: ZbaConversationProfileKey): string {
  return `${key.driver.platformId}\u001F${key.driver.accountScope}\u001F${key.driver.instanceId}\u001F${key.conversationKey}`
}

export function conversationKeyHint(platformId: string): { placeholder: string; rule: string } {
  switch (platformId) {
    case 'qq.private':
      return { placeholder: 'group:10001 或 private:20001', rule: '仅接受 group:<数字> 或 private:<数字>' }
    case 'qq.official':
      return { placeholder: 'official:group:80001 或 official:private:80002', rule: '仅接受 official:group:... 或 official:private:...' }
    case 'feishu':
      return { placeholder: 'feishu:group:oc_10001 或 feishu:private:ou_20001', rule: '仅接受 feishu:group:... 或 feishu:private:...' }
    case 'local':
      return { placeholder: 'local:room-1', rule: '仅接受 local:<非空标识>' }
    default:
      return { placeholder: '输入完整会话键', rule: '必须匹配当前平台前缀且不含空白字符' }
  }
}

export function validateConversationKeyPrefix(platformId: string, conversationKey: string): string | null {
  const value = conversationKey.trim()
  if (value.length === 0) return '请输入会话键（conversationKey）。'
  if (value.length > 128) return '会话键长度不能超过 128 个字符。'
  if (/\s/u.test(value)) return '会话键不能包含空格或换行。'
  if (platformId === 'qq.private') {
    return /^(group|private):\d+$/u.test(value) ? null : 'QQ 私域会话键必须形如 group:10001 或 private:20001。'
  }
  if (platformId === 'qq.official') {
    return /^official:(group|private):.+$/u.test(value) ? null : 'QQ 官方会话键必须以 official:group: 或 official:private: 开头。'
  }
  if (platformId === 'feishu') {
    return /^feishu:(group|private):.+$/u.test(value) ? null : '飞书会话键必须以 feishu:group: 或 feishu:private: 开头。'
  }
  if (platformId === 'local') {
    return /^local:.+$/u.test(value) ? null : '本地通道会话键必须以 local: 开头。'
  }
  return '当前平台不支持新增会话档案。'
}

export function conversationProfileViews(view: ZbaConfigurationView | undefined): ZbaConversationProfileView[] {
  const list = view?.registered?.conversationProfiles
  return Array.isArray(list) ? list : []
}

export function filterProfilesByPlatform(
  views: readonly ZbaConversationProfileView[],
  platformId: string | null | undefined,
  driverKey?: ZbaDriverKey | null
): ZbaConversationProfileView[] {
  if (driverKey) {
    return views.filter(
      (item) =>
        item.key.driver.platformId === driverKey.platformId
        && item.key.driver.accountScope === driverKey.accountScope
        && item.key.driver.instanceId === driverKey.instanceId
    )
  }
  if (!platformId) return [...views]
  return views.filter((item) => item.key.driver.platformId === platformId)
}

export function defaultDriverForPlatform(
  platformId: string | null | undefined,
  policyRows: readonly ZbaPlatformPolicyEntryView[]
): ZbaDriverKey | null {
  if (!platformId) return null
  const instance = policyRows.find((row) => row.target.kind === 'instance' && row.target.key.platformId === platformId)
  if (instance && instance.target.kind === 'instance') return { ...instance.target.key }
  const defaultInstanceId = platformId === 'feishu' ? 'feishu-main' : ''
  const account = policyRows.find((row) => row.target.kind === 'accountDefault' && row.target.platformId === platformId)
  if (account && account.target.kind === 'accountDefault') {
    return {
      platformId: account.target.platformId,
      accountScope: account.target.accountScope,
      instanceId: defaultInstanceId
    }
  }
  return {
    platformId,
    accountScope: platformId === 'feishu' ? 'default' : 'legacy',
    instanceId: defaultInstanceId
  }
}

export function emptyConversationProfileDraft(key: ZbaConversationProfileKey, displayLabel = ''): ConversationProfileRowDraft {
  return {
    key: { driver: { ...key.driver }, conversationKey: key.conversationKey },
    displayLabel,
    personaOverride: '',
    personaAppend: '',
    filterActionNarration: 'inherit',
    voiceMaxChars: '',
    voiceEagerness: '',
    groupInterjectionProbability: '',
    groupInterjectionCooldownSeconds: '',
    groupContinuationWaitSeconds: '',
    privateContinuationWaitSeconds: '',
    suitabilityThreshold: '',
    maxContextMessages: '',
    proactiveEnabled: 0,
    proactiveQuietSeconds: '',
    features: [],
    toolTier: 0
  }
}

export function conversationProfileBaseline(view: ZbaConversationProfileView): ConversationProfileRowDraft {
  const style = view.style ?? null
  const pacing = view.pacing ?? null
  const caps = view.capabilities ?? null
  return {
    key: { driver: { ...view.key.driver }, conversationKey: view.key.conversationKey },
    displayLabel: view.displayLabelRaw ?? '',
    personaOverride: style?.personaOverride ?? '',
    personaAppend: style?.personaAppend ?? '',
    filterActionNarration:
      style?.filterActionNarration === true ? 'on' : style?.filterActionNarration === false ? 'off' : 'inherit',
    voiceMaxChars: style?.voiceMaxChars != null ? String(style.voiceMaxChars) : '',
    voiceEagerness: style?.voiceEagerness != null ? String(style.voiceEagerness) : '',
    groupInterjectionProbability:
      pacing?.groupInterjectionProbability != null ? String(pacing.groupInterjectionProbability) : '',
    groupInterjectionCooldownSeconds:
      pacing?.groupInterjectionCooldownSeconds != null ? String(pacing.groupInterjectionCooldownSeconds) : '',
    groupContinuationWaitSeconds:
      pacing?.groupContinuationWaitSeconds != null ? String(pacing.groupContinuationWaitSeconds) : '',
    privateContinuationWaitSeconds:
      pacing?.privateContinuationWaitSeconds != null ? String(pacing.privateContinuationWaitSeconds) : '',
    suitabilityThreshold: pacing?.suitabilityThreshold != null ? String(pacing.suitabilityThreshold) : '',
    maxContextMessages: pacing?.maxContextMessages != null ? String(pacing.maxContextMessages) : '',
    proactiveEnabled: pacing?.proactiveEnabled ?? 0,
    proactiveQuietSeconds: pacing?.proactiveQuietSeconds != null ? String(pacing.proactiveQuietSeconds) : '',
    features: (caps?.features ?? []).map((item) => ({ feature: item.feature, enabled: item.enabled })),
    toolTier: caps?.toolTier ?? 0
  }
}

function draftSignature(row: ConversationProfileRowDraft): string {
  return JSON.stringify([
    row.key,
    row.displayLabel.trim(),
    row.personaOverride.trim(),
    row.personaAppend.trim(),
    row.filterActionNarration,
    row.voiceMaxChars.trim(),
    row.voiceEagerness.trim(),
    row.groupInterjectionProbability.trim(),
    row.groupInterjectionCooldownSeconds.trim(),
    row.groupContinuationWaitSeconds.trim(),
    row.privateContinuationWaitSeconds.trim(),
    row.suitabilityThreshold.trim(),
    row.maxContextMessages.trim(),
    row.proactiveEnabled,
    row.proactiveQuietSeconds.trim(),
    [...row.features].sort((a, b) => a.feature - b.feature),
    row.toolTier
  ])
}

export function conversationProfileDirty(
  draft: ConversationProfileRowDraft | undefined,
  baseline: ConversationProfileRowDraft | undefined
): boolean {
  if (!draft) return false
  if (!baseline) return true
  return draftSignature(draft) !== draftSignature(baseline)
}

function parseOptionalInt(raw: string, min: number, max: number, label: string, errors: string[]): number | null {
  const trimmed = raw.trim()
  if (trimmed.length === 0) return null
  if (!/^-?\d+$/u.test(trimmed)) {
    errors.push(`${label}必须是整数。`)
    return null
  }
  const value = Number(trimmed)
  if (!Number.isInteger(value) || value < min || value > max) {
    errors.push(`${label}必须在 ${min}..${max} 之间。`)
    return null
  }
  return value
}

function parseOptionalFloat(raw: string, min: number, max: number, label: string, errors: string[]): number | null {
  const trimmed = raw.trim()
  if (trimmed.length === 0) return null
  const value = Number(trimmed)
  if (!Number.isFinite(value) || value < min || value > max) {
    errors.push(`${label}必须在 ${min}..${max} 之间。`)
    return null
  }
  return value
}

function toWireProfile(draft: ConversationProfileRowDraft, errors: string[]): ZbaConversationProfile {
  const prefixError = validateConversationKeyPrefix(draft.key.driver.platformId, draft.key.conversationKey)
  if (prefixError) errors.push(`${draft.key.conversationKey || '新档案'}：${prefixError}`)

  const label = draft.displayLabel.trim()
  if (label.length > 120) errors.push(`${draft.key.conversationKey}：备注名称不能超过 120 字。`)

  const personaOverride = draft.personaOverride.trim() || null
  const personaAppend = draft.personaAppend.trim() || null
  if (personaOverride && personaOverride.length > 1200) {
    errors.push(`${draft.key.conversationKey}：覆盖人设不能超过 1200 字。`)
  }
  if (personaAppend && personaAppend.length > 600) {
    errors.push(`${draft.key.conversationKey}：补充人设不能超过 600 字。`)
  }
  if (personaOverride && personaAppend && personaOverride.length + personaAppend.length > 1200) {
    errors.push(`${draft.key.conversationKey}：覆盖人设与补充人设合计不能超过 1200 字。`)
  }

  const filterActionNarration =
    draft.filterActionNarration === 'on' ? true : draft.filterActionNarration === 'off' ? false : null
  const voiceMaxChars = parseOptionalInt(draft.voiceMaxChars, 10, 300, `${draft.key.conversationKey} 语音字数上限`, errors)
  const voiceEagerness = parseOptionalInt(draft.voiceEagerness, 0, 100, `${draft.key.conversationKey} 语音积极性`, errors)

  const style: ZbaConversationStyleOverride | null =
    personaOverride !== null || personaAppend !== null || filterActionNarration !== null
    || voiceMaxChars !== null || voiceEagerness !== null
      ? { personaOverride, personaAppend, filterActionNarration, voiceMaxChars, voiceEagerness }
      : null

  const groupInterjectionProbability = parseOptionalInt(draft.groupInterjectionProbability, 0, 100, `${draft.key.conversationKey} 插话概率`, errors)
  const groupInterjectionCooldownSeconds = parseOptionalInt(draft.groupInterjectionCooldownSeconds, 0, 3600, `${draft.key.conversationKey} 群冷却秒数`, errors)
  const groupContinuationWaitSeconds = parseOptionalInt(draft.groupContinuationWaitSeconds, 0, 30, `${draft.key.conversationKey} 群续话等待`, errors)
  const privateContinuationWaitSeconds = parseOptionalFloat(draft.privateContinuationWaitSeconds, 0, 30, `${draft.key.conversationKey} 私聊续话等待`, errors)
  const suitabilityThreshold = parseOptionalInt(draft.suitabilityThreshold, 0, 100, `${draft.key.conversationKey} 发言适宜度阈值`, errors)
  const maxContextMessages = parseOptionalInt(draft.maxContextMessages, 4, 60, `${draft.key.conversationKey} 上下文消息条数`, errors)
  const proactiveQuietSeconds = parseOptionalInt(draft.proactiveQuietSeconds, 30, 3600, `${draft.key.conversationKey} 主动发言安静时长`, errors)

  const pacing: ZbaConversationPacingOverride | null =
    groupInterjectionProbability !== null || groupInterjectionCooldownSeconds !== null
    || groupContinuationWaitSeconds !== null || privateContinuationWaitSeconds !== null
    || suitabilityThreshold !== null || maxContextMessages !== null
    || draft.proactiveEnabled !== 0 || proactiveQuietSeconds !== null
      ? {
          groupInterjectionProbability,
          groupInterjectionCooldownSeconds,
          groupContinuationWaitSeconds,
          privateContinuationWaitSeconds,
          suitabilityThreshold,
          maxContextMessages,
          proactiveEnabled: draft.proactiveEnabled,
          proactiveQuietSeconds
        }
      : null

  const sortedFeatures = [...draft.features].sort((a, b) => a.feature - b.feature)
  const capabilities: ZbaConversationCapabilityOverride | null =
    sortedFeatures.length > 0 || draft.toolTier !== 0
      ? { features: sortedFeatures, toolTier: draft.toolTier }
      : null

  return {
    key: { driver: { ...draft.key.driver }, conversationKey: draft.key.conversationKey.trim() },
    displayLabel: label,
    style,
    pacing,
    capabilities
  }
}

export function applyConversationProfileEdits(
  patch: ZbaConfigurationPatch,
  drafts: ConversationProfileDrafts,
  view: ZbaConfigurationView | undefined
): string[] {
  const errors: string[] = []
  const known = new Map(conversationProfileViews(view).map((item) => [conversationProfileId(item.key), item]))
  const edits: ZbaConversationProfileEdit[] = []

  for (const [id, entry] of Object.entries(drafts)) {
    if (entry.kind === 'delete') {
      if (known.has(id)) {
        edits.push({ key: entry.key, operation: 1, profile: null })
      }
      continue
    }
    const existing = known.get(id)
    const baseline = existing ? conversationProfileBaseline(existing) : undefined
    if (existing && !conversationProfileDirty(entry.draft, baseline)) continue
    const wire = toWireProfile(entry.draft, errors)
    edits.push({ key: wire.key, operation: 0, profile: wire })
  }

  if (edits.length > 0) {
    const registered = patch.registered ?? { plugins: [], policies: [], providers: [] }
    patch.registered = {
      ...registered,
      conversationProfiles: [...(registered.conversationProfiles ?? []), ...edits]
    }
  }
  return errors
}
