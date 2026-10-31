import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ProviderEditorDialog from '../../src/features/capabilities/ProviderEditorDialog.vue'
import { ZbaClient, ZbaNetworkError, type ZbaConfigurationReceipt, type ZbaConfigurationSaveRequest } from '../../src/api/zba'
import { providerSnapshot, syntheticProvider } from '../fixtures/provider-marketplace'
import { getPendingOperation } from '../../src/shared/sync/pending-operations'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount(); document.body.replaceChildren() })
function receipt(key: string): ZbaConfigurationReceipt {
  return { key, contractRevision: 'cfg-0.3.3', decision: 0, observedVersion: '1', committedVersion: '2', commitState: 1, applyState: 3, steps: [], deviceKeyAssignments: [] }
}
let clientScope = 0
function clientWith(overrides: Partial<ZbaClient> = {}): ZbaClient {
  const scope = ++clientScope
  return { baseUrl: '/synthetic/provider-' + scope, identityLabel: 'provider-test-' + scope, readConfiguration: vi.fn(async () => providerSnapshot()), saveConfiguration: vi.fn(async () => ({ code: 2, errors: [], replayed: false, currentVersion: '1' })), ...overrides } as unknown as ZbaClient
}
function mountEditor(client = clientWith(), create = false) {
  const trigger = document.createElement('button'); trigger.textContent = 'Open'; document.body.append(trigger); trigger.focus()
  const wrapper = mount(ProviderEditorDialog, { props: { client, mode: 'admin', writesEnabled: true, ...(create ? {} : { providerKey: 'provider-sample' }), trigger }, attachTo: document.body, global: { stubs: { teleport: true } } })
  mounted.push(wrapper)
  return { wrapper, trigger }
}

