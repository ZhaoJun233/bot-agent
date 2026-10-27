import { describe, expect, it } from 'vitest'
import editableFixture from '../fixtures/configuration-read-editable-result.json'
import type { ZbaConfigurationPatch, ZbaConfigurationView } from '../../src/api/zba'
import {
  applyProviderNames,
  baselineRaw,
  buildPatch,
  buildSecrets,
  conflictingPaths,
  createOperationKey,
  fieldPath,
  isEditable,
  valueKind,
  type DraftMap
} from '../../src/features/configuration/draft'

const view = editableFixture.view as unknown as ZbaConfigurationView

function field(section: 'behavior' | 'model' | 'agent' | 'platform', key: string) {
  return view[section][key]!
}

describe('配置草稿', () => {
  it('供应商改名只提交改过的项，且拒绝把脱敏展示名写回（PNL-11）', () => {
    // 真实宿主返回的是 visibility=2 + nameRaw 的可改名投影；fixture 是 visibility=1 的只读形态，
    // 所以这里在测试里显式换成宿主真实形态，避免拿假形状测出假结论。
    const renamableView = structuredClone(view) as ZbaConfigurationView
    const provider = renamableView.registered.providers[0]!
    ;(provider.name as { visibility: number }).visibility = 2
    ;(provider.name as { nameRaw?: string }).nameRaw = 'sample-provider'
    ;(provider.name as { display: string }).display = 'sam***er'
    delete (provider.name as { value?: string }).value

    const patch: ZbaConfigurationPatch = {}
    expect(applyProviderNames(patch, { 'provider-sample': '真名样例' }, renamableView)).toEqual([])
    // ProviderMetadataPatch 是闭合记录：六个字段全在，没改的都是 Keep。
    expect(patch.registered?.providers).toEqual([{
      key: 'provider-sample',
      changes: {
        nameRaw: { kind: 1, value: '真名样例' },
        priority: { kind: 0 },
        baseUrl: { kind: 0 },
        modelName: { kind: 0 },
        enabled: { kind: 0 },
        credential: { kind: 0 }
      }
    }])

    // 没改动 -> 一个字节都不发（否则每次保存都会覆盖别人的改名）。
    const untouched: ZbaConfigurationPatch = {}
    expect(applyProviderNames(untouched, { 'provider-sample': 'sample-provider' }, renamableView)).toEqual([])
    expect(untouched.registered).toBeUndefined()

    // 把脱敏串当名字写回 = 明确拒绝。
    const placeholder: ZbaConfigurationPatch = {}
    const placeholderErrors = applyProviderNames(placeholder, { 'provider-sample': 'sam***er' }, renamableView)
    expect(placeholderErrors).toHaveLength(1)
    expect(placeholderErrors[0]).toContain('脱敏')
    expect(placeholder.registered).toBeUndefined()

    // 空名、不存在的供应商、以及只读形态（visibility=1）都拦在客户端。
    expect(applyProviderNames({}, { 'provider-sample': '   ' }, renamableView)).toHaveLength(1)
    expect(applyProviderNames({}, { 'no-such-provider': 'x' }, renamableView)).toHaveLength(1)
    expect(applyProviderNames({}, { 'provider-sample': '真名' }, view)[0]).toContain('不可改名')
  })

  it('只把 Set/Clear 转成 patch，并保留字段类型', () => {
    const draft: DraftMap = {
      [fieldPath('behavior', 'botPersona')]: { kind: 'set', raw: '新人格' },
      [fieldPath('model', 'model')]: { kind: 'set', raw: 'sample-model-2' },
      [fieldPath('behavior', 'splitReplies')]: { kind: 'set', raw: 'false' },
      [fieldPath('agent', 'agentPrompt')]: { kind: 'clear' }
    }

    const { patch, errors } = buildPatch(draft, view)
    expect(errors).toEqual([])
    expect(patch.behavior?.botPersona).toEqual({ kind: 1, value: '新人格' })
    expect(patch.behavior?.splitReplies).toEqual({ kind: 1, value: false })
    expect(patch.model?.model).toEqual({ kind: 1, value: 'sample-model-2' })
    expect(patch.agent?.agentPrompt).toEqual({ kind: 2 })
  })

  it('数字输入的非法值不进入 patch', () => {
    const draft: DraftMap = {
      [fieldPath('behavior', 'botPersona')]: { kind: 'set', raw: 'ok' },
      [fieldPath('behavior', 'moodTtlSeconds')]: { kind: 'set', raw: 'abc' }
    }
    const { patch, errors } = buildPatch(draft, view)
    expect(errors).toContain('behavior.moodTtlSeconds：需要数字')
    expect(patch.behavior?.moodTtlSeconds).toBeUndefined()
    expect(patch.behavior?.botPersona).toEqual({ kind: 1, value: 'ok' })
  })

  it('数组/对象字段不提供编辑', () => {
    const policies = field('platform', 'platformPolicies')
    expect(valueKind(policies)).toBeNull()
    expect(isEditable(policies)).toBe(false)
    expect(valueKind(field('behavior', 'splitReplies'))).toBe('boolean')
    expect(valueKind(field('behavior', 'moodTtlSeconds'))).toBe('number')
  })

  it('秘密编辑：Keep 省略，Replace/Delete 转成对应 kind', () => {
    const { secrets, errors } = buildSecrets({
      0: { kind: 'keep' },
      2: { kind: 'replace', value: 'tts-secret' },
      3: { kind: 'delete' }
    })
    expect(errors).toEqual([])
    expect(secrets).toEqual([
      { slot: 2, kind: 1, value: 'tts-secret' },
      { slot: 3, kind: 2 }
    ])
  })

  it('空替换值被拒绝', () => {
    const { errors } = buildSecrets({ 1: { kind: 'replace', value: '' } })
    expect(errors).toContain('槽位 1：替换值不能为空')
  })

  it('冲突比较能识别与新基线不同的字段', () => {
    const draft: DraftMap = {
      [fieldPath('behavior', 'botPersona')]: { kind: 'set', raw: '对方也改了' },
      [fieldPath('behavior', 'splitReplies')]: { kind: 'set', raw: baselineRaw(field('behavior', 'splitReplies')) }
    }
    expect(conflictingPaths(draft, view)).toEqual(['behavior.botPersona'])
  })

  it('操作键符合 cfg1:<generation>:<issued>:<nonce>', () => {
    const key = createOperationKey('0123456789abcdef0123456789abcdef', 1760000000, 'fedcba9876543210fedcba9876543210')
    expect(key).toBe('cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210')
  })
})

