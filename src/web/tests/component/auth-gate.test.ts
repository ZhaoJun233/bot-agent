import { describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import AuthGate from '../../src/features/auth/AuthGate.vue'

function mountGate(overrides: Partial<{
  configured: boolean | null
  contractVersion: string | null
  error: { code: string; message: string; status?: number; requestId?: string } | null
  busy: boolean
}> = {}) {
  return mount(AuthGate, {
    props: {
      configured: true,
      contractVersion: 'zba-1.0',
      error: null,
      busy: false,
      ...overrides
    }
  })
}

describe('AuthGate', () => {
  it('品牌标记为装饰元素，页面只保留一个可读主标题', () => {
    const wrapper = mountGate()
    expect(wrapper.findAll('h1')).toHaveLength(1)
    expect(wrapper.get('h1').text()).toBe('连接 ZBA')
    expect(wrapper.get('.brand-mark').attributes('aria-hidden')).toBe('true')
    expect(wrapper.get('.auth-wordmark').text()).toContain('BotAgent')
  })

  it('提交时发出 trim 后的令牌，不自行发请求', async () => {
    const wrapper = mountGate()
    await wrapper.get('[data-testid="token-input"]').setValue('  synthetic-token  ')
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('connect')).toEqual([['synthetic-token']])
  })

  it('空令牌不提交', async () => {
    const wrapper = mountGate()
    await wrapper.get('form').trigger('submit')
    expect(wrapper.emitted('connect')).toBeUndefined()
  })

  it('忙碌时显示加载提示并禁用连接', () => {
    const wrapper = mountGate({ busy: true })
    expect(wrapper.get('[data-testid="auth-loading"]').text()).toContain('正在检查')
    expect(wrapper.get('[data-testid="connect-button"]').attributes('disabled')).toBeDefined()
  })

  it('后端未配置令牌时不伪造连接成功', () => {
    const wrapper = mountGate({ configured: false })
    expect(wrapper.get('[data-testid="not-configured"]').text()).toContain('BOTAGENT_ZBA_TOKEN')
  })

  it('显示归一化后的安全错误和请求 id', () => {
    const wrapper = mountGate({
      error: { code: 'unauthorized', message: '令牌无效或已过期，请重新输入。', status: 401, requestId: 'zr-synthetic-1' }
    })
    const notice = wrapper.get('[data-testid="error-notice"]')
    expect(notice.text()).toContain('令牌无效')
    expect(notice.text()).toContain('unauthorized')
    expect(notice.text()).toContain('zr-synthetic-1')
  })
})
