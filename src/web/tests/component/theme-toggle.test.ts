import { afterEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'

afterEach(() => { localStorage.clear(); delete document.documentElement.dataset.theme; vi.restoreAllMocks() })

describe('appearance preference keeps authentication private', () => {
  it('defaults to light and persists only the appearance choice', async () => {
    vi.resetModules()
    const { default: ThemeToggle } = await import('../../src/shared/ui/ThemeToggle.vue')
    const wrapper = mount(ThemeToggle)
    expect(document.documentElement.dataset.theme).toBe('light')
    await wrapper.get('button').trigger('click')
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(localStorage.getItem('botagent-appearance')).toBe('dark')
    expect(localStorage.length).toBe(1)
    expect(wrapper.get('button').attributes('aria-label')).toBe('切换到浅色主题')
    wrapper.unmount()
  })
  it('restores a saved theme without persisting session information', async () => {
    vi.resetModules()
    localStorage.setItem('botagent-appearance', 'dark')
    const { default: ThemeToggle } = await import('../../src/shared/ui/ThemeToggle.vue')
    const wrapper = mount(ThemeToggle)
    expect(document.documentElement.dataset.theme).toBe('dark')
    expect(wrapper.get('button').attributes('aria-label')).toBe('切换到浅色主题')
    wrapper.unmount()
  })
  it('stays usable when browser storage throws', async () => {
    vi.resetModules()
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    const { default: ThemeToggle } = await import('../../src/shared/ui/ThemeToggle.vue')
    const wrapper = mount(ThemeToggle)
    expect(document.documentElement.dataset.theme).toBe('light')
    await wrapper.get('button').trigger('click')
    expect(document.documentElement.dataset.theme).toBe('dark')
    wrapper.unmount()
  })
})
