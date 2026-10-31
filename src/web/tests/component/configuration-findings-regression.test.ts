import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import ConfigurationPage from '../../src/features/configuration/ConfigurationPage.vue'
import { ZbaClient, ZbaRequestError, type ZbaConfigurationReadResult, type ZbaConfigurationSaveRequest, type ZbaConfigurationSaveResult } from '../../src/api/zba'
import fixture from '../fixtures/configuration-read-editable-result.json'
import { getPendingOperation, markOperation } from '../../src/shared/sync/pending-operations'

const mounted: Array<{ unmount: () => void }> = []
let identity = 0
afterEach(() => { mounted.splice(0).forEach(w => w.unmount()); document.body.replaceChildren() })
function snapshot(version = '1'): ZbaConfigurationReadResult {
  const result = structuredClone(fixture) as unknown as ZbaConfigurationReadResult
  result.version = version
  return result
}
function clientWith(overrides: Partial<ZbaClient>): ZbaClient {
  const id = ++identity
  return { baseUrl: '/synthetic-configuration-' + id, identityLabel: 'configuration-test-' + id, ...overrides } as ZbaClient
}
function mountPage(client: ZbaClient, scope = 'basic') {
  const wrapper = mount(ConfigurationPage, { props: { client, mode: 'admin', writesEnabled: true, scope } })
  mounted.push(wrapper)
  return wrapper
}
async function save(wrapper: ReturnType<typeof mountPage>) {
  await wrapper.get('[data-testid="config-save"]').trigger('click')
  await flushPromises()
}
const conflict = (): ZbaConfigurationSaveResult => ({ code: 3, replayed: false, currentVersion: '2', errors: [] })

