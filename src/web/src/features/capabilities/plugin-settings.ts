import type { ZbaConfigurationView, ZbaPatchSection, ZbaReadField } from '../../api/zba'

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

export interface PluginSettingsSpec {
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

const behavior = (...keys: string[]): PluginFieldRef[] => keys.map((key) => ({ section: 'behavior', key }))
const ref = (section: ZbaPatchSection, key: string): PluginFieldRef => ({ section, key })

const presets: PluginSettingsSpec[] = [
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
    sections: ['voice']
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

/** 当前登记的全部插件（预设 + 扩展）。只读视图；登记请走 {@link registerPlugin}。 */
export const pluginSettings: readonly PluginSettingsSpec[] = []
const registry = pluginSettings as PluginSettingsSpec[]

/** 整个分区都交给插件页的分区（配置页导航里不再出现）。随登记原地更新。 */
export const pluginOwnedSections: ZbaPatchSection[] = []

/** 个别归插件所有的字段路径（`section.key`），配置页过滤掉它们。随登记原地更新。 */
export const pluginOwnedPaths: Set<string> = new Set()

const pathOf = (item: PluginFieldRef): string => item.section + '.' + item.key

function ownedPaths(spec: PluginSettingsSpec): string[] {
  const paths = (spec.fields ?? []).map(pathOf)
  if (spec.enableField) paths.push(pathOf(spec.enableField))
  return paths
}

function rebuildOwnership(): void {
  pluginOwnedSections.splice(0, pluginOwnedSections.length, ...registry.flatMap((spec) => spec.sections ?? []))
  pluginOwnedPaths.clear()
  for (const spec of registry) for (const path of ownedPaths(spec)) pluginOwnedPaths.add(path)
}

/**
 * 登记一个插件的设置描述（后续接入插件的入口）。
 *
 * 校验：插件 ID 不能重复；分区与字段不能已经归别的插件所有
 * （同一个字段两张卡片都能改，会互相覆盖）；enableField 不能落在别人的分区里。
 * 返回撤销登记的函数（测试与热插拔用）。
 */
export function registerPlugin(spec: PluginSettingsSpec): () => void {
  if (!spec.pluginKey.trim()) throw new Error('registerPlugin：pluginKey 不能为空')
  if (registry.some((item) => item.pluginKey === spec.pluginKey)) {
    throw new Error('registerPlugin：插件 ' + spec.pluginKey + ' 已经登记过')
  }
  for (const section of spec.sections ?? []) {
    const owner = registry.find((item) => (item.sections ?? []).includes(section))
    if (owner) throw new Error('registerPlugin：分区 ' + section + ' 已归 ' + owner.pluginKey)
  }
  for (const path of ownedPaths(spec)) {
    const [section, key] = [path.slice(0, path.indexOf('.')), path.slice(path.indexOf('.') + 1)]
    const owner = ownerOf(section, key)
    if (owner) throw new Error('registerPlugin：字段 ' + path + ' 已归 ' + owner.pluginKey)
  }
  const entry: PluginSettingsSpec = { source: 'extension', ...spec }
  registry.push(entry)
  rebuildOwnership()
  return () => {
    const index = registry.indexOf(entry)
    if (index >= 0) registry.splice(index, 1)
    rebuildOwnership()
  }
}

for (const spec of presets) registerPlugin({ ...spec, source: 'preset' })

export function settingsFor(pluginKey: string): PluginSettingsSpec | undefined {
  return registry.find((spec) => spec.pluginKey === pluginKey)
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

/** 字段归哪个插件（用于配置页提示「去插件页设置」）。 */
export function ownerOf(section: string, key: string): PluginSettingsSpec | undefined {
  return registry.find((spec) =>
    (spec.sections ?? []).includes(section as ZbaPatchSection)
    || ownedPaths(spec).includes(section + '.' + key))
}
