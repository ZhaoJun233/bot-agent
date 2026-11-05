import { afterEach, describe, expect, it } from 'vitest'
import { computed } from 'vue'
import type { PluginSettingsSpec } from '../../src/features/capabilities/plugin-settings'
import {
  ownerOf, pluginOwnedPaths, pluginOwnedSections, pluginRegistryRevision, pluginSettings,
  registerPlugin, resolvePluginFields, settingsFor, unregisterPlugin
} from '../../src/features/capabilities/plugin-settings'
import { readSwitches, setPluginEnabled } from '../../src/features/capabilities/plugin-toggle'

const field = (value: unknown) => ({
  visibility: 1, display: String(value), value, userMode: 2, effectiveSource: 1, applyState: 1,
  rule: { id: 1, effect: 0, canClear: false, editable: true, legacyNeedsReview: false }
})
const view: any = {
  behavior: { enablePoke: field(false), pokeCooldownSeconds: field(30) }, model: {}, agent: {}, platform: {},
  voice: { enableVoice: field(true) }, music: { enableMusic: field(true), musicBitrate: field(128) }, reporting: {},
  registered: { plugins: [], providers: [] }
}

describe('plugin registry', () => {
  it('预设插件都带启用开关，归属表随登记建立', () => {
    expect(settingsFor('preset.feature.music')?.enableField).toEqual({ section: 'music', key: 'enableMusic' })
    expect(pluginOwnedSections).toContain('music')
    expect(pluginOwnedPaths.has('behavior.enablePoke')).toBe(true)
  })

  it('启用开关排在字段第一位且不重复', () => {
    const fields = resolvePluginFields(settingsFor('preset.feature.poke')!, view)
    expect(fields.map((f) => f.key)).toEqual(['enablePoke', 'pokeCooldownSeconds'])
  })

  it('registerPlugin 登记扩展插件，撤销后归属表恢复', () => {
    const undo = registerPlugin({
      pluginKey: 'ext.demo', title: '演示', summary: 's',
      enableField: { section: 'behavior', key: 'enableDemo' }
    })
    expect(settingsFor('ext.demo')?.source).toBe('extension')
    expect(ownerOf('behavior', 'enableDemo')?.pluginKey).toBe('ext.demo')
    expect(pluginOwnedPaths.has('behavior.enableDemo')).toBe(true)
    undo()
    expect(settingsFor('ext.demo')).toBeUndefined()
    expect(pluginOwnedPaths.has('behavior.enableDemo')).toBe(false)
  })

  it('重复 ID、抢占别人的分区或字段会被拒绝', () => {
    expect(() => registerPlugin({ pluginKey: 'preset.feature.music', title: 'x', summary: 'x' })).toThrow()
    expect(() => registerPlugin({ pluginKey: 'ext.a', title: 'x', summary: 'x', sections: ['music'] })).toThrow()
    expect(() => registerPlugin({
      pluginKey: 'ext.b', title: 'x', summary: 'x', fields: [{ section: 'behavior', key: 'enablePoke' }]
    })).toThrow()
    expect(settingsFor('ext.a')).toBeUndefined()
    expect(settingsFor('ext.b')).toBeUndefined()
  })
})

describe('plugin toggle', () => {
  const snapshot = { code: 0, version: '7', generation: 'e6fe12b715524927a91bb01af7857bdb', contractRevision: 'cfg-0.3.3', view }

  it('readSwitches 读出每个插件的开关状态，缺字段为 null', () => {
    const states = readSwitches(view)
    expect(states['preset.feature.music']).toEqual({ enabled: true, editable: true })
    expect(states['preset.feature.poke']).toEqual({ enabled: false, editable: true })
    expect(states['preset.feature.stickers']?.enabled).toBeNull()
  })

  it('setPluginEnabled 只发启用字段，并带 CAS 版本', async () => {
    let sent: any = null
    const client: any = {
      readConfiguration: async () => snapshot,
      saveConfiguration: async (req: unknown) => { sent = req; return { code: 0, errors: [], receipt: { key: 'k' } } }
    }
    const result = await setPluginEnabled(client, settingsFor('preset.feature.poke')!, true)
    expect(result).toEqual({ kind: 'saved', enabled: true })
    expect(sent.changes).toEqual({ behavior: { enablePoke: { kind: 1, value: true } } })
    expect(sent.expectedVersion).toBe('7')
  })

  it('目标值与当前一致时不写', async () => {
    let calls = 0
    const client: any = { readConfiguration: async () => snapshot, saveConfiguration: async () => { calls++ } }
    expect((await setPluginEnabled(client, settingsFor('preset.feature.music')!, true)).kind).toBe('noop')
    expect(calls).toBe(0)
  })

  it('冲突与无回执分别映射为 conflict / unconfirmed', async () => {
    const mk = (res: unknown): any => ({ readConfiguration: async () => snapshot, saveConfiguration: async () => res })
    const spec = settingsFor('preset.feature.poke')!
    expect((await setPluginEnabled(mk({ code: 3, errors: [] }), spec, true)).kind).toBe('conflict')
    expect((await setPluginEnabled(mk({ code: 0, errors: [] }), spec, true)).kind).toBe('unconfirmed')
  })
})

