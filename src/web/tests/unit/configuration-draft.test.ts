import { describe, expect, it } from 'vitest'
import editableFixture from '../fixtures/configuration-read-editable-result.json'
import type { ZbaConfigurationView } from '../../src/api/zba'
import {
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

