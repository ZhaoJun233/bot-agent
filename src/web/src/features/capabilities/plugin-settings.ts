import { readonly, ref as vueRef, shallowReactive, shallowReadonly, type Component } from 'vue'
import type { ZbaConfigurationView, ZbaPatchSection, ZbaReadField, ZbaSecretSlot } from '../../api/zba'

/**
 * 插件 → 它所拥有的配置字段。
 *
 * 字段仍然存在原来的配置分区里（契约不变），这里只决定「在哪里编辑」：
 * 插件相关的设置从配置页挪到能力页的插件卡片上，配置页不再重复展示。
 * 插件 ID 与 PluginGate.CapabilityPlugins 一致；字段键来自 cfg-0.3.3 字段表。
 *
 * 后续接入新插件（预留接口）：
 * - 后端：实现 IBotPlugin，并把 ID 加进 CompositionRoot 的 declaredPlugins，
 *   能力目录里才会出现这张卡片；它的配置字段需要先进 cfg 字段表。
 * - 前端：调用 {@link registerPlugin} 登记一份 {@link PluginSettingsSpec}。
 *   登记后卡片自动出现「启用」开关（enableField）与「设置」按钮，
 *   配置页会自动把这些字段过滤掉，不需要改任何页面代码。
 */
export interface PluginFieldRef {
  section: ZbaPatchSection
  key: string
}

export type PluginConfigurationEditor =
  | { type: 'fields' }
  | { type: 'component'; load: () => Promise<{ default: Component }> }
  | { type: 'declared' }

export interface PluginSettingsSpec {
  /** 默认是插件；系统能力也通过同一个登记入口声明。 */
  kind?: 'plugin' | 'system'
  /** 省略时：有可编辑内容则使用字段编辑器，否则仅声明能力。 */
  editor?: PluginConfigurationEditor
  secrets?: ZbaSecretSlot[]
  /** 仅系统能力可声明；显式所有者和基础字段优先于分区兜底。 */
  fallbackSections?: ZbaPatchSection[]
  /** 复用现有供应商子编辑器。 */
  providers?: boolean
  /** 与后端插件 ID 一致，例如 `preset.feature.music`。 */
  pluginKey: string
  title: string
  summary: string
  /**
   * 「是否启用」开关对应的布尔字段。卡片上的开关直接写这个字段；
   * 省略时卡片不显示开关（只能通过运行时动作启停）。
   */
  enableField?: PluginFieldRef
  /** 整个分区都归这个插件（例如 voice / music）。 */
  sections?: ZbaPatchSection[]
  /** 分区里的个别字段。 */
  fields?: PluginFieldRef[]
  /** 来源：内置预设或后续接入的扩展插件（仅用于展示）。 */
  source?: 'preset' | 'extension'
}

/** 归一化后的登记结果，调用方不必再次推断默认编辑器。 */
export interface RegisteredPluginSettingsSpec extends PluginSettingsSpec {
  kind: 'plugin' | 'system'
  editor: PluginConfigurationEditor
  secrets: ZbaSecretSlot[]
}

const behavior = (...keys: string[]): PluginFieldRef[] => keys.map((key) => ({ section: 'behavior', key }))
const ref = (section: ZbaPatchSection, key: string): PluginFieldRef => ({ section, key })

