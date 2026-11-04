import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import StatusBadge from '../../src/shared/ui/StatusBadge.vue'

describe('StatusBadge', () => {
  it('失败状态优先于 available=true', () => {
    const wrapper = mount(StatusBadge, { props: { state: 'Failed', available: true } })
    expect(wrapper.attributes('data-tone')).toBe('bad')
    expect(wrapper.text()).toContain('Failed')
  })

  it('available=false 显示 bad，即使 state=Ready', () => {
    const wrapper = mount(StatusBadge, { props: { state: 'Ready', available: false } })
    expect(wrapper.attributes('data-tone')).toBe('bad')
    expect(wrapper.text()).toContain('不可用')
  })

  it('Ready + available=true 显示 ok', () => {
    const wrapper = mount(StatusBadge, { props: { state: 'Ready', available: true } })
    expect(wrapper.attributes('data-tone')).toBe('ok')
  })

  it('Unknown 显示 warn 而不是成功色，且不会被 available=true 标成“可用”', () => {
    const wrapper = mount(StatusBadge, { props: { state: 'Unknown', available: true } })
    expect(wrapper.attributes('data-tone')).toBe('warn')
    expect(wrapper.text()).toBe('Unknown')
  })

  it('主动停用用 neutral，故障仍用 bad', () => {
    const disabled = mount(StatusBadge, { props: { state: 'Disabled', available: false } })
    expect(disabled.attributes('data-tone')).toBe('neutral')
    expect(disabled.text()).toContain('不可用')

    const failed = mount(StatusBadge, { props: { state: 'Failed', available: false } })
    expect(failed.attributes('data-tone')).toBe('bad')
  })

  it('空 state 回退为 unknown，不崩溃', () => {
    const wrapper = mount(StatusBadge, { props: { state: '' } })
    expect(wrapper.text()).toContain('unknown')
  })
})
