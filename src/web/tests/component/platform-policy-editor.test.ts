import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import type { ZbaConfigurationView, ZbaPlatformPolicyEntryView, ZbaReadField, ZbaListRuleValue } from '../../src/api/zba'
import PolicyListControl from '../../src/features/configuration/PolicyListControl.vue'
import PlatformPolicyEditor from '../../src/features/configuration/PlatformPolicyEditor.vue'
import { applyPolicyEdits, policyKey, type PolicyDrafts } from '../../src/features/configuration/policy-draft'
import type { RuntimeEffectPresentation } from '../../src/features/configuration/runtime-effects'
import fixture from '../fixtures/configuration-read-editable-result.json'

const mounted: ReturnType<typeof mount>[] = []
afterEach(() => mounted.splice(0).forEach(wrapper => wrapper.unmount()))
function control(state?: RuntimeEffectPresentation['state']) {
  const stored = structuredClone(fixture.view.platform.platformPolicies.value[0]!.groupWhitelist) as unknown as ZbaReadField<ZbaListRuleValue>
  const effective: ZbaReadField<ZbaListRuleValue> = { ...stored, effectiveSource: 2, display: '合成群 100***01', value: { kind: 1, value: '20002' } }
  const runtime = state ? { state, label: state === 'current' ? '当前生效' : '生效待确认', tone: 'warn' } : undefined
  const wrapper = mount(PolicyListControl, { props: { label: '群聊接收范围', kind: 0, value: '', stored, effective, runtime, testid: 'synthetic-list' } })
  mounted.push(wrapper)
  return wrapper
}

describe('platform policy runtime preview', () => {
  it('only confirmed current state shows server-masked display and source', () => {
    const wrapper = control('current')
    const preview = wrapper.get('[data-testid="synthetic-list-runtime"]')
    expect(preview.text()).toContain('合成群 100***01')
    expect(preview.text()).toContain('环境变量')
    expect(preview.text()).not.toContain('20002')
    expect(wrapper.emitted()).toEqual({})
  })
  it.each([undefined, 'unknown', 'restartRequired', 'applyFailed'] as const)('state %s never treats requested display as actual runtime', state => {
    const wrapper = control(state)
    const preview = wrapper.get('[data-testid="synthetic-list-runtime"]')
    expect(preview.text()).not.toContain('合成群')
    expect(preview.text()).not.toContain('20002')
    expect(preview.text()).not.toContain('来源：')
  })
  it('inheritance selection never materializes the effective list', async () => {
    const wrapper = control('current')
    await wrapper.get('select').setValue('0')
    expect(wrapper.emitted('mode')).toEqual([[0]])
    expect(wrapper.emitted('text')).toBeUndefined()
    expect(wrapper.find('textarea').exists()).toBe(false)
  })
  it('explicit empty warns denial and allow-all does not promise command permissions', async () => {
    const wrapper = control('current')
    await wrapper.setProps({ kind: 3 })
    expect(wrapper.get('[role="status"]').text()).toContain('明确拒绝所有人')
    await wrapper.setProps({ kind: 2 })
    expect(wrapper.text()).toContain('不会授予管理命令权限')
  })

  it('switching actionKind from custom (1) back to inherit (0) clears selected actions and avoids validation deadlock', async () => {
    const entry = structuredClone(fixture.view.platform.platformPolicies.value[0]!) as unknown as ZbaPlatformPolicyEntryView
    const key = policyKey(entry.target)
    const drafts: PolicyDrafts = {
      [key]: {
        enabled: 0,
        chatEnabled: 0,
        groupKind: 0,
        groupValue: '',
        privateKind: 0,
        privateValue: '',
        actionKind: 1,
        actions: 'like, poke',
        features: []
      }
    }
    const wrapper = mount(PlatformPolicyEditor, {
      props: { entries: [entry], modelValue: drafts }
    })
    mounted.push(wrapper)
    const actionSelect = wrapper.findAll('select').find(select =>
      select.findAll('option').some(option => option.text() === '单独选择动作')
    )
    expect(actionSelect).toBeDefined()
    await actionSelect!.setValue('0')
    const emitted = wrapper.emitted('update:modelValue')
    expect(emitted).toBeDefined()
    const nextDrafts = emitted![0]![0] as PolicyDrafts
    // Either cleaned back to baseline (deleted) or explicitly cleared actions: both pass validatePolicyDrafts without error
    if (nextDrafts[key]) {
      expect(nextDrafts[key]!.actionKind).toBe(0)
      expect(nextDrafts[key]!.actions).toBe('')
    }
    expect(applyPolicyEdits({}, nextDrafts, fixture.view as unknown as ZbaConfigurationView)).toEqual([])
  })
})
