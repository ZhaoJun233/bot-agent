import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ManagementDialog from '../../../src/shared/ui/ManagementDialog.vue'
import ProviderEditorDialog from '../../../src/features/capabilities/ProviderEditorDialog.vue'
import { ZbaClient } from '../../../src/api/zba'
import { providerSnapshot } from '../../fixtures/provider-marketplace'

const wrappers: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.replaceChildren() })
function key(element: Element, value: string, shiftKey = false): void {
  element.dispatchEvent(new KeyboardEvent('keydown', { key: value, shiftKey, bubbles: true, cancelable: true }))
}

describe('issue148 shared modal ergonomics', () => {
  it('isolates background, skips CSS-hidden controls and restores opener focus', async () => {
    const opener = document.createElement('button'); document.body.append(opener); opener.focus()
    const wrapper = mount(ManagementDialog, { attachTo: document.body,
      props: { title: '示例', titleId: 'example-title', testId: 'example-dialog', trigger: opener },
      slots: { default: '<button style="display:none">隐藏</button><button id="example-last">最后</button>' } })
    wrappers.push(wrapper); await flushPromises()
    expect(opener.inert).toBe(true)
    expect(document.activeElement?.id).toBe('example-title')
    key(document.activeElement!, 'Tab', true)
    expect(document.activeElement?.id).toBe('example-last')
    key(document.activeElement!, 'Tab')
    expect(document.activeElement?.getAttribute('aria-label')).toContain('关闭')
    wrapper.unmount(); wrappers.pop(); await flushPromises()
    expect(opener.inert).toBe(false)
    expect(document.activeElement).toBe(opener)
  })

  it('releases isolation for inactive v-show dialogs and preserves originally inert nodes', async () => {
    const original = document.createElement('div'); original.inert = true; document.body.append(original)
    const shell = document.createElement('button'); document.body.append(shell)
    const wrapper = mount(ManagementDialog, { attachTo: document.body,
      props: { title: '示例', titleId: 'example-title', testId: 'example-dialog' } })
    wrappers.push(wrapper); await flushPromises()
    expect(shell.inert).toBe(true)
    await wrapper.setProps({ active: false }); await flushPromises()
    expect(shell.inert).toBe(false)
    expect(original.inert).toBe(true)
    expect(document.body.style.overflow).not.toBe('hidden')
  })
})

describe('issue148 provider draft protection', () => {
  async function editor() {
    const client = new ZbaClient({ baseUrl: '/synthetic/issue148', token: 'synthetic-web-read-token' })
    vi.spyOn(client, 'readConfiguration').mockResolvedValue(providerSnapshot())
    const wrapper = mount(ProviderEditorDialog, { attachTo: document.body,
      props: { client, mode: 'admin', writesEnabled: true, active: true, providerKey: 'provider-sample' } })
    wrappers.push(wrapper); await flushPromises()
    const name = document.querySelector<HTMLInputElement>('[data-testid="provider-name"]')!
    name.value = '草稿'; name.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    return wrapper
  }
  it('requires the explicit discard button after repeated Escape or close', async () => {
    const wrapper = await editor()
    const dialog = document.querySelector('[role="dialog"]')!
    key(dialog, 'Escape'); await flushPromises()
    key(dialog, 'Escape'); await flushPromises()
    expect(wrapper.emitted('close')).toBeUndefined()
    document.querySelector<HTMLButtonElement>('[data-testid="provider-discard"] button')!.click(); await flushPromises()
    expect(wrapper.emitted('close')).toHaveLength(1)
  })
  it('warns on reload, but removes the browser guard when session teardown unmounts the editor', async () => {
    const wrapper = await editor()
    const event = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(event)
    expect(event.defaultPrevented).toBe(true)
    wrapper.unmount(); wrappers.pop()
    const after = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(after)
    expect(after.defaultPrevented).toBe(false)
  })
})
