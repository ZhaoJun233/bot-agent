import { describe, expect, it } from 'vitest'
import {
  ownerOf, pluginOwnedPaths, pluginOwnedSections, registerPlugin, resolvePluginFields, settingsFor
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
