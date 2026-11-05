import type {
  ZbaActionRuleValue,
  ZbaConfigurationPatch,
  ZbaConfigurationView,
  ZbaListRuleValue,
  ZbaPlatformPolicyEntryView,
  ZbaPolicyEdit,
  ZbaPolicyTarget
} from '../../api/zba'

/**
 * 平台策略条目的草稿。
 *
 * 策略条目是判定的唯一真相：条目里显式写了的字段覆盖标量基线，写 Inherit 才回落到
 * 面板上的开关/白名单文本。旧内置面板有这套编辑器，S6 删除前端时一并丢了，
 * 于是「面板能改的东西不参与判定，参与判定的东西改不了」。
 *
 * 这里只描述**一条条目**的六个字段（与 ZbaPolicyConfiguration 同构），
 * 提交走 `registered.policies` 逐条 upsert，不做整表替换。
 */
export interface PolicyRowDraft {
  /** 0=跟随基线 1=启用 2=禁用。 */
  enabled: number
  chatEnabled: number
  /** 0=跟随 1=显式 2=全收。 */
  groupKind: number
  groupValue: string
  privateKind: number
  privateValue: string
  /** 0=跟随 1=显式。 */
  actionKind: number
  /** 逗号/空白分隔的动作名。 */
  actions: string
  /** 显式覆盖的功能开关；没列出的功能沿用基线。 */
  features: Array<{ feature: number; enabled: boolean }>
}

export type PolicyDrafts = Record<string, PolicyRowDraft>

/** 服务端 `FreezePolicy` 认可的动作名（unknown 会被整条拒绝）。 */
export const POLICY_ACTIONS = [
  'like', 'poke', 'emoji_like', 'recall', 'ban', 'kick', 'card', 'group_name', 'leave', 'send'
] as const

/** 11 个平台功能，顺序与 `PlatformFeatureId` 一致。 */
export const POLICY_FEATURES = [
  { id: 0, label: '文本' },
  { id: 1, label: '图片' },
  { id: 2, label: '语音' },
  { id: 3, label: '引用' },
  { id: 4, label: '撤回' },
  { id: 5, label: '话题' },
  { id: 6, label: '表情包' },
  { id: 7, label: '音乐' },
  { id: 8, label: '拍一拍' },
  { id: 9, label: '链接预览' },
  { id: 10, label: '联网搜索' }
] as const

export function policyKey(target: ZbaPolicyTarget): string {
  return target.kind === 'accountDefault'
    ? `account:${target.platformId}:${target.accountScope}`
    : `instance:${target.key.platformId}:${target.key.accountScope}:${target.key.instanceId}`
}

/** 一行条目的标题；实例目标必须写清楚 instanceId，否则和 account 默认看起来一样。 */
export function policyTitle(target: ZbaPolicyTarget): string {
  const base = `${target.kind === 'accountDefault' ? target.platformId : target.key.platformId} · ${target.kind === 'accountDefault' ? target.accountScope : target.key.accountScope}`
  if (target.kind === 'accountDefault') return base
  const instance = target.key.instanceId.trim()
  return instance.length === 0 ? `${base} · 实例（未命名）` : `${base} · 实例 ${instance}`
}

function listKind(value: ZbaListRuleValue | undefined): { kind: number; text: string } {
  if (value === undefined || value === null) return { kind: 0, text: '' }
  return { kind: value.kind ?? 0, text: value.kind === 1 ? (value.value ?? '') : '' }
}

function actionDraft(value: ZbaActionRuleValue | undefined): { kind: number; text: string } {
  if (value === undefined || value === null) return { kind: 0, text: '' }
  return { kind: value.kind ?? 0, text: (value.actions ?? []).join(', ') }
}

/** 从可编辑投影取一条条目的基线草稿。 */
export function policyBaseline(entry: ZbaPlatformPolicyEntryView): PolicyRowDraft {
  // Inherited values are resolved previews, never stored overrides.
  const group = listKind(entry.groupWhitelist.userMode === 2 ? entry.groupWhitelist.value : undefined)
  const direct = listKind(entry.privateWhitelist.userMode === 2 ? entry.privateWhitelist.value : undefined)
  const actions = actionDraft(entry.allowedActions.userMode === 2 ? entry.allowedActions.value : undefined)
  return {
    enabled: entry.enabled.userMode === 2 ? entry.enabled.value ?? 0 : 0,
    chatEnabled: entry.chatEnabled.userMode === 2 ? entry.chatEnabled.value ?? 0 : 0,
    groupKind: group.kind === 1 && !group.text.trim() ? 3 : group.kind,
    groupValue: group.text,
    privateKind: direct.kind === 1 && !direct.text.trim() ? 3 : direct.kind,
    privateValue: direct.text,
    actionKind: actions.kind,
    actions: actions.text,
    features: (entry.features.userMode === 2 ? entry.features.value ?? [] : []).map((item) => ({ feature: item.feature, enabled: item.enabled }))
  }
}