describe('configuration findings desired behavior', () => {
  it('a late unknown save cannot relock a record already settled by another observer', async () => {
    let release!: (result: ZbaConfigurationSaveResult) => void
    const gate = new Promise<ZbaConfigurationSaveResult>(resolve => { release = resolve })
    let key = ''
    const client = clientWith({ readConfiguration: async () => snapshot(), saveConfiguration: async request => { key = request.key; return gate } })
    const wrapper = mountPage(client)
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic retained')
    await save(wrapper)
    markOperation(client, key, 'settled')
    await flushPromises()
    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)
    release({ code: 12, replayed: false, currentVersion: '1', errors: [] })
    await flushPromises()
    expect(getPendingOperation(client, 'configuration', 'config:basic')).toBeNull()
    expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)
  })

  it.each(['save', 'find'] as const)('code0 %s with mismatched or unknown receipt keeps original key and draft', async operation => {
    for (const bad of ['key', 'commitState'] as const) {
      const current = snapshot()
      let originalKey = ''
      const response = () => ({ key: bad === 'key' ? 'synthetic-wrong-key' : originalKey, contractRevision: 'cfg-0.3.3', decision: 0 as const, observedVersion: '1', committedVersion: '2', commitState: (bad === 'commitState' ? 2 : 1) as 1 | 2, applyState: 1 as const, steps: [], deviceKeyAssignments: [] })
      const client = clientWith({ readConfiguration: async () => current, saveConfiguration: async request => {
        originalKey = request.key
        return operation === 'save' ? { code: 0, replayed: false, currentVersion: '2', errors: [], receipt: response() } : { code: 12, replayed: false, currentVersion: '1', errors: [] }
      }, findConfiguration: async () => ({ code: 0, receipt: response() }) })
      const wrapper = mountPage(client)
      await flushPromises()
      await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic retained')
      await save(wrapper)
      if (operation === 'find') { await wrapper.get('[data-testid="config-check-pending"]').trigger('click'); await flushPromises() }
      expect(wrapper.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
      expect(wrapper.get('[data-testid="config-save-bar"]').text()).toContain('1 项修改')
      expect(getPendingOperation(client, 'configuration', 'config:basic')?.key).toBe(originalKey)
      expect(wrapper.find('[data-testid="config-receipt"]').exists()).toBe(false)
    }
  })

  it('lock clears secret drafts immediately and remount restores the same unknown key without replay', async () => {
    const current = snapshot()
    const keys: string[] = []
    const finds: string[] = []
    const client = clientWith({ readConfiguration: async () => current, saveConfiguration: async request => {
      keys.push(request.key)
      throw new ZbaRequestError(503, 'synthetic_unknown', 'synthetic')
    }, findConfiguration: async key => { finds.push(key); return { code: 14 } } })
    const first = mountPage(client, 'models')
    await flushPromises()
    await first.get('[data-testid="secret-replace"]').trigger('click')
    await first.get('[data-testid="config-secrets"] .secret-actions input').setValue('synthetic-secret')
    await save(first)
    const api = first.vm as unknown as { requestLeave: (reason: 'lock') => Promise<boolean>; endSession: () => void }
    await expect(api.requestLeave('lock')).resolves.toBe(true)
    api.endSession()
    first.unmount()
    const second = mountPage(client, 'models')
    await flushPromises()
    expect(second.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(second.find('[data-testid="config-secrets"] .secret-actions input').exists()).toBe(false)
    expect(getPendingOperation(client, 'configuration', 'config:models')?.key).toBe(keys[0])
    await second.get('[data-testid="config-check-pending"]').trigger('click')
    await flushPromises()
    expect(finds).toEqual(keys)
    expect(keys).toHaveLength(1)
    expect(getPendingOperation(client, 'configuration', 'config:models')).toBeNull()
  })

  it('late completion after unmount settles its original record and forces fresh baseline in a restored editor', async () => {
    const current = snapshot()
    let release!: (result: ZbaConfigurationSaveResult) => void
    const delayed = new Promise<ZbaConfigurationSaveResult>(resolve => { release = resolve })
    const keys: string[] = []
    const client = clientWith({ readConfiguration: async () => current, saveConfiguration: async r => { keys.push(r.key); return delayed } })
    const first = mountPage(client)
    await flushPromises()
    await first.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic local')
    await save(first)
    first.unmount()
    const second = mountPage(client)
    await flushPromises()
    expect(second.find('[data-testid="config-unconfirmed"]').exists()).toBe(true)
    expect(second.get('[data-testid="config-check-pending"]').attributes('disabled')).toBeDefined()
    release({ code: 3, replayed: false, currentVersion: '2', errors: [] })
    await flushPromises()
    expect(getPendingOperation(client, 'configuration', 'config:basic')).toBeNull()
    expect(second.find('[data-testid="config-conflict"]').exists()).toBe(true)
    expect(second.find('[data-testid="config-receipt"]').exists()).toBe(false)
    expect(keys).toHaveLength(1)
  })

  it('F08 failed conflict reread retains the conflict and old CAS even with cached V2', async () => {
    let current = snapshot()
    let failing = false
    const versions: string[] = []
    const client = clientWith({ readConfiguration: async () => {
      if (failing) throw new ZbaRequestError(503, 'synthetic_read_failure', 'synthetic')
      return current
    }, saveConfiguration: async r => { versions.push(r.expectedVersion); return conflict() } })
    const wrapper = mountPage(client)
    await flushPromises()
    await wrapper.get('[data-testid="config-field-botPersona"] input').setValue('Synthetic local draft')
    current = snapshot('2')
    await (wrapper.vm as unknown as { reload: () => Promise<boolean> }).reload()
    await save(wrapper)
    failing = true
    await wrapper.get('[data-testid="config-reload-conflict"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="config-conflict"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="config-save"]').attributes('disabled')).toBeDefined()
    expect(wrapper.text()).not.toContain('已读取最新快照')
    await save(wrapper)
    expect(versions).toEqual(['1'])
    failing = false
    await wrapper.get('[data-testid="config-reload-conflict"]').trigger('click')
    await flushPromises()
    await save(wrapper)
    expect(versions).toEqual(['1', '2'])
  })

  it.each(['replace', 'delete'] as const)('F09 secret %s requires explicit reconfirmation after an uncomparable conflict', async action => {
    let current = snapshot()
    const requests: ZbaConfigurationSaveRequest[] = []
    const client = clientWith({ readConfiguration: async () => current, saveConfiguration: async r => { requests.push(r); return conflict() } })
    const wrapper = mountPage(client, 'models')
    await flushPromises()
    await wrapper.get('[data-testid="secret-' + action + '"]').trigger('click')
    if (action === 'replace') await wrapper.get('[data-testid="config-secrets"] .secret-actions input').setValue('synthetic-local-secret')
    current = snapshot('2')
    await save(wrapper)
    await wrapper.get('[data-testid="config-reload-conflict"]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).not.toContain('没有重叠字段')
    expect(wrapper.text()).toContain('秘密值无法比较')
    if (action === 'replace') expect(wrapper.get<HTMLInputElement>('[data-testid="config-secrets"] .secret-actions input').element.value).toBe('')
    expect(wrapper.get('[data-testid="config-save"]').attributes('disabled')).toBeDefined()
    await save(wrapper)
    expect(requests).toHaveLength(1)
    await wrapper.get('[data-testid="config-secret-reconfirm-0"]').setValue(true)
    if (action === 'replace') await wrapper.get('[data-testid="config-secrets"] .secret-actions input').setValue('synthetic-reentered-secret')
    await save(wrapper)
    expect(requests.map(r => r.expectedVersion)).toEqual(['1', '2'])
    expect(requests[1]!.secrets[0]!.kind).toBe(action === 'replace' ? 1 : 2)
  })

  it.each(['validate', 'save'] as const)('F15 %s locates backend field 69 on its actual input', async operation => {
    const current = snapshot()
    current.view!.behavior.maxConcurrentReplies = { ...current.view!.behavior.botPersona!, value: 8, rule: { ...current.view!.behavior.botPersona!.rule, id: 69 } }
    const errors = [{ address: { field: 69 }, code: 'out_of_range' }]
    const client = clientWith({ readConfiguration: async () => current, validateConfiguration: async () => ({ code: 2, errors }), saveConfiguration: async () => ({ code: 2, errors, replayed: false, currentVersion: '1' }) })
    const wrapper = mountPage(client)
    await flushPromises()
    await wrapper.get('[data-testid="config-field-maxConcurrentReplies"] input').setValue('17')
    await wrapper.get('[data-testid="config-' + operation + '"]').trigger('click')
    await flushPromises()
    const input = wrapper.get('[data-testid="config-field-maxConcurrentReplies"] input')
    expect(input.attributes('aria-invalid')).toBe('true')
    expect(wrapper.get('[id="' + input.attributes('aria-describedby') + '"]').text()).toContain('并发回复上限')
    expect(wrapper.get('[data-testid="config-field-botPersona"] input').attributes('aria-invalid')).toBeUndefined()
    expect(wrapper.text()).not.toContain('字段 69')
  })
})
