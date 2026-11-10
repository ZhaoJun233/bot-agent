import { afterEach, describe, expect, it, vi } from 'vitest'
import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import CapabilitySettingsHost from '../../src/features/capabilities/CapabilitySettingsHost.vue'
import type { CapabilityConfiguration } from '../../src/features/capabilities/configuration-ownership'
import { ZbaClient } from '../../src/api/zba'

const mounted: Array<{ unmount(): void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount(); document.body.replaceChildren() })
function setup(capability: CapabilityConfiguration) {
  const wrapper = mount(CapabilitySettingsHost, { attachTo: document.body, props: {
    client: new ZbaClient({ baseUrl: '/synthetic/host', token: 'synthetic-token' }),
    mode: 'admin', writesEnabled: true, active: true, capability, features: []
  } })
  mounted.push(wrapper)
  return wrapper
}
function componentSpec(load: () => Promise<{ default: ReturnType<typeof defineComponent> }>): CapabilityConfiguration {
  return { id: 'synthetic.custom', pluginKey: 'synthetic.custom', kind: 'plugin',
    title: '合成专用设置', summary: '合成编辑器', sections: [], secrets: [], editor: { type: 'component', load } }
}

describe('unified settings host', () => {
  it('loads the registered editor lazily and forwards lifecycle, drafts and events', async () => {
    const requestLeave = vi.fn().mockResolvedValue(false)
    const discardSecretsOnLeave = vi.fn()
    const endSession = vi.fn()
    const Component = defineComponent({
      emits: ['saved', 'close', 'auth-expired'],
      setup(_, { expose }) {
        expose({ requestLeave, discardSecretsOnLeave, endSession, draftCount: ref(3) })
        return () => h('div', { 'data-testid': 'synthetic-editor' })
      }
    })
    const load = vi.fn().mockResolvedValue({ default: Component })
    const spec = componentSpec(load)
    expect(load).not.toHaveBeenCalled()
    const wrapper = setup(spec)
    await flushPromises()
    expect(load).toHaveBeenCalledTimes(1)
    expect(wrapper.vm.draftCount).toBe(3)
    expect(await wrapper.vm.requestLeave('navigate')).toBe(false)
    expect(requestLeave).toHaveBeenCalledWith('navigate', undefined)
    wrapper.vm.discardSecretsOnLeave(); wrapper.vm.endSession()
    expect(discardSecretsOnLeave).toHaveBeenCalledTimes(1)
    expect(endSession).toHaveBeenCalledTimes(1)
    wrapper.findComponent(Component).vm.$emit('saved')
    wrapper.findComponent(Component).vm.$emit('close')
    expect(wrapper.emitted('saved')).toHaveLength(1)
    expect(wrapper.emitted('close')).toHaveLength(1)
  })

  it('keeps a dismissible loading dialog while a component chunk is pending', async () => {
    let resolve!: (value: { default: ReturnType<typeof defineComponent> }) => void
    const loading = new Promise<{ default: ReturnType<typeof defineComponent> }>(done => { resolve = done })
    const wrapper = setup(componentSpec(() => loading))
    await flushPromises()
    const close = document.querySelector<HTMLButtonElement>('[data-testid="settings-loading-close"]')
    expect(close).not.toBeNull()
    close!.click(); await flushPromises()
    expect(wrapper.emitted('close')).toHaveLength(1)
    wrapper.unmount()
    resolve({ default: defineComponent({ render: () => null }) })
    await flushPromises()
    expect(document.querySelector('[data-testid="settings-loading"]')).toBeNull()
  })

  it('contains an import failure without rendering an unrelated editor', async () => {
    const wrapper = setup(componentSpec(async () => { throw new Error('synthetic chunk failure') }))
    await flushPromises()
    expect(document.querySelector('[data-testid="settings-load-error"]')).not.toBeNull()
    expect(document.querySelector('[data-testid="configuration"]')).toBeNull()
    expect(await wrapper.vm.requestLeave('lock')).toBe(true)
    document.querySelector<HTMLButtonElement>('[data-testid="settings-load-error-close"]')!.click()
    expect(wrapper.emitted('close')).toHaveLength(1)
  })
})