const presets: PluginSettingsSpec[] = [
  {
    pluginKey: 'conversation', kind: 'system', title: '对话与参与策略',
    summary: '主动回复、参与节奏、分句、消息过滤与审批', fallbackSections: ['behavior', 'music', 'voice']
  },
  {
    pluginKey: 'models', kind: 'system', title: '模型服务',
    summary: '主模型连接、模型选择、采样参数与 API 凭据', fallbackSections: ['model'], secrets: [0], providers: true
  },
  {
    pluginKey: 'agent', kind: 'system', title: 'Agent 与工具',
    summary: '任务执行、服务器连接、工具范围与 Agent 密钥', fallbackSections: ['agent'], secrets: [1]
  },
  {
    pluginKey: 'platform', kind: 'system', title: '平台接入与访问策略',
    summary: '私域、官方与飞书接入、白名单及飞书凭据', fallbackSections: ['platform'], secrets: [3, 4, 5]
  },
  {
    pluginKey: 'reporting', kind: 'system', title: '健康报告',
    summary: '报告开关、发送时间与接收范围', fallbackSections: ['reporting']
  },
  {
    pluginKey: 'preset.feature.token-quota',
    title: 'Token 配额',
    summary: '按来源管理每日 Token 额度与用量；耗尽后进入节能模式',
    editor: { type: 'component', load: () => import('./TokenQuotaDialog.vue') }
  },
  {
    pluginKey: 'preset.feature.music',
    title: '点歌与音乐解析',
    summary: '点歌开关、音源、下载与音频分析',
    enableField: ref('music', 'enableMusic'),
    sections: ['music']
  },
  {
    pluginKey: 'preset.feature.voice',
    title: '语音合成',
    summary: 'TTS 服务、音色与语音参数',
    enableField: ref('voice', 'enableVoice'),
    sections: ['voice'],
    secrets: [2]
  },
  {
    pluginKey: 'preset.feature.stickers',
    title: '表情包',
    summary: '表情收录、候选与冷却',
    enableField: ref('behavior', 'enableStickers'),
    fields: behavior('enableStickers', 'stickerCandidates', 'stickerCooldownSeconds',
      'stickerCurateIntervalSeconds', 'stickerLibraryMax')
  },
  {
    pluginKey: 'preset.feature.poke',
    title: '戳一戳',
    summary: '开关与冷却',
    enableField: ref('behavior', 'enablePoke'),
    fields: behavior('enablePoke', 'pokeCooldownSeconds')
  },
  {
    pluginKey: 'preset.feature.vibes',
    title: '氛围与心情',
    summary: '氛围阻尼与心情保留时长',
    enableField: ref('behavior', 'enableAtmosphereDamping'),
    fields: behavior('enableAtmosphereDamping', 'moodTtlSeconds')
  },
  {
    pluginKey: 'preset.feature.profiles',
    title: '人物画像',
    summary: '画像摘要、巡检与字数预算',
    enableField: ref('behavior', 'enableProfileSummary'),
    fields: behavior('enableProfileSummary', 'profileSummaryThreshold', 'profileSummaryIntervalSeconds',
      'profileSummaryLines', 'profileSummaryMaxChars', 'maxProfileChars', 'profileLookupCount')
  },
  {
    pluginKey: 'preset.feature.websearch',
    title: '联网搜索与链接预览',
    summary: '搜索源、结果条数、超时与链接预览',
    enableField: ref('behavior', 'enableWebSearch'),
    fields: behavior('enableWebSearch', 'webSearchUseModelSearch', 'webSearchSources', 'webSearchMaxResults',
      'webSearchTimeoutSeconds', 'webSearchCooldownSeconds', 'enableLinkPreview', 'linkPreviewMax',
      'linkPreviewTimeoutSeconds')
  }
]

/** 当前登记的插件与系统能力；保持数组接口，登记请走 registerPlugin。 */
const registry = shallowReactive<RegisteredPluginSettingsSpec[]>([])
export const pluginSettings: readonly RegisteredPluginSettingsSpec[] = shallowReadonly(registry)
const registryRevision = vueRef(0)
/** 注册成功或实际撤销后递增；让 computed 消费者能追踪延迟登记。 */
export const pluginRegistryRevision = readonly(registryRevision)

/** 历史数组/集合接口：只描述显式所有权，不包括系统兜底。 */
export const pluginOwnedSections: ZbaPatchSection[] = []
export const pluginOwnedPaths: Set<string> = new Set()

const pathOf = (item: PluginFieldRef): string => item.section + '.' + item.key

function ownedFields(spec: PluginSettingsSpec): PluginFieldRef[] {
  return [...(spec.fields ?? []), ...(spec.enableField ? [spec.enableField] : [])]
}

function owns(spec: PluginSettingsSpec, field: PluginFieldRef): boolean {
  return !!spec.sections?.includes(field.section) || ownedFields(spec).some((item) => pathOf(item) === pathOf(field))
}

function rebuildOwnership(): void {
  pluginOwnedSections.splice(0, pluginOwnedSections.length, ...registry.flatMap((spec) => spec.sections ?? []))
  pluginOwnedPaths.clear()
  for (const spec of registry) for (const field of ownedFields(spec)) pluginOwnedPaths.add(pathOf(field))
  registryRevision.value++
}

function defaultEditor(spec: PluginSettingsSpec): PluginConfigurationEditor {
  const hasFields = spec.enableField || spec.fields?.length || spec.sections?.length
    || spec.fallbackSections?.length || spec.secrets?.length || spec.providers
  return { type: hasFields ? 'fields' : 'declared' }
}

/** 防止登记方后来修改数组、字段引用或编辑器描述，绕过所有权校验。 */
function snapshotSpec(spec: PluginSettingsSpec): RegisteredPluginSettingsSpec {
  const entry: RegisteredPluginSettingsSpec = {
    ...spec, source: spec.source ?? 'extension', kind: spec.kind ?? 'plugin',
    editor: { ...(spec.editor ?? defaultEditor(spec)) },
    enableField: spec.enableField ? { ...spec.enableField } : undefined,
    sections: spec.sections ? [...spec.sections] : undefined,
    fields: spec.fields?.map((field) => ({ ...field })),
    fallbackSections: spec.fallbackSections ? [...spec.fallbackSections] : undefined,
    secrets: [...(spec.secrets ?? [])]
  }
  if (entry.enableField) Object.freeze(entry.enableField)
  for (const field of entry.fields ?? []) Object.freeze(field)
  for (const array of [entry.sections, entry.fields, entry.fallbackSections, entry.secrets]) {
    if (array) Object.freeze(array)
  }
  Object.freeze(entry.editor)
  return Object.freeze(entry)
}

