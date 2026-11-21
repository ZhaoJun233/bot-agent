import { readonly, ref as vueRef, shallowReactive, shallowReadonly, type Component } from 'vue'
import { agentPresentation } from '../../../../../packages/agent/frontend/presentation'
import { conversationPresentation } from '../../../../../packages/conversation/frontend/presentation'
import { healthReportingPresentation } from '../../../../../packages/health-reporting/frontend/presentation'
import { modelProvidersPresentation, modelServicesPresentation } from '../../../../../packages/model-services/frontend/presentation'
import { musicPresentation } from '../../../../../packages/music/frontend/presentation'
import { platformFeishuPresentation } from '../../../../../packages/platform-feishu/frontend/presentation'
import { platformLocalPresentation } from '../../../../../packages/platform-local/frontend/presentation'
import { platformQqOfficialPresentation } from '../../../../../packages/platform-qq-official/frontend/presentation'
import { platformQqPrivatePresentation } from '../../../../../packages/platform-qq-private/frontend/presentation'
import { pokePresentation } from '../../../../../packages/poke/frontend/presentation'
import { profilesPresentation } from '../../../../../packages/profiles/frontend/presentation'
import { stickersPresentation } from '../../../../../packages/stickers/frontend/presentation'
import { quotaPresentation } from '../../../../../packages/token-quota/frontend/presentation'
import { vibesPresentation } from '../../../../../packages/vibes/frontend/presentation'
import { voicePresentation } from '../../../../../packages/voice/frontend/presentation'
import { researchPresentation } from '../../../../../packages/websearch/frontend/presentation'
import type { ZbaConfigurationView, ZbaPatchSection, ZbaReadField, ZbaSecretSlot } from '../../api/zba'

/**
 * 插件 → 它所拥有的配置字段。
 *
 * 字段仍然存在原来的配置分区里（契约不变），这里只决定「在哪里编辑」：
 * 插件相关的设置从配置页挪到对应领域页的卡片上，配置页不再重复展示。
 * 插件 ID 与 PluginGate.CapabilityPlugins 一致；字段键来自 cfg-0.3.3 字段表。
 *
 * 普通包的声明式设置从获准清单读取，不需要宿主逐 ID 登记。
 * 此处保留默认发行界面及旧 cfg 字段的过渡展示归属；复杂页面运行时交付由 D2 接入。
 * - 前端展示：调用 {@link registerPlugin} 登记一份 {@link PluginSettingsSpec}。
 *   登记后卡片自动出现「启用」开关（enableField）与「设置」按钮，
 *   配置页会自动把这些字段过滤掉，不需要改任何页面代码。
 */
export interface PluginFieldRef {
  section: ZbaPatchSection
  key: string
}

export interface PlatformPolicyCopy {
  groupListLabel?: string
  privateListLabel?: string
  groupPlaceholder?: string
  privatePlaceholder?: string
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
  secrets?: readonly ZbaSecretSlot[]
  /** 仅系统能力可声明；显式所有者和基础字段优先于分区兜底。 */
  fallbackSections?: readonly ZbaPatchSection[]
  /** 复用现有供应商子编辑器。 */
  providers?: boolean
  /** 平台驱动标识（如 `qq.private`、`qq.official`、`feishu`、`local`）。 */
  platformId?: string
  /** 该配置编辑器对应的 `platformPolicies` 切片目标平台 ID。 */
  policyPlatformId?: string
  /** 包声明的图标标识（供宿主领域卡片与导航解析）。 */
  icon?: string
  /** 凭据获取方式说明（如环境变量只读密钥提示），在配置抽屉顶部展示。 */
  credentialHint?: string
  /** 是否支持平台消息能力与限制检查弹窗。 */
  supportsMessagingInspection?: boolean
  /** 包自有的字段标签、提示、秘密槽位名称与策略输入文案。 */
  fieldLabels?: Readonly<Record<string, string>>
  fieldHints?: Readonly<Record<string, string>>
  secretLabels?: Readonly<Record<number, string>>
  policyCopy?: Readonly<PlatformPolicyCopy>
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
  sections?: readonly ZbaPatchSection[]
  /** 分区里的个别字段。 */
  fields?: readonly PluginFieldRef[]
  /** 来源：内置预设或后续接入的扩展插件（仅用于展示）。 */
  source?: 'preset' | 'extension'
}