describe('统一配置登记契约', () => {
  const cleanup: (() => void)[] = []
  afterEach(() => { while (cleanup.length) cleanup.pop()!() })
  const register = (spec: PluginSettingsSpec) => {
    const dispose = registerPlugin(spec)
    cleanup.push(dispose)
    return dispose
  }
  const withoutPreset = (key: string) => {
    const spec = settingsFor(key)!
    expect(unregisterPlugin(key)).toBe(true)
    cleanup.push(() => { registerPlugin(spec) })
    return spec
  }

  it('保持数组接口，插件默认 kind，按元数据派生编辑器', () => {
    expect(Array.isArray(pluginSettings)).toBe(true)
    register({ pluginKey: 'ext.declared', title: 'Declared', summary: '' })
    register({ pluginKey: 'ext.fields', title: 'Fields', summary: '', enableField: { section: 'agent', key: 'syntheticEnabled' } })
    register({ pluginKey: 'ext.explicit', title: 'Explicit', summary: '', editor: { type: 'declared' }, fields: [{ section: 'agent', key: 'syntheticHidden' }] })
    expect(settingsFor('ext.declared')).toMatchObject({ kind: 'plugin', editor: { type: 'declared' }, secrets: [] })
    expect(settingsFor('ext.fields')?.editor).toEqual({ type: 'fields' })
    expect(settingsFor('ext.explicit')?.editor).toEqual({ type: 'declared' })
    expect(settingsFor('models')).toMatchObject({ kind: 'system', editor: { type: 'fields' }, secrets: [0], providers: true })
  })

  it('显式字段编辑器必须有可派生的编辑内容', () => {
    expect(() => registerPlugin({ pluginKey: 'ext.empty-fields', title: 'Empty', summary: '', editor: { type: 'fields' } })).toThrow(/缺少可派生/)
    register({ pluginKey: 'ext.provider-fields', title: 'Providers', summary: '', providers: true })
    expect(settingsFor('ext.provider-fields')?.editor).toEqual({ type: 'fields' })
  })

  it('component 编辑器保持惰性，Token 配额也在登记表声明', async () => {
    let loads = 0
    const load = async () => { loads++; return { default: {} } }
    register({ pluginKey: 'ext.component', title: 'Component', summary: '', editor: { type: 'component', load } })
    expect(settingsFor('ext.component')?.editor).toEqual({ type: 'component', load })
    expect(loads).toBe(0)
    const quota = settingsFor('preset.feature.token-quota')!
    expect(quota.editor.type).toBe('component')
    if (quota.editor.type === 'component') {
      expect(typeof quota.editor.load).toBe('function')
      expect((await quota.editor.load()).default).toBeDefined()
    }
  })

  it.each(['fields', 'enableField'] as const)('先登记 %s 再登记整分区也会拒绝冲突', (kind) => {
    const field = { section: 'agent' as const, key: 'syntheticCollision' }
    register({ pluginKey: 'ext.specific', title: 'Specific', summary: '', ...(kind === 'fields' ? { fields: [field] } : { enableField: field }) })
    expect(() => registerPlugin({ pluginKey: 'ext.whole', title: 'Whole', summary: '', sections: ['agent'] })).toThrow(/已归/)
    expect(settingsFor('ext.whole')).toBeUndefined()
  })

  it.each(['fields', 'enableField'] as const)('先登记整分区再登记 %s 也会拒绝冲突', (kind) => {
    register({ pluginKey: 'ext.whole', title: 'Whole', summary: '', sections: ['reporting'] })
    const field = { section: 'reporting' as const, key: 'syntheticCollision' }
    expect(() => registerPlugin({ pluginKey: 'ext.specific', title: 'Specific', summary: '', ...(kind === 'fields' ? { fields: [field] } : { enableField: field }) })).toThrow(/已归/)
  })

  it('显式声明可以覆盖系统兜底，但不能覆盖其他显式所有者', () => {
    register({ pluginKey: 'ext.model', title: 'Model', summary: '', kind: 'system', fields: [{ section: 'model', key: 'syntheticModel' }] })
    expect(ownerOf('model', 'syntheticModel')?.pluginKey).toBe('ext.model')
    expect(() => registerPlugin({ pluginKey: 'ext.collision', title: 'Collision', summary: '', fields: [{ section: 'model', key: 'syntheticModel' }] })).toThrow(/已归/)
  })

  it('系统兜底不允许插件声明（包括空数组），每个分区只能登记一次', () => {
    expect(() => registerPlugin({ pluginKey: 'ext.fallback', title: 'Fallback', summary: '', fallbackSections: ['behavior'] })).toThrow(/仅限 system/)
    expect(() => registerPlugin({ pluginKey: 'ext.empty-fallback', title: 'Fallback', summary: '', fallbackSections: [] })).toThrow(/仅限 system/)
    expect(() => registerPlugin({ pluginKey: 'ext.duplicate-fallback', title: 'Fallback', summary: '', kind: 'system', fallbackSections: ['behavior'] })).toThrow(/已归/)
    withoutPreset('reporting')
    expect(() => registerPlugin({ pluginKey: 'ext.repeated-fallback', title: 'Fallback', summary: '', kind: 'system', fallbackSections: ['reporting', 'reporting'] })).toThrow(/重复/)
    register({ pluginKey: 'ext.fallback-owner', title: 'Fallback', summary: '', kind: 'system', fallbackSections: ['reporting'] })
    expect(settingsFor('ext.fallback-owner')?.editor).toEqual({ type: 'fields' })
  })

  it('密钥所有权不允许跨登记或单登记重复，密钥也可派生字段编辑器', () => {
    expect(() => registerPlugin({ pluginKey: 'ext.secret-conflict', title: 'Secret', summary: '', secrets: [0] })).toThrow(/已归/)
    withoutPreset('models')
    expect(() => registerPlugin({ pluginKey: 'ext.secret-repeated', title: 'Secret', summary: '', secrets: [0, 0] })).toThrow(/重复/)
    register({ pluginKey: 'ext.secret', title: 'Secret', summary: '', secrets: [0] })
    expect(settingsFor('ext.secret')?.editor).toEqual({ type: 'fields' })
  })

  it('复制所有可变描述，外部修改不能改变所有权或编辑器', () => {
    withoutPreset('reporting')
    withoutPreset('platform')
    const load = async () => ({ default: {} })
    const spec: PluginSettingsSpec = {
      pluginKey: 'ext.snapshot', title: 'Snapshot', summary: '', kind: 'system',
      sections: ['agent'], fields: [{ section: 'reporting', key: 'syntheticField' }],
      enableField: { section: 'reporting', key: 'syntheticEnabled' }, fallbackSections: ['reporting'],
      secrets: [4], editor: { type: 'component', load }
    }
    register(spec)
    spec.sections![0] = 'model'
    spec.fields![0]!.key = 'changedField'
    spec.enableField!.key = 'changedEnabled'
    spec.fallbackSections![0] = 'platform'
    spec.secrets![0] = 0
    if (spec.editor?.type === 'component') spec.editor.load = async () => ({ default: {} })
    const saved = settingsFor('ext.snapshot')!
    expect(saved).toMatchObject({ sections: ['agent'], fields: [{ key: 'syntheticField' }], enableField: { key: 'syntheticEnabled' }, fallbackSections: ['reporting'], secrets: [4], editor: { type: 'component', load } })
    expect(ownerOf('reporting', 'syntheticField')?.pluginKey).toBe('ext.snapshot')
    expect(ownerOf('reporting', 'changedField')).toBeUndefined()
    expect(Object.isFrozen(saved.fields![0])).toBe(true)
    expect(Object.isFrozen(saved.editor)).toBe(true)
    expect(Object.isFrozen(saved.secrets)).toBe(true)
  })

  it('登记与撤销刷新 computed，失败和重复撤销不发布 revision', () => {
    const item = computed(() => settingsFor('ext.reactive'))
    const all = computed(() => pluginSettings.map((spec) => spec.pluginKey))
    const owner = computed(() => ownerOf('agent', 'syntheticReactive')?.pluginKey)
    expect(item.value).toBeUndefined()
    expect(all.value).not.toContain('ext.reactive')
    expect(owner.value).toBeUndefined()
    const before = pluginRegistryRevision.value
    const dispose = register({ pluginKey: 'ext.reactive', title: 'Reactive', summary: '', fields: [{ section: 'agent', key: 'syntheticReactive' }] })
    expect(pluginRegistryRevision.value).toBe(before + 1)
    expect(item.value?.pluginKey).toBe('ext.reactive')
    expect(all.value).toContain('ext.reactive')
    expect(owner.value).toBe('ext.reactive')
    expect(() => registerPlugin({ pluginKey: 'ext.reactive', title: 'Duplicate', summary: '' })).toThrow()
    expect(pluginRegistryRevision.value).toBe(before + 1)
    dispose()
    expect(item.value).toBeUndefined()
    expect(owner.value).toBeUndefined()
    expect(all.value).not.toContain('ext.reactive')
    expect(pluginRegistryRevision.value).toBe(before + 2)
    dispose()
    expect(unregisterPlugin('ext.missing')).toBe(false)
    expect(pluginRegistryRevision.value).toBe(before + 2)
  })

  it('旧撤销函数不会误删同 key 的新登记', () => {
    const dispose = register({ pluginKey: 'ext.reused', title: 'Old', summary: '' })
    expect(unregisterPlugin('ext.reused')).toBe(true)
    register({ pluginKey: 'ext.reused', title: 'New', summary: '' })
    dispose()
    expect(settingsFor('ext.reused')?.title).toBe('New')
  })
})
