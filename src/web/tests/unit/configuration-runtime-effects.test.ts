import { describe, expect, it } from 'vitest'
import type { ZbaPolicyTarget, ZbaReadField } from '../../src/api/zba'
import editable from '../fixtures/configuration-read-editable-result.json'
import { runtimeEffectPresentation } from '../../src/features/configuration/runtime-effects'
import { policySource } from '../../src/features/configuration/policy-presentation'

type Effect = NonNullable<Parameters<typeof runtimeEffectPresentation>[0]>[number]
const field = (id = 100): ZbaReadField => {
  const base = editable.view.behavior.botPersona as ZbaReadField
  return { ...base, rule: { ...base.rule, id } }
}
const scalar = (state: Effect['state'], restartTarget?: Effect['restartTarget']): Effect => ({
  field: { field: 100, object: null }, state, ...(restartTarget ? { restartTarget } : {})
})
const account: ZbaPolicyTarget = { kind: 'accountDefault', platformId: 'sample-platform', accountScope: 'sample-account' }
const instance: ZbaPolicyTarget = { kind: 'instance', key: { platformId: 'sample-platform', accountScope: 'sample-account', instanceId: 'sample-instance' } }

describe('runtime effect presentation', () => {
  it('absent or unrelated metadata never proves current', () => {
    expect(runtimeEffectPresentation(undefined, field()).state).toBe('unknown')
    expect(runtimeEffectPresentation([scalar('current')], field(101)).state).toBe('unknown')
  })
  it('current requires the exact field address', () => {
    expect(runtimeEffectPresentation([scalar('current')], field())).toEqual({ state: 'current', label: '当前已生效', tone: 'ok' })
  })
  it('scalar metadata cannot leak into a policy or the opposite', () => {
    const policy: Effect = { field: { field: 100, object: { kind: 'policy', target: instance } }, state: 'current' }
    expect(runtimeEffectPresentation([scalar('current')], field(), instance).state).toBe('unknown')
    expect(runtimeEffectPresentation([policy], field()).state).toBe('unknown')
  })
  it('policy lookup isolates scope, account and instance, not just numeric field id', () => {
    const effect: Effect = { field: { field: 100, object: { kind: 'policy', target: instance } }, state: 'current' }
    expect(runtimeEffectPresentation([effect], field(), instance).state).toBe('current')
    expect(runtimeEffectPresentation([effect], field(), account).state).toBe('unknown')
    const other: ZbaPolicyTarget = { kind: 'instance', key: { ...instance.key, instanceId: 'other-instance' } }
    expect(runtimeEffectPresentation([effect], field(), other).state).toBe('unknown')
  })
  it('only the server-confirmed restart target can name a service', () => {
    expect(runtimeEffectPresentation([scalar('restartRequired', 'botAgent')], field()).label).toContain('机器人服务')
    expect(runtimeEffectPresentation([scalar('restartRequired')], field()).label).toContain('对象待确认')
  })
  it('failed apply and unknown states cannot display current', () => {
    expect(runtimeEffectPresentation([scalar('applyFailed')], field()).label).toContain('运行应用失败')
    expect(runtimeEffectPresentation([scalar('unknown')], field()).label).toContain('待确认')
  })
  it('configuration value source is not mislabeled as a policy scope', () => {
    expect([0, 1, 2, 3, 4, 99].map(effectiveSource => policySource({ effectiveSource } as Parameters<typeof policySource>[0]))).toEqual(['内置默认', '用户配置', '环境变量', '旧配置兼容', '来源待确认', '来源待确认'])
  })
})
