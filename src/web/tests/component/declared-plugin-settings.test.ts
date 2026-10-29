import { afterEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import DeclaredPluginSettings from '../../src/features/capabilities/DeclaredPluginSettings.vue'
import { createPluginSettingsDraft } from '../../src/features/capabilities/declared-plugin-settings'
import { syntheticSettings } from '../fixtures/provider-marketplace'
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount() })

describe('declarative plugin settings', () => {
  it('renders exactly supported field controls and never echoes stored secrets', async () => {
    const draft = createPluginSettingsDraft(syntheticSettings, { secret: 'synthetic-do-not-echo' })
    const wrapper = mount(DeclaredPluginSettings, { props: { definitions: syntheticSettings, draft, configuredSecrets: { secret: true } } }); mounted.push(wrapper)
    expect(wrapper.find('script').exists()).toBe(false)
    expect(wrapper.text()).toContain('不加载插件提供的 UI 脚本')
    expect(wrapper.text()).not.toContain('synthetic-do-not-echo')
    expect(wrapper.get('[data-testid="plugin-setting-limit"]').attributes('type')).toBe('number')
    expect(wrapper.get('[data-testid="plugin-setting-enabled"]').attributes('type')).toBe('checkbox')
    await wrapper.get('[data-testid="plugin-setting-format"]').setValue('long')
    expect(wrapper.emitted('change')?.[0]).toEqual(['format', { kind: 'set', value: 'long' }])
    await wrapper.get('[data-testid="plugin-setting-secret-mode"]').setValue('set')
    expect(wrapper.emitted('change')?.[1]).toEqual(['secret', { kind: 'set', value: '' }])
    await wrapper.setProps({ draft: { ...draft, secret: { kind: 'set', value: '' } } })
    expect(wrapper.get('[data-testid="plugin-setting-secret"]').attributes('type')).toBe('pass' + 'word')
    expect(wrapper.get('[data-testid="plugin-setting-secret"]').attributes('autocomplete')).toBe('new-password')
  })
  it('prevents edits under a disabled fieldset and does not add undeclared inputs', () => {
    const wrapper = mount(DeclaredPluginSettings, { props: { definitions: syntheticSettings.slice(0, 1), draft: createPluginSettingsDraft(syntheticSettings), disabled: true } }); mounted.push(wrapper)
    expect(wrapper.get('fieldset').attributes('disabled')).toBeDefined()
    expect(wrapper.findAll('input')).toHaveLength(1)
    expect(wrapper.find('[data-testid="plugin-setting-secret"]').exists()).toBe(false)
  })
})