export function policyEntries(view: ZbaConfigurationView | undefined): ZbaPlatformPolicyEntryView[] {
  const field = view?.platform?.platformPolicies
  return Array.isArray(field?.value) ? field.value : []
}

export function policyBaselines(view: ZbaConfigurationView | undefined): Record<string, PolicyRowDraft> {
  const rows: Record<string, PolicyRowDraft> = {}
  for (const entry of policyEntries(view)) rows[policyKey(entry.target)] = policyBaseline(entry)
  return rows
}

function signature(row: PolicyRowDraft): string {
  return JSON.stringify([
    row.enabled, row.chatEnabled,
    row.groupKind, row.groupValue,
    row.privateKind, row.privateValue,
    row.actionKind, splitActions(row.actions),
    [...row.features].sort((left, right) => left.feature - right.feature)
  ])
}

/** 行是否真的改过；只提交改过的行，避免整表覆盖别人的改动。 */
export function policyDirty(draft: PolicyRowDraft | undefined, baseline: PolicyRowDraft | undefined): boolean {
  if (draft === undefined) return false
  if (baseline === undefined) return true
  return signature(draft) !== signature(baseline)
}

export function splitActions(text: string): string[] {
  const seen = new Set<string>()
  const result: string[] = []
  for (const token of text.split(/[\s,，]+/u)) {
    const value = token.trim()
    if (value.length === 0 || seen.has(value)) continue
    seen.add(value)
    result.push(value)
  }
  return result
}

function listRule(kind: number, text: string): ZbaListRuleValue {
  if (kind === 2) return { kind: 2 } as ZbaListRuleValue
  if (kind === 1 || kind === 3) return { kind: 1, value: kind === 3 ? '' : text }
  return { kind: 0 } as ZbaListRuleValue
}

/**
 * 把草稿合并进 patch 的 `registered.policies`。
 *
 * 只发**改过的行**；行内保持完整原始配置，绝不写入生效展示值。动作名的合法性在这一层先挡一次，
 * 免得整批保存被服务端的 `invalid_policy_actions` 拒掉。
 */
export function applyPolicyEdits(
  patch: ZbaConfigurationPatch,
  drafts: PolicyDrafts,
  view: ZbaConfigurationView | undefined
): string[] {
  const errors: string[] = []
  const entries = policyEntries(view)
  const known = new Map(entries.map((entry) => [policyKey(entry.target), entry]))
  const edits: ZbaPolicyEdit[] = []
  for (const [key, row] of Object.entries(drafts)) {
    const entry = known.get(key)
    if (entry === undefined) {
      errors.push(`策略 ${key}：条目已不存在，请刷新后重试`)
      continue
    }
    const before = policyBaseline(entry)
    if (!policyDirty(row, before)) continue
    const actions = splitActions(row.actions)
    if (row.actionKind === 0 && actions.length > 0) {
      errors.push(`策略 ${policyTitle(entry.target)}：跟随基线时不能保留动作名单`)
      continue
    }
    const unknown = actions.filter((action) => !POLICY_ACTIONS.includes(action as never))
    if (unknown.length > 0) {
      errors.push(`策略 ${policyTitle(entry.target)}：未知动作 ${unknown.join(', ')}`)
      continue
    }
    if (row.groupKind === 1 && row.groupValue.length > 4096
      || row.privateKind === 1 && row.privateValue.length > 4096) {
      errors.push(`策略 ${policyTitle(entry.target)}：名单超过 4096 字符`)
      continue
    }
    edits.push({
      target: entry.target,
      configuration: {
        enabled: row.enabled,
        chatEnabled: row.chatEnabled,
        groupWhitelist: listRule(row.groupKind, row.groupValue),
        privateWhitelist: listRule(row.privateKind, row.privateValue),
        allowedActions: row.actionKind === 1
          ? { kind: 1, actions: [...actions].sort() }
          : { kind: 0, actions: [] },
        features: row.features.map((item) => ({ feature: item.feature, enabled: item.enabled }))
      }
    })
  }
  if (edits.length > 0) {
    const registered = patch.registered ?? { plugins: [], providers: [], policies: [] }
    patch.registered = { ...registered, policies: [...registered.policies, ...edits] }
  }
  return errors
}

/** 一条条目的中文摘要，用于只读列表与提示。 */
export function policySummary(row: PolicyRowDraft): string {
  const toggle = (value: number, on: string, off: string): string => value === 1 ? on : value === 2 ? off : '跟随'
  const list = (kind: number, value: string): string => kind === 2 ? '全收' : kind === 1 ? (value.length === 0 ? '显式空（全拦）' : value) : '跟随'
  return `${toggle(row.enabled, '启用', '停用')} / ${toggle(row.chatEnabled, '可聊', '禁聊')} / 群 ${list(row.groupKind, row.groupValue)} / 私 ${list(row.privateKind, row.privateValue)}`
}
