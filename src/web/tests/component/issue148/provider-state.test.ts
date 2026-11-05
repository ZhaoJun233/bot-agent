import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ProviderEditorDialog from '../../../src/features/capabilities/ProviderEditorDialog.vue'
import { ZbaClient } from '../../../src/api/zba'
import { providerSnapshot, syntheticProvider } from '../../fixtures/provider-marketplace'

const wrappers: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount(); document.body.replaceChildren() })

describe('issue148 provider draft and saved state separation', () => {
  it.each([
    [0, '尚未确认'], [1, '设置已应用'], [2, '未全部应用成功'],
    [3, '重启 BotAgent 服务'], [4, '未全部应用成功']
  ] as const)('explains baseline runtime state %s independently of an unsaved toggle', async (runtimeApplyState, text) => {
    const client = new ZbaClient({ baseUrl: '/synthetic/issue148-provider-state', token: 'synthetic-web-admin-token' })
    vi.spyOn(client, 'readConfiguration').mockResolvedValue(providerSnapshot([syntheticProvider({ desiredEnabled: false, runtimeApplyState })]))
    const wrapper = mount(ProviderEditorDialog, { attachTo: document.body,
      props: { client, mode: 'admin', writesEnabled: true, providerKey: 'provider-sample', active: true } })
    wrappers.push(wrapper); await flushPromises()
    const checkbox = document.querySelector<HTMLInputElement>('[data-testid="provider-enabled"]')!
    checkbox.checked = true; checkbox.dispatchEvent(new Event('change', { bubbles: true }))
    await flushPromises()
    const saved = document.querySelector('[data-testid="provider-saved-state"]')!
    expect(saved.textContent).toContain('已保存设置：停用')
    expect(saved.textContent).toContain(text)
    expect(checkbox.checked).toBe(true)
  })

  it('preserves ordinary fields when navigating away while clearing a secret draft', async () => {
    const client = new ZbaClient({ baseUrl: '/synthetic/issue148-provider-navigate', token: 'synthetic-web-admin-token' })
    vi.spyOn(client, 'readConfiguration').mockResolvedValue(providerSnapshot())
    const wrapper = mount(ProviderEditorDialog, { attachTo: document.body,
      props: { client, mode: 'admin', writesEnabled: true, providerKey: 'provider-sample', active: true } })
    wrappers.push(wrapper); await flushPromises()
    const name = document.querySelector<HTMLInputElement>('[data-testid="provider-name"]')!
    name.value = '未保存名称'; name.dispatchEvent(new Event('input', { bubbles: true }))
    const action = document.querySelector<HTMLSelectElement>('select[data-testid$="-mode"]')!
    action.value = 'independent'; action.dispatchEvent(new Event('change', { bubbles: true })); await flushPromises()
    const secret = document.querySelector<HTMLInputElement>('input[data-testid$="-secret"]')!
    secret.value = 'synthetic-secret'; secret.dispatchEvent(new Event('input', { bubbles: true })); await flushPromises()
    expect(await wrapper.vm.requestLeave('navigate')).toBe(true); await flushPromises()
    expect(name.value).toBe('未保存名称')
    expect(document.querySelector<HTMLInputElement>('input[data-testid$="-secret"]')?.value ?? '').toBe('')
    const unload = new Event('beforeunload', { cancelable: true }); window.dispatchEvent(unload)
    expect(unload.defaultPrevented).toBe(true)
  })
})