describe('provider editor safe configuration lifecycle', () => {
  it('defaults legacy protocol and has no existing secret plaintext input', async () => {
    const { wrapper } = mountEditor(); await flushPromises()
    expect((wrapper.get('[data-testid="provider-protocol"]').element as HTMLSelectElement).value).toBe('openai-chat')
    expect((wrapper.get('[data-testid="provider-name"]').element as HTMLInputElement).value).toBe('示例提供商')
    expect(wrapper.find('input[type="password"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('不会读取或显示已有密钥')
  })
  it('saves a create disabled with CAS and leaves enablement for a separate update', async () => {
    const requests: ZbaConfigurationSaveRequest[] = []
    const client = clientWith({ readConfiguration: vi.fn(async () => requests.length ? providerSnapshot([syntheticProvider({ key: requests[0]!.changes.registered!.providers[0]!.key })], '2') : providerSnapshot([])), saveConfiguration: vi.fn<ZbaClient['saveConfiguration']>(async (request) => { requests.push(request); return { code: 0, errors: [], replayed: false, currentVersion: '2', receipt: receipt(request.key) } }) })
    const { wrapper } = mountEditor(client, true); await flushPromises()
    await wrapper.get('[data-testid="provider-name"]').setValue('示例')
    await wrapper.get('[data-testid="provider-endpoint"]').setValue('https://example.com/v1')
    await wrapper.get('[data-testid="provider-model"]').setValue('free-text-model')
    expect(wrapper.get('[data-testid="provider-enabled"]').attributes('disabled')).toBeDefined()
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(requests).toHaveLength(1)
    expect(requests[0]).toMatchObject({ expectedVersion: '1', contractRevision: 'cfg-0.3.3', secrets: [] })
    expect(requests[0]!.key).toMatch(/^cfg1:/)
    expect(requests[0]!.changes.registered!.providers[0]).toMatchObject({ operation: 1, changes: { enabled: { kind: 1, value: false }, modelName: { kind: 1, value: 'free-text-model' } } })
    expect(wrapper.get('[data-testid="provider-notice"]').text()).toContain('需重启')
  })
  it('preserves draft on conflict and requires a new baseline before explicit resubmit', async () => {
    let reads = 0
    const saveConfiguration = vi.fn<ZbaClient['saveConfiguration']>(async (_request) => ({ code: 3 as const, errors: [], replayed: false, currentVersion: '2' }))
    const client = clientWith({ saveConfiguration, readConfiguration: vi.fn(async () => providerSnapshot([syntheticProvider({ priority: ++reads > 1 ? 8 : 0 })], String(reads))) })
    const { wrapper } = mountEditor(client); await flushPromises()
    await wrapper.get('[data-testid="provider-priority"]').setValue('5'); await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.get('[data-testid="provider-conflict"]').text()).toContain('草稿不会被替换')
    expect(wrapper.get('[data-testid="provider-save"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="provider-rebase"]').trigger('click'); await flushPromises()
    expect((wrapper.get('[data-testid="provider-priority"]').element as HTMLInputElement).value).toBe('5')
    expect(wrapper.get('[data-testid="provider-latest"]').text()).toContain('优先级 8')
    expect(saveConfiguration).toHaveBeenCalledTimes(1)
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(saveConfiguration.mock.calls[1]?.[0]).toMatchObject({ expectedVersion: '2' })
  })
  it('retains an unknown key across hidden navigation and checks without replay', async () => {
    const saved: string[] = []; const queried: string[] = []
    const client = clientWith({ saveConfiguration: vi.fn(async (request) => { saved.push(request.key); throw new ZbaNetworkError() }), findConfiguration: vi.fn<ZbaClient['findConfiguration']>(async (key) => { queried.push(key); return { code: 0, receipt: receipt(key) } }) })
    const { wrapper } = mountEditor(client); await flushPromises()
    await wrapper.get('[data-testid="provider-priority"]').setValue('5'); await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(wrapper.find('[data-testid="provider-unknown"]').exists()).toBe(true)
    await wrapper.setProps({ active: false }); await wrapper.setProps({ active: true }); await flushPromises()
    await wrapper.get('[data-testid="provider-check"]').trigger('click'); await flushPromises()
    expect(saved).toHaveLength(1); expect(queried).toEqual(saved)
    expect(wrapper.find('[data-testid="provider-unknown"]').exists()).toBe(false)
  })
  it.each([{ active: false }, { mode: 'read' as const }, { writesEnabled: false }])('clears ordinary secret input on authorization boundary %# without discarding nonsecret draft', async boundary => {
    const { wrapper } = mountEditor(); await flushPromises()
    await wrapper.get('[data-testid="provider-name"]').setValue('draft-name')
    await wrapper.get('[data-testid="provider-credential-mode"]').setValue('independent')
    await wrapper.get('[data-testid="provider-credential-key"]').setValue('synthetic-key')
    await wrapper.get('[data-testid="provider-credential-secret"]').setValue('synthetic-secret')
    await wrapper.setProps(boundary)
    expect((wrapper.get('[data-testid="provider-credential-secret"]').element as HTMLInputElement).value).toBe('')
    expect((wrapper.get('[data-testid="provider-name"]').element as HTMLInputElement).value).toBe('draft-name')
  })
  it('clears confirmed secrets before a failed follow-up read', async () => {
    const readConfiguration = vi.fn<ZbaClient['readConfiguration']>().mockResolvedValueOnce(providerSnapshot()).mockRejectedValueOnce(new ZbaNetworkError())
    const client = clientWith({ readConfiguration, saveConfiguration: vi.fn<ZbaClient['saveConfiguration']>(async request => ({ code: 0, errors: [], replayed: false, currentVersion: '2', receipt: receipt(request.key) })) })
    const { wrapper } = mountEditor(client); await flushPromises()
    await wrapper.get('[data-testid="provider-credential-mode"]').setValue('independent')
    await wrapper.get('[data-testid="provider-credential-key"]').setValue('synthetic-key')
    await wrapper.get('[data-testid="provider-credential-secret"]').setValue('synthetic-secret')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect((wrapper.get('[data-testid="provider-credential-secret"]').element as HTMLInputElement).value).toBe('')
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
  })
  it('clears secret input on client identity replacement', async () => {
    const { wrapper } = mountEditor(); await flushPromises()
    await wrapper.get('[data-testid="provider-credential-mode"]').setValue('independent')
    await wrapper.get('[data-testid="provider-credential-secret"]').setValue('synthetic-secret')
    await wrapper.setProps({ client: clientWith() })
    expect((wrapper.get('[data-testid="provider-credential-secret"]').element as HTMLInputElement).value).toBe('')
  })
  it('exposes lock cleanup while preventing unconfirmed downgrade', async () => {
    const client = clientWith({ saveConfiguration: vi.fn(async () => { throw new ZbaNetworkError() }) })
    const { wrapper } = mountEditor(client); await flushPromises()
    await wrapper.get('[data-testid="provider-credential-mode"]').setValue('independent')
    await wrapper.get('[data-testid="provider-credential-key"]').setValue('synthetic-key')
    await wrapper.get('[data-testid="provider-credential-secret"]').setValue('synthetic-secret')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(await wrapper.vm.requestLeave('downgrade')).toBe(false)
    expect(await wrapper.vm.requestLeave('lock')).toBe(true)
    expect((wrapper.get('[data-testid="provider-credential-secret"]').element as HTMLInputElement).value).toBe('')
    expect(getPendingOperation(client, 'configuration', 'provider:provider-sample')).not.toBeNull()
  })
  it('reopens unknown provider submission under its original key without secrets or replay', async () => {
    const client = clientWith({ saveConfiguration: vi.fn(async () => { throw new ZbaNetworkError() }), findConfiguration: vi.fn(async () => ({ code: 12 as const })) })
    const { wrapper } = mountEditor(client); await flushPromises()
    await wrapper.get('[data-testid="provider-priority"]').setValue('5')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    const key = vi.mocked(client.saveConfiguration).mock.calls[0]![0].key
    wrapper.unmount(); mounted.splice(mounted.indexOf(wrapper), 1)
    const { wrapper: reopened } = mountEditor(client); await flushPromises()
    expect(reopened.find('[data-testid="provider-unknown"]').exists()).toBe(true)
    await reopened.get('[data-testid="provider-check"]').trigger('click'); await flushPromises()
    expect(client.findConfiguration).toHaveBeenCalledWith(key)
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
    expect(getPendingOperation(client, 'configuration', 'provider:provider-sample')?.meta).toEqual({})
  })
  it('records a late provider receipt without allowing another mounted owner to use stale baseline', async () => {
    let resolve!: (result: Awaited<ReturnType<ZbaClient['saveConfiguration']>>) => void
    const client = clientWith({ saveConfiguration: vi.fn<ZbaClient['saveConfiguration']>(() => new Promise(result => { resolve = result })) })
    const { wrapper } = mountEditor(client); await flushPromises()
    await wrapper.get('[data-testid="provider-priority"]').setValue('5')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    const key = vi.mocked(client.saveConfiguration).mock.calls[0]![0].key
    wrapper.unmount(); mounted.splice(mounted.indexOf(wrapper), 1)
    const { wrapper: reopened } = mountEditor(client); await flushPromises()
    resolve({ code: 0, errors: [], replayed: false, currentVersion: '2', receipt: receipt(key) }); await flushPromises()
    expect(getPendingOperation(client, 'configuration', 'provider:provider-sample')).toBeNull()
    expect(reopened.find('[data-testid="provider-conflict"]').exists()).toBe(true)
    expect(reopened.get('[data-testid="provider-save"]').attributes('disabled')).toBeDefined()
    await reopened.get('[data-testid="provider-rebase"]').trigger('click'); await flushPromises()
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
    expect(reopened.find('[data-testid="provider-conflict"]').exists()).toBe(false)
  })
  it('recovers an unknown create without generating a new provider key', async () => {
    const client = clientWith({ readConfiguration: vi.fn(async () => providerSnapshot([])), saveConfiguration: vi.fn(async () => { throw new ZbaNetworkError() }), findConfiguration: vi.fn(async () => ({ code: 12 as const })) })
    const { wrapper } = mountEditor(client, true); await flushPromises()
    await wrapper.get('[data-testid="provider-name"]').setValue('synthetic-create')
    await wrapper.get('[data-testid="provider-endpoint"]').setValue('https://example.com/v1')
    await wrapper.get('[data-testid="provider-model"]').setValue('synthetic-model')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    const request = vi.mocked(client.saveConfiguration).mock.calls[0]![0]
    const providerKey = request.changes.registered!.providers[0]!.key
    wrapper.unmount(); mounted.splice(mounted.indexOf(wrapper), 1)
    const { wrapper: reopened } = mountEditor(client, true); await flushPromises()
    expect(reopened.find('[data-testid="provider-unknown"]').exists()).toBe(true)
    expect(getPendingOperation(client, 'configuration', 'provider:create:' + providerKey)?.key).toBe(request.key)
    await reopened.get('[data-testid="provider-check"]').trigger('click'); await flushPromises()
    expect(client.findConfiguration).toHaveBeenCalledWith(request.key)
    expect(client.saveConfiguration).toHaveBeenCalledTimes(1)
  })
  it('uses the existing-provider recovery target after a confirmed create and later unknown edit', async () => {
    let providerKey = ''
    const saveConfiguration = vi.fn<ZbaClient['saveConfiguration']>().mockImplementationOnce(async request => {
      providerKey = request.changes.registered!.providers[0]!.key
      return { code: 0, errors: [], replayed: false, currentVersion: '2', receipt: receipt(request.key) }
    }).mockRejectedValue(new ZbaNetworkError())
    const client = clientWith({ saveConfiguration, readConfiguration: vi.fn(async () => providerSnapshot(providerKey ? [syntheticProvider({ key: providerKey })] : [])), findConfiguration: vi.fn(async () => ({ code: 12 as const })) })
    const { wrapper } = mountEditor(client, true); await flushPromises()
    await wrapper.get('[data-testid="provider-name"]').setValue('synthetic-create')
    await wrapper.get('[data-testid="provider-endpoint"]').setValue('https://example.com/v1')
    await wrapper.get('[data-testid="provider-model"]').setValue('synthetic-model')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    await wrapper.get('[data-testid="provider-priority"]').setValue('5')
    await wrapper.get('form').trigger('submit'); await flushPromises()
    const key = saveConfiguration.mock.calls[1]![0].key
    expect(getPendingOperation(client, 'configuration', 'provider:' + providerKey)?.key).toBe(key)
    wrapper.unmount(); mounted.splice(mounted.indexOf(wrapper), 1)
    const reopened = mount(ProviderEditorDialog, { props: { client, mode: 'admin', writesEnabled: true, providerKey }, global: { stubs: { teleport: true } } }); mounted.push(reopened)
    await flushPromises()
    expect(reopened.find('[data-testid="provider-unknown"]').exists()).toBe(true)
    await reopened.get('[data-testid="provider-check"]').trigger('click'); await flushPromises()
    expect(client.findConfiguration).toHaveBeenCalledWith(key)
    expect(saveConfiguration).toHaveBeenCalledTimes(2)
  })
  it('disables writes after downgrade and restores focus to the actual trigger', async () => {
    const saveConfiguration = vi.fn(); const { wrapper, trigger } = mountEditor(clientWith({ saveConfiguration })); await flushPromises()
    await wrapper.setProps({ mode: 'read' }); await wrapper.get('form').trigger('submit'); await flushPromises()
    expect(saveConfiguration).not.toHaveBeenCalled()
    await wrapper.get('[data-testid="provider-editor-close"]').trigger('click')
    expect(wrapper.emitted('close')).toHaveLength(1)
    wrapper.unmount(); mounted.splice(mounted.indexOf(wrapper), 1); await flushPromises()
    expect(document.activeElement).toBe(trigger)
  })
})
