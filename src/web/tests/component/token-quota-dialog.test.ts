import { afterEach, describe, expect, it, vi } from 'vitest'
import { DOMWrapper, flushPromises, mount } from '@vue/test-utils'
import TokenQuotaDialog from '../../src/features/capabilities/TokenQuotaDialog.vue'
import CapabilitiesPage from '../../src/features/capabilities/CapabilitiesPage.vue'
import { settingsFor } from '../../src/features/capabilities/plugin-settings'
import { ZbaClient, ZbaNetworkError, ZbaRequestError } from '../../src/api/zba'
import { quota, quotaFeatures } from '../fixtures/quotas'
import { providerSnapshot } from '../fixtures/provider-marketplace'
const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount(); document.body.replaceChildren() })
function clientWith(overrides: Partial<ZbaClient> = {}): ZbaClient {
  return { listQuotas: vi.fn(async () => ({ items: [quota(), quota({ sourceKey: 'group:20002', displayName: '群聊 200***02' })] })),
    saveQuota: vi.fn(async (request) => ({ quota: quota({ dailyTokenLimit: request.dailyTokenLimit, revision: '2' }) })),
    findQuota: vi.fn(async () => ({ found: false })), ...overrides } as unknown as ZbaClient
}
function editor(client = clientWith(), props = {}) {
  const trigger = document.createElement('button'); document.body.append(trigger); trigger.focus()
  const wrapper = mount(TokenQuotaDialog, { props: { client, mode: 'admin', writesEnabled: true, features: quotaFeatures, trigger, ...props }, attachTo: document.body, global: { stubs: { teleport: true } } })
  mounted.push(wrapper); return { wrapper, trigger }
}
async function change(wrapper: ReturnType<typeof editor>['wrapper'], value = '120000') {
  await flushPromises(); await wrapper.get('[data-testid="quota-edit-0"]').trigger('click'); await wrapper.get('[data-testid="quota-limit"]').setValue(value)
}
async function submit(wrapper: ReturnType<typeof editor>['wrapper']) { await wrapper.get('form').trigger('submit'); await flushPromises() }
describe('quota dialog single-intent lifecycle', () => {
  it('keeps an unknown plugin enablement intent mounted and blocks concurrent quota editing or close', async () => {
    const snapshot = providerSnapshot()
    snapshot.view!.registered.plugins = [{ key: 'preset.feature.token-quota', desiredEnabled: true }]
    const client = clientWith({ readConfiguration: vi.fn(async () => snapshot), saveConfiguration: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const owner = mount(TokenQuotaDialog, { props: { client, mode: 'admin', writesEnabled: true, features: quotaFeatures }, attachTo: document.body })
    mounted.push(owner)
    const wrapper = new DOMWrapper(document.body)
    await flushPromises()
    expect(wrapper.get('[data-testid="quota-plugin-toggle"]').attributes('disabled')).toBeUndefined()
    await wrapper.get('[data-testid="quota-plugin-toggle"]').trigger('click'); await flushPromises()
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
    expect(wrapper.find('[data-testid="quota-plugin-check"]').exists(), wrapper.get('[data-testid="quota-plugin-switch"]').text()).toBe(true)
    expect(wrapper.get('[data-testid="quota-edit-0"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="token-quota-dialog-close"]').trigger('click')
    await wrapper.get('[role="dialog"]').trigger('keydown', { key: 'Escape' })
    expect(owner.emitted('close')).toBeUndefined()
    await owner.setProps({ active: false }); await owner.setProps({ active: true }); await flushPromises()
    expect(wrapper.find('[data-testid="quota-plugin-check"]').exists()).toBe(true)
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1); expect(client.saveQuota).not.toHaveBeenCalled()
  })
  it('shows masked labels and usage/reset, writes raw key only with CAS then refreshes', async () => {
    const client = clientWith(); const { wrapper } = editor(client); await change(wrapper)
    expect(wrapper.text()).toContain('群聊 100***01'); expect(wrapper.html()).not.toContain('group:10001')
    expect(wrapper.text()).toContain('1,500'); expect(wrapper.text()).toContain('98,500'); expect(wrapper.text()).toContain('2026-10-29')
    expect(wrapper.get('[data-testid="quota-edit-1"]').attributes('disabled')).toBeDefined()
    await submit(wrapper)
    expect(client.saveQuota).toHaveBeenCalledTimes(1)
    expect(vi.mocked(client.saveQuota).mock.calls[0]?.[0]).toMatchObject({ sourceKey: 'group:10001', expectedRevision: '1', dailyTokenLimit: 120000 })
    expect(vi.mocked(client.saveQuota).mock.calls[0]?.[0].idempotencyKey).toMatch(/^[a-f0-9]{32}$/)
    expect(client.listQuotas).toHaveBeenCalledTimes(2)
    expect(wrapper.find('form').exists()).toBe(false)
  })
  it.each([{ mode: 'read' }, { writesEnabled: false }, { features: ['quotas.list', 'quotas.find'] }, { features: ['quotas.list', 'quotas.save'] }])('gates write permissions %#', async props => {
    const client = clientWith(); const { wrapper } = editor(client, props); await flushPromises()
    expect(wrapper.get('[data-testid="quota-edit-0"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="quota-edit-0"]').trigger('click'); expect(wrapper.find('form').exists()).toBe(false)
    expect(client.saveQuota).not.toHaveBeenCalled()
  })
  it('does not request unsupported listing', async () => {
    const client = clientWith(); const { wrapper } = editor(client, { features: [] }); await flushPromises()
    expect(client.listQuotas).not.toHaveBeenCalled(); expect(wrapper.find('[data-testid="quota-unavailable"]').exists()).toBe(true)
  })
  it('preserves conflicting draft and requires rebase plus explicit new-key save', async () => {
    const listQuotas = vi.fn<ZbaClient['listQuotas']>().mockResolvedValueOnce({ items: [quota()] }).mockResolvedValue({ items: [quota({ revision: '2', dailyTokenLimit: 80000 })] })
    const saveQuota = vi.fn<ZbaClient['saveQuota']>().mockRejectedValue(new ZbaRequestError(409, 'quota_revision_conflict', 'synthetic'))
    const { wrapper } = editor(clientWith({ listQuotas, saveQuota })); await change(wrapper); await submit(wrapper)
    expect(wrapper.find('[data-testid="quota-conflict"]').exists()).toBe(true); expect(wrapper.get('[data-testid="quota-save"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="quota-rebase"]').trigger('click'); await flushPromises()
    expect((wrapper.get('[data-testid="quota-limit"]').element as HTMLInputElement).value).toBe('120000')
    expect(wrapper.get('[data-testid="quota-latest"]').text()).toContain('80,000'); expect(saveQuota).toHaveBeenCalledTimes(1)
    await submit(wrapper); expect(saveQuota.mock.calls[1]?.[0].expectedRevision).toBe('2')
    expect(saveQuota.mock.calls[1]?.[0].idempotencyKey).not.toBe(saveQuota.mock.calls[0]?.[0].idempotencyKey)
  })
  it.each([new ZbaNetworkError(), new ZbaRequestError(200, 'invalid_json', 'synthetic'), new ZbaRequestError(503, 'unavailable', 'synthetic'), new ZbaRequestError(409, 'idempotency_conflict', 'synthetic')])('locks unknown mutation and never replays %#', async cause => {
    const client = clientWith({ saveQuota: vi.fn(async () => { throw cause }) }); const { wrapper } = editor(client)
    await change(wrapper); await submit(wrapper)
    expect(wrapper.find('[data-testid="quota-unknown"]').exists()).toBe(true)
    expect(document.activeElement?.textContent).toContain('已知来源')
    await wrapper.get('[data-testid="token-quota-dialog-close"]').trigger('click')
    await wrapper.get('[role="dialog"]').trigger('keydown', { key: 'Escape' }); expect(wrapper.emitted('close')).toBeUndefined()
    expect(wrapper.get('[data-testid="quota-limit"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="quota-refresh"]').attributes('disabled')).toBeDefined()
    await submit(wrapper); expect(client.saveQuota).toHaveBeenCalledTimes(1)
    await wrapper.setProps({ active: false }); await wrapper.setProps({ active: true }); await flushPromises()
    await wrapper.get('[data-testid="quota-check"]').trigger('click'); await flushPromises()
    expect(client.findQuota).toHaveBeenCalledWith(vi.mocked(client.saveQuota).mock.calls[0]?.[0].idempotencyKey)
    expect(wrapper.find('[data-testid="quota-unknown"]').exists()).toBe(true)
    expect(wrapper.text()).toContain('不能确认没有执行'); expect(client.listQuotas).toHaveBeenCalledTimes(1)
  })
  it('clears confirmed draft before failed refresh and locks editing until reload', async () => {
    const listQuotas = vi.fn<ZbaClient['listQuotas']>().mockResolvedValueOnce({ items: [quota()] }).mockRejectedValueOnce(new ZbaNetworkError()).mockResolvedValue({ items: [quota({ dailyTokenLimit: 120000, revision: '2' })] })
    const client = clientWith({ listQuotas, saveQuota: vi.fn(async () => { throw new ZbaNetworkError() }), findQuota: vi.fn<ZbaClient['findQuota']>(async () => ({ found: true, quota: quota({ dailyTokenLimit: 120000, revision: '2' }) })) })
    const { wrapper } = editor(client); await change(wrapper); await submit(wrapper)
    await wrapper.get('[data-testid="quota-check"]').trigger('click'); await flushPromises()
    expect(wrapper.find('form').exists()).toBe(false); expect(wrapper.find('[data-testid="quota-unknown"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('已确认保存'); expect(wrapper.get('[data-testid="quota-edit-0"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="quota-refresh"]').trigger('click'); await flushPromises()
    expect(wrapper.get('[data-testid="quota-edit-0"]').attributes('disabled')).toBeUndefined(); expect(client.saveQuota).toHaveBeenCalledTimes(1)
  })
  it('keeps unknown when receipt mismatches or lookup authorization expires', async () => {
    const findQuota = vi.fn<ZbaClient['findQuota']>().mockResolvedValueOnce({ found: true, quota: quota({ dailyTokenLimit: 70000 }) }).mockRejectedValueOnce(new ZbaRequestError(401, 'unauthorized', 'synthetic'))
    const { wrapper } = editor(clientWith({ saveQuota: vi.fn(async () => { throw new ZbaNetworkError() }), findQuota }))
    await change(wrapper); await submit(wrapper); await wrapper.get('[data-testid="quota-check"]').trigger('click'); await flushPromises()
    expect(wrapper.text()).toContain('回执与本次提交不匹配')
    await wrapper.get('[data-testid="quota-check"]').trigger('click'); await flushPromises()
    expect(wrapper.emitted('auth-expired')).toHaveLength(1); expect(wrapper.find('[data-testid="quota-unknown"]').exists()).toBe(true)
  })
  it.each(['list', 'save'])('reports expired auth from %s', async operation => {
    const expired = new ZbaRequestError(401, 'unauthorized', 'synthetic')
    const client = clientWith(operation === 'list' ? { listQuotas: vi.fn(async () => { throw expired }) } : { saveQuota: vi.fn(async () => { throw expired }) })
    const { wrapper } = editor(client); await flushPromises()
    if (operation === 'save') { await change(wrapper); await submit(wrapper) }
    expect(wrapper.emitted('auth-expired')).toHaveLength(1)
  })
  it('validates integer bounds and blocks writes after downgrade', async () => {
    const client = clientWith(); const { wrapper } = editor(client); await change(wrapper, '1.5'); await submit(wrapper)
    expect(wrapper.get('[data-testid="quota-validation"]').text()).toContain('整数'); expect(client.saveQuota).not.toHaveBeenCalled()
    await wrapper.get('[data-testid="quota-limit"]').setValue('120000'); await wrapper.setProps({ mode: 'read' }); await submit(wrapper); expect(client.saveQuota).not.toHaveBeenCalled()
  })
  it('prompts discard on Escape and restores focus on close; Tab stays in dialog', async () => {
    const { wrapper, trigger } = editor(); await change(wrapper)
    await wrapper.get('[role="dialog"]').trigger('keydown', { key: 'Escape' }); expect(wrapper.emitted('close')).toBeUndefined()
    expect(wrapper.find('[data-testid="quota-discard"]').exists()).toBe(true)
    const last = wrapper.get('[data-testid="quota-discard"]').findAll('button').at(-1)!
    ;(last.element as HTMLElement).focus(); await last.trigger('keydown', { key: 'Tab' })
    expect(document.activeElement).toBe(wrapper.get('[data-testid="token-quota-dialog-close"]').element)
    await wrapper.get('[data-testid="quota-discard-confirm"]').trigger('click'); expect(wrapper.emitted('close')).toHaveLength(1)
    wrapper.unmount(); mounted.splice(mounted.indexOf(wrapper), 1); await flushPromises(); expect(document.activeElement).toBe(trigger)
  })
  it('adds preset card settings without an enableField and preserves quota intent on navigation', async () => {
    expect(settingsFor('preset.feature.token-quota')?.enableField).toBeUndefined()
    const client = clientWith({ readConfiguration: vi.fn<ZbaClient['readConfiguration']>(async () => ({ code: 0, version: '1', generation: 'synthetic', contractRevision: 'cfg-0.3.3', serverUnixSeconds: 1, secrets: [], fields: [], latestApplyState: 1 })), listCapabilities: vi.fn<ZbaClient['listCapabilities']>(async () => ({ code: 0, descriptorRevision: '1', items: [{ key: { kind: 'plugin', key: 'preset.feature.token-quota' }, displayName: 'plugin', desiredEnabled: true, runtimeState: 4, actions: [], credentialConfigured: false, descriptorRevision: '1' }] })), saveQuota: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const wrapper = mount(CapabilitiesPage, { props: { client, mode: 'admin', writesEnabled: true, features: quotaFeatures }, attachTo: document.body, global: { stubs: { teleport: true } } }); mounted.push(wrapper)
    await flushPromises(); expect(wrapper.get('[data-testid="cap-item-plugin:preset.feature.token-quota"]').text()).toContain('Token 配额')
    await wrapper.get('[data-testid="cap-settings-plugin:preset.feature.token-quota"]').trigger('click'); await flushPromises()
    await wrapper.get('[data-testid="quota-edit-0"]').trigger('click'); await wrapper.get('[data-testid="quota-limit"]').setValue('120000'); await wrapper.get('form').trigger('submit'); await flushPromises()
    await wrapper.setProps({ active: false }); await wrapper.setProps({ active: true }); await flushPromises()
    expect(wrapper.find('[data-testid="quota-unknown"]').exists()).toBe(true); expect(client.saveQuota).toHaveBeenCalledTimes(1)
  })
})
