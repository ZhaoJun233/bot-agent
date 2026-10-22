import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import OverviewPage from '../../src/features/overview/OverviewPage.vue'
import type { ZbaHealth, ZbaMeta, ZbaStatus, ZbaComponent, ZbaErrorSummary } from '../../src/api/zba'
import type { Compatibility } from '../../src/api/compat'
import healthFixture from '../fixtures/health.json'
import metaFixture from '../fixtures/meta.json'
import statusFixture from '../fixtures/status.json'

const health = healthFixture as ZbaHealth
const meta = metaFixture as ZbaMeta
const status = statusFixture as ZbaStatus
const compatible: Compatibility = {
  checked: true,
  compatible: true,
  writesEnabled: true,
  reasons: [],
  warnings: []
}

function mountPage(overrides: Partial<{
  status: ZbaStatus
  compatibility: Compatibility
  stale: boolean
  mode: 'read' | 'admin'
}> = {}) {
  return mount(OverviewPage, {
    props: {
      health,
      meta,
      status,
      compatibility: compatible,
      stale: false,
      error: null,
      lastSuccessAt: '2026-10-20T12:00:00+00:00',
      mode: 'read',
      adminError: null,
      busy: false,
      ...overrides
    }
  })
}

describe('OverviewPage', () => {
  it('显示总体状态、组件计数与结构化组件行', () => {
    const wrapper = mountPage()
    expect(wrapper.get('[data-testid="overview-state"]').text()).toBe('healthy')
    expect(wrapper.get('[data-testid="components"]').text()).toContain('合成驱动')
    expect(wrapper.get('[data-testid="components"]').text()).toContain('合成插件')
    expect(wrapper.get('[data-testid="components"]').text()).toContain('disabled_by_operator')
    expect(wrapper.text()).toContain('1 / 2')
  })

  it('无错误时显示空态而不是伪造错误', () => {
    const wrapper = mountPage({ status: { ...status, recentErrors: [] } })
    expect(wrapper.get('[data-testid="errors-empty"]').text()).toContain('没有待处理错误')
  })

  it('旧数据时显示 stale 提示但保留原数据', () => {
    const wrapper = mountPage({ stale: true })
    expect(wrapper.get('[data-testid="stale-banner"]').text()).toContain('上次成功读取')
    expect(wrapper.get('[data-testid="components"]').text()).toContain('合成驱动')
  })

  it('契约不兼容时禁用管理员入口并展示原因', () => {
    const wrapper = mountPage({
      compatibility: {
        checked: true,
        compatible: false,
        writesEnabled: false,
        reasons: ['契约版本 zba-9.9 不一致。'],
        warnings: []
      }
    })
    expect(wrapper.get('[data-testid="compatibility-warning"]').text()).toContain('zba-9.9')
    expect(wrapper.get('[data-testid="enable-admin"]').attributes('disabled')).toBeDefined()
  })

  it('组件为空时显示明确空态', () => {
    const wrapper = mountPage({ status: { ...status, components: [] } })
    expect(wrapper.get('[data-testid="components-empty"]').text()).toContain('尚未登记')
    expect(wrapper.text()).toContain('0 / 0')
  })

  it('旧后端缺组件/错误数组时显示空态而不是崩溃', () => {
    const wrapper = mountPage({
      status: {
        ...status,
        components: undefined as unknown as ZbaComponent[],
        recentErrors: undefined as unknown as ZbaErrorSummary[]
      }
    })
    expect(wrapper.get('[data-testid="components-empty"]').text()).toContain('尚未登记')
    expect(wrapper.get('[data-testid="errors-empty"]').text()).toContain('没有待处理错误')
  })

  it('刷新按钮只发出 refresh 事件，不自行写业务状态', async () => {
    const wrapper = mountPage()
    await wrapper.get('[data-testid="refresh"]').trigger('click')
    expect(wrapper.emitted('refresh')).toHaveLength(1)
  })

  it('管理员模式按钮只发出 enable-admin 事件', async () => {
    const wrapper = mountPage()
    await wrapper.get('[data-testid="enable-admin"]').trigger('click')
    expect(wrapper.emitted('enable-admin')).toHaveLength(1)
  })
})
