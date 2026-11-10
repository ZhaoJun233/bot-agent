import { describe, expect, it } from 'vitest'
import type {
  ZbaConfigurationPatch,
  ZbaConfigurationView,
  ZbaPlatformPolicyEntryView
} from '../../src/api/zba'
import {
  POLICY_ACTIONS,
  applyPolicyEdits,
  policyBaseline,
  policyDirty,
  policyKey,
  policyTitle,
  splitActions,
  type PolicyRowDraft
} from '../../src/features/configuration/policy-draft'

function field<T>(value: T) {
  return {
    visibility: 1,
    display: '',
    rule: { id: 0, effect: 0, canClear: true, editable: true, legacyNeedsReview: false },
    userMode: 2,
    effectiveSource: 2,
    applyState: 1,
    value
  }
}

function entry(
  platformId = 'qq.private',
  group = { kind: 1 as const, value: '10001, 10002' },
  direct = { kind: 0 as const, value: '' }
): ZbaPlatformPolicyEntryView {
  return {
    target: { kind: 'accountDefault', platformId, accountScope: 'legacy' },
    enabled: field(1),
    chatEnabled: field(1),
    groupWhitelist: field(group),
    privateWhitelist: field(direct),
    allowedActions: field({ kind: 1 as const, actions: ['like', 'poke'] }),
    features: field([{ feature: 0, enabled: true }])
  } as ZbaPlatformPolicyEntryView
}

function viewWith(entries: ZbaPlatformPolicyEntryView[]): ZbaConfigurationView {
  return { platform: { platformPolicies: field(entries) } } as unknown as ZbaConfigurationView
}

describe('平台策略草稿', () => {
  it('baseline 从条目投影还原六个字段，且原样比较不脏', () => {
    const row = policyBaseline(entry())
    expect(row).toEqual({
      enabled: 1,
      chatEnabled: 1,
      groupKind: 1,
      groupValue: '10001, 10002',
      privateKind: 0,
      privateValue: '',
      actionKind: 1,
      actions: 'like, poke',
      features: [{ feature: 0, enabled: true }]
    })
    expect(policyDirty(row, policyBaseline(entry()))).toBe(false)
    expect(policyDirty({ ...row, enabled: 2 }, policyBaseline(entry()))).toBe(true)
  })

  it('条目键与标题能区分账户默认目标和实例目标', () => {
    expect(policyKey({ kind: 'accountDefault', platformId: 'feishu', accountScope: 'default' }))
      .toBe('account:feishu:default')
    expect(policyTitle({ kind: 'instance', key: { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' } }))
      .toContain('未命名')
    expect(policyTitle({ kind: 'instance', key: { platformId: 'qq.private', accountScope: 'legacy', instanceId: 'group:10001' } }))
      .toContain('group:10001')
  })

  it('动作名单按空白与中英文逗号切分并去重', () => {
    expect(splitActions('like, poke   like，ban')).toEqual(['like', 'poke', 'ban'])
    expect(splitActions('   ')).toEqual([])
  })

  it('只提交改过的行，未改字段保持完整原始规则', () => {
    const entries = [entry('qq.private'), entry('feishu')]
    const view = viewWith(entries)
    const baseline = policyBaseline(entries[0]!)
    const dirty: PolicyRowDraft = { ...baseline, groupKind: 2, groupValue: '', privateKind: 1, privateValue: '20002' }
    const patch: ZbaConfigurationPatch = {}
    const errors = applyPolicyEdits(patch, { [policyKey(entries[0]!.target)]: dirty }, view)
    expect(errors).toEqual([])
    expect(patch.registered?.policies).toHaveLength(1)
    expect(patch.registered?.policies[0]!.target).toEqual(entries[0]!.target)
    expect(patch.registered?.policies[0]!.configuration).toEqual({
      enabled: 1, chatEnabled: 1,
      groupWhitelist: { kind: 2 },
      privateWhitelist: { kind: 1, value: '20002' },
      allowedActions: { kind: 1, actions: ['like', 'poke'] }, features: [{ feature: 0, enabled: true }]
    })
  })

  it('没改的行一个字节都不发', () => {
    const entries = [entry()]
    const view = viewWith(entries)
    const patch: ZbaConfigurationPatch = {}
    expect(applyPolicyEdits(patch, { [policyKey(entries[0]!.target)]: policyBaseline(entries[0]!) }, view)).toEqual([])
    expect(patch.registered).toBeUndefined()
  })

  it('合并进 registered 时不覆盖供应商与插件', () => {
    const entries = [entry()]
    const view = viewWith(entries)
    const baseline = policyBaseline(entries[0]!)
    const patch = {
      registered: { plugins: [{ key: 'sample' }], providers: [{ key: 'provider-sample', changes: {} }], policies: [] }
    } as unknown as ZbaConfigurationPatch
    applyPolicyEdits(patch, { [policyKey(entries[0]!.target)]: { ...baseline, chatEnabled: 2 } }, view)
    expect(patch.registered?.plugins).toHaveLength(1)
    expect(patch.registered?.providers).toHaveLength(1)
    expect(patch.registered?.policies).toHaveLength(1)
  })

  it('未知动作、跟随基线却带动作名、名单超长都在本地挡掉', () => {
    const entries = [entry()]
    const view = viewWith(entries)
    const key = policyKey(entries[0]!.target)
    const baseline = policyBaseline(entries[0]!)

    const unknown = applyPolicyEdits({}, { [key]: { ...baseline, actions: 'like, selfdestruct' } }, view)
    expect(unknown).toHaveLength(1)
    expect(unknown[0]).toContain('未知动作')
    expect(POLICY_ACTIONS).toContain('like')

    const followWithActions = applyPolicyEdits({}, { [key]: { ...baseline, actionKind: 0 } }, view)
    expect(followWithActions[0]).toContain('跟随基线')

    const oversized = applyPolicyEdits({}, { [key]: { ...baseline, groupKind: 1, groupValue: '1'.repeat(4097) } }, view)
    expect(oversized[0]).toContain('4096')
  })

  it('条目已被别人删掉时拒绝提交而不是重建', () => {
    const view = viewWith([entry()])
    const stale = policyBaseline(entry('qq.official'))
    const errors = applyPolicyEdits({}, { 'account:qq.official:legacy': stale }, view)
    expect(errors).toHaveLength(1)
    expect(errors[0]).toContain('不存在')
  })
})
