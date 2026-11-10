import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import type { ZbaReadField, ZbaListRuleValue } from '../../src/api/zba'
import PolicyListControl from '../../src/features/configuration/PolicyListControl.vue'
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
})