function validateSpec(spec: RegisteredPluginSettingsSpec): void {
  if (!spec.pluginKey.trim()) throw new Error('registerPlugin：pluginKey 不能为空')
  if (registry.some((item) => item.pluginKey === spec.pluginKey)) {
    throw new Error('registerPlugin：插件 ' + spec.pluginKey + ' 已经登记过')
  }
  if (spec.editor.type === 'fields' && defaultEditor(spec).type !== 'fields') {
    throw new Error('registerPlugin：fields 编辑器缺少可派生的字段、密钥或供应商配置')
  }
  if (spec.kind !== 'system' && spec.fallbackSections !== undefined) {
    throw new Error('registerPlugin：fallbackSections 仅限 system 能力')
  }
  for (const section of spec.sections ?? []) {
    // Whole-section claims must reject earlier individual fields as well as whole sections.
    const owner = registry.find((item) => item.sections?.includes(section)
      || ownedFields(item).some((field) => field.section === section))
    if (owner) throw new Error('registerPlugin：分区 ' + section + ' 已归 ' + owner.pluginKey)
  }
  for (const field of ownedFields(spec)) {
    const owner = registry.find((item) => owns(item, field))
    if (owner) throw new Error('registerPlugin：字段 ' + pathOf(field) + ' 已归 ' + owner.pluginKey)
  }
  const seenSecrets = new Set<ZbaSecretSlot>()
  for (const slot of spec.secrets) {
    if (seenSecrets.has(slot)) throw new Error('registerPlugin：密钥 ' + slot + ' 重复声明')
    seenSecrets.add(slot)
    const owner = registry.find((item) => item.secrets.includes(slot))
    if (owner) throw new Error('registerPlugin：密钥 ' + slot + ' 已归 ' + owner.pluginKey)
  }
  const seenFallbacks = new Set<ZbaPatchSection>()
  for (const section of spec.fallbackSections ?? []) {
    if (seenFallbacks.has(section)) throw new Error('registerPlugin：兜底分区 ' + section + ' 重复声明')
    seenFallbacks.add(section)
    const owner = registry.find((item) => item.fallbackSections?.includes(section))
    if (owner) throw new Error('registerPlugin：兜底分区 ' + section + ' 已归 ' + owner.pluginKey)
  }
}

/** 登记插件或系统能力；先完整校验，再原子发布。返回幂等撤销函数。 */
export function registerPlugin(spec: PluginSettingsSpec): () => void {
  const entry = snapshotSpec(spec)
  validateSpec(entry)
  registry.push(entry)
  rebuildOwnership()
  return () => {
    const index = registry.indexOf(entry)
    if (index < 0) return
    registry.splice(index, 1)
    rebuildOwnership()
  }
}

/** 按 key 撤销当前登记（例如扩展卸载）；旧 disposer 不会误删同 key 的新登记。 */
export function unregisterPlugin(pluginKey: string): boolean {
  const index = registry.findIndex((spec) => spec.pluginKey === pluginKey)
  if (index < 0) return false
  registry.splice(index, 1)
  rebuildOwnership()
  return true
}

for (const spec of presets) registerPlugin({ ...spec, source: 'preset' })

export function settingsFor(pluginKey: string): RegisteredPluginSettingsSpec | undefined {
  void pluginRegistryRevision.value
  return registry.find((spec) => spec.pluginKey === pluginKey)
}

/** 不包含显式所有权；字段归属策略在 configuration-ownership 中统一应用。 */
export function fallbackOwnerOf(section: ZbaPatchSection): RegisteredPluginSettingsSpec | undefined {
  void pluginRegistryRevision.value
  return registry.find((spec) => spec.fallbackSections?.includes(section))
}

/** 某个插件在当前快照里实际存在的字段（快照里没有的字段不展示）；启用开关排第一。 */
export function resolvePluginFields(
  spec: PluginSettingsSpec,
  view: ZbaConfigurationView
): Array<PluginFieldRef & { field: ZbaReadField }> {
  const result: Array<PluginFieldRef & { field: ZbaReadField }> = []
  const seen = new Set<string>()
  const push = (item: PluginFieldRef): void => {
    const path = pathOf(item)
    if (seen.has(path)) return
    const field = (view[item.section] as Record<string, ZbaReadField> | undefined)?.[item.key]
    if (!field) return
    seen.add(path)
    result.push({ section: item.section, key: item.key, field })
  }
  if (spec.enableField) push(spec.enableField)
  for (const section of spec.sections ?? []) {
    const fields = view[section] as Record<string, ZbaReadField> | undefined
    for (const key of Object.keys(fields ?? {})) push({ section, key })
  }
  for (const item of spec.fields ?? []) push(item)
  return result
}

/** 字段的显式所有者（系统兜底不在此处参与竞争）。 */
export function ownerOf(section: string, key: string): RegisteredPluginSettingsSpec | undefined {
  void pluginRegistryRevision.value
  return registry.find((spec) => owns(spec, { section: section as ZbaPatchSection, key }))
}
