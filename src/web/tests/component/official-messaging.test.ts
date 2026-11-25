import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import OfficialMessagingDialog from '../../src/features/capabilities/OfficialMessagingDialog.vue'
import { ZbaClient, ZbaRequestError, type ZbaManagedInspectResult } from '../../src/api/zba'
import { messagingResult, officialDescriptor } from '../fixtures/messaging'

const wrappers: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of wrappers.splice(0)) wrapper.unmount() })
function client(inspectCapability: () => Promise<ZbaManagedInspectResult>): ZbaClient {
  const api = new ZbaClient({ baseUrl: '/zba/v1', token: 'synthetic' })
  api.inspectCapability = inspectCapability
  return api
}
function open(api = client(async () => messagingResult())) {
  const wrapper = mount(OfficialMessagingDialog, { attachTo: document.body, props: { client: api, target: officialDescriptor.key, active: true } })
  wrappers.push(wrapper)
  return wrapper
}
const click = (id: string): void => document.querySelector<HTMLButtonElement>('[data-testid="' + id + '"]')!.click()
const text = (): string => document.querySelector('[data-testid="official-messaging-dialog"]')?.textContent ?? ''
describe('official messaging observations', () => {
  it('keeps application and context unknown and opens a separate explanation without actions', async () => {
    const inspect = vi.fn(async () => messagingResult())
    open(client(inspect))
    await flushPromises()
    expect(text()).toContain('已实现')
    expect(text()).toContain('应用权限待核实')
    expect(text()).not.toContain('synthetic-app')
    click('messaging-reason-send')
    await flushPromises()
    const reason = document.querySelector('[data-testid="messaging-reason-dialog"]')!
    expect(reason.textContent).toContain('连接成功都不能证明获得权限')
    expect(reason.textContent).toContain('未选择具体会话')
    click('messaging-reason-back')
    await flushPromises()
    expect(document.querySelector('[data-testid="messaging-reason-dialog"]')).toBeNull()
    expect(document.querySelector('[data-testid="messaging-list"]')).not.toBeNull()
    expect(inspect).toHaveBeenCalledTimes(1)
  })
  it('refresh discards old success when a policy is revoked', async () => {
    const revoked = messagingResult()
    revoked.messaging!.items[0]!.policy = { state: 1, reasonCode: 'action_not_allowed' }
    const inspect = vi.fn().mockResolvedValueOnce(messagingResult()).mockResolvedValueOnce(revoked)
    open(client(inspect))
    await flushPromises()
    click('messaging-refresh')
    await flushPromises()
    expect(text()).toContain('策略限制')
    expect(text()).not.toContain('条件满足')
    click('messaging-reason-send')
    await flushPromises()
    expect(document.body.textContent).toContain('未包含在当前平台允许的动作列表中')
  })
  it('does not let a late response restore a previous identity', async () => {
    let complete!: (value: ZbaManagedInspectResult) => void
    const wrapper = open(client(() => new Promise(resolve => { complete = resolve })))
    await wrapper.setProps({ client: client(async () => ({ code: 1 })) })
    await flushPromises()
    complete(messagingResult())
    await flushPromises()
    expect(text()).toContain('没有查看权限')
    expect(document.querySelector('[data-testid="messaging-list"]')).toBeNull()
  })
  it('clears data when hidden and does not accept the hidden request later', async () => {
    let complete!: (value: ZbaManagedInspectResult) => void
    const wrapper = open(client(() => new Promise(resolve => { complete = resolve })))
    await wrapper.setProps({ active: false })
    complete(messagingResult())
    await flushPromises()
    expect(document.querySelector('[data-testid="messaging-list"]')).toBeNull()
  })
  it('old hosts expose unavailable observations without inventing grants', async () => {
    open(client(async () => ({ code: 0, descriptor: officialDescriptor })))
    await flushPromises()
    expect(text()).toContain('当前宿主未提供消息能力快照')
  })
  it('rejects a response for a different driver', async () => {
    open(client(async () => ({ ...messagingResult(), descriptor: { ...officialDescriptor, key: { kind: 'driver', key: { platformId: 'qq.official', accountScope: 'another-app', instanceId: '' } } } })))
    await flushPromises()
    expect(text()).toContain('驱动身份与当前选择不符')
    expect(document.querySelector('[data-testid="messaging-list"]')).toBeNull()
  })
  it('failed refresh clears the old snapshot and emits expiration', async () => {
    const inspect = vi.fn().mockResolvedValueOnce(messagingResult()).mockRejectedValueOnce(new ZbaRequestError(401, 'unauthorized', 'synthetic'))
    const wrapper = open(client(inspect))
    await flushPromises()
    click('messaging-refresh')
    await flushPromises()
    expect(document.querySelector('[data-testid="messaging-list"]')).toBeNull()
    expect(wrapper.emitted('auth-expired')).toHaveLength(1)
  })
})