/** 归一化后的登记结果，调用方不必再次推断默认编辑器。 */
export interface RegisteredPluginSettingsSpec extends PluginSettingsSpec {
  kind: 'plugin' | 'system'
  editor: PluginConfigurationEditor
  secrets: readonly ZbaSecretSlot[]
}

/** 宿主保留的平台分区兜底声明：仅承载未归属特定驱动的平台字段与策略条目。 */
const platformFallbackPresentation: PluginSettingsSpec = Object.freeze({
  pluginKey: 'platform',
  kind: 'system' as const,
  title: '其它平台接入与策略',
  summary: '未归属特定驱动的平台通用字段与访问策略条目',
  fallbackSections: ['platform' as const],
  secrets: [] as const
})

const presets: PluginSettingsSpec[] = [
  conversationPresentation,
  modelServicesPresentation,
  modelProvidersPresentation,
  agentPresentation,
  platformQqPrivatePresentation,
  platformQqOfficialPresentation,
  platformFeishuPresentation,
  platformLocalPresentation,
  platformFallbackPresentation,
  healthReportingPresentation,
  {
    ...quotaPresentation,
    editor: { type: 'component', load: () => import('./TokenQuotaDialog.vue') }
  },
  musicPresentation,
  voicePresentation,
  stickersPresentation,
  pokePresentation,
  vibesPresentation,
  profilesPresentation,
  researchPresentation
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
    || spec.fallbackSections?.length || spec.secrets?.length || spec.providers || Boolean(spec.policyPlatformId)
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
    secrets: [...(spec.secrets ?? [])],
    fieldLabels: spec.fieldLabels ? Object.freeze({ ...spec.fieldLabels }) : undefined,
    fieldHints: spec.fieldHints ? Object.freeze({ ...spec.fieldHints }) : undefined,
    secretLabels: spec.secretLabels ? Object.freeze({ ...spec.secretLabels }) : undefined,
    policyCopy: spec.policyCopy ? Object.freeze({ ...spec.policyCopy }) : undefined
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
  if (spec.platformId !== undefined) {
    if (!spec.platformId.trim()) throw new Error('registerPlugin：platformId 不能为空')
    const owner = registry.find((item) => item.platformId === spec.platformId)
    if (owner) throw new Error('registerPlugin：平台驱动 ' + spec.platformId + ' 已归 ' + owner.pluginKey)
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

/** 按 platformId 查找已登记的平台驱动声明。 */
export function driverSpecByPlatformId(platformId: string): RegisteredPluginSettingsSpec | undefined {
  void pluginRegistryRevision.value
  return registry.find((spec) => spec.platformId === platformId)
}

/** 当前已登记的所有平台驱动 ID 列表。 */
export function registeredPlatformIds(): string[] {
  void pluginRegistryRevision.value
  return registry.flatMap((spec) => spec.platformId ? [spec.platformId] : [])
}

/** 从已登记包中查询字段中文标签（驱动包自有，不再由宿主硬编码）。 */
export function registeredFieldLabel(key: string): string | undefined {
  void pluginRegistryRevision.value
  for (const spec of registry) {
    const label = spec.fieldLabels?.[key]
    if (label !== undefined) return label
  }
  return undefined
}

/** 从已登记包中查询字段附加说明。 */
export function registeredFieldHint(key: string): string | undefined {
  void pluginRegistryRevision.value
  for (const spec of registry) {
    const hint = spec.fieldHints?.[key]
    if (hint !== undefined) return hint
  }
  return undefined
}

/** 从已登记包中查询秘密槽位名称（例如飞书 3/4/5 槽位归属 system.platform-feishu）。 */
export function registeredSecretLabel(slot: number): string | undefined {
  void pluginRegistryRevision.value
  for (const spec of registry) {
    const label = spec.secretLabels?.[slot]
    if (label !== undefined) return label
  }
  return undefined
}
