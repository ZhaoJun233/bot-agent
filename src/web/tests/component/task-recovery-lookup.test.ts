import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { ZbaClient } from '../../src/api/zba'
import TaskRecoveryLookup from '../../src/features/tasks/TaskRecoveryLookup.vue'

const task = { id: 'synthetic-task', state: 3, target: 'server', sessionId: 'synthetic-session',
  createdAt: '2026-11-09T00:00:00Z', updatedAt: '2026-11-09T00:00:00Z', cancellationRequested: false }
const found = { code: 'found', instanceId: 'synthetic-instance', task }
const response = (body: unknown, status = 200) => new Response(JSON.stringify(body), { status })
const mounted: Array<{ unmount(): void }> = []
afterEach(() => { for (const item of mounted.splice(0)) item.unmount() })

function setup(fetchImpl: typeof fetch, enabled = true) {
  const client = new ZbaClient({ token: 'synthetic-admin', fetchImpl })
  const wrapper = mount(TaskRecoveryLookup, { props: { client, enabled } })
  mounted.push(wrapper)
  return { wrapper, client }
}
async function find(wrapper: ReturnType<typeof setup>['wrapper'], key = 'synthetic-original-key') {
  await wrapper.get('input').setValue(key)
  await wrapper.get('form').trigger('submit')
  await flushPromises()
}

describe('manual original-key recovery', () => {
  it.each(['A._~-b12', 'a'.repeat(128)])('queries the exact valid key without submitting or manufacturing an observation: %s', async key => {
    const fetchImpl = vi.fn<typeof fetch>().mockResolvedValue(response(found))
    const { wrapper, client } = setup(fetchImpl)
    await find(wrapper, key)
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/zba/v1/tasks/find')
    expect(fetchImpl.mock.calls[0]?.[1]).toMatchObject({ method: 'POST', body: JSON.stringify({ key }) })
    expect(wrapper.emitted('found')).toEqual([[key, task]])
    await wrapper.get('[data-testid="task-recovery-open"]').trigger('click')
    expect(wrapper.emitted('open-task')).toEqual([[task]])
    expect(client.getTaskObservation()).toMatchObject({ attempts: 0, accepted: 0 })
  })

  it.each(['', 'short', 'a'.repeat(129), 'synthetic key', 'synthetic/invalid', '中文synthetic'])('rejects invalid keys before network: %s', async key => {
    const fetchImpl = vi.fn<typeof fetch>()
    const { wrapper } = setup(fetchImpl)
    await find(wrapper, key)
    expect(fetchImpl).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="task-recovery-find"]').attributes('disabled')).toBeDefined()
  })

  it.each(['expired', 'accepted_unknown', 'unknown'])('keeps %s separate from found and offers no replay', async code => {
    const fetchImpl = vi.fn<typeof fetch>().mockResolvedValue(response({ code, instanceId: 'synthetic-instance', taskId: task.id }))
    const { wrapper } = setup(fetchImpl)
    await find(wrapper)
    expect(wrapper.get('[data-testid="task-recovery-code"]').text()).not.toContain('已找到')
    expect(wrapper.text()).toContain('不能据此判断可以安全重做')
    expect(wrapper.find('[data-testid="task-recovery-open"]').exists()).toBe(false)
    expect(wrapper.emitted('found')).toBeUndefined()
    expect(fetchImpl).toHaveBeenCalledTimes(1)
  })

  it('gates read mode even if the form submit handler is invoked directly', async () => {
    const fetchImpl = vi.fn<typeof fetch>()
    const { wrapper } = setup(fetchImpl, false)
    await find(wrapper)
    expect(fetchImpl).not.toHaveBeenCalled()
  })

  it.each([200, 503])('does not accept an invalid or failed lookup (HTTP %s)', async status => {
    const body = status === 200 ? { code: 'found', instanceId: 'synthetic-instance' } : found
    const { wrapper } = setup(async () => response(body, status))
    await find(wrapper)
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="task-recovery-open"]').exists()).toBe(false)
  })

  it.each([401, 403])('forwards authentication failure %s', async status => {
    const { wrapper } = setup(async () => response({ error: { code: 'synthetic_auth', message: 'synthetic' } }, status))
    await find(wrapper)
    expect(wrapper.emitted('auth-expired')?.[0]?.[0]).toMatchObject({ status })
  })

  it.each(['client', 'permission', 'key', 'unmount'])('discards a late response after %s changes', async change => {
    let finish: (value: Response) => void = () => { throw new Error('request not started') }
    const fetchImpl = vi.fn<typeof fetch>().mockImplementation(() => new Promise(resolve => { finish = resolve }))
    const { wrapper } = setup(fetchImpl)
    await wrapper.get('input').setValue('synthetic-original-key')
    await wrapper.get('form').trigger('submit')
    await wrapper.get('form').trigger('submit')
    expect(fetchImpl).toHaveBeenCalledTimes(1)
    if (change === 'client') await wrapper.setProps({ client: new ZbaClient({ token: 'synthetic-other', fetchImpl }) })
    if (change === 'permission') await wrapper.setProps({ enabled: false })
    if (change === 'key') await wrapper.get('input').setValue('synthetic-other-key')
    if (change === 'unmount') mounted.pop()?.unmount()
    finish(response(found))
    await flushPromises()
    expect(wrapper.emitted('found')).toBeUndefined()
    expect(wrapper.emitted('open-task')).toBeUndefined()
    if (change !== 'unmount') expect(wrapper.find('[data-testid="task-recovery-code"]').exists()).toBe(false)
    if (change === 'client' || change === 'permission') expect(wrapper.get('input').element.value).toBe('')
  })

  it('clears a previous result when a different key is entered', async () => {
    const { wrapper } = setup(async () => response(found))
    await find(wrapper)
    await wrapper.get('input').setValue('synthetic-another-key')
    expect(wrapper.find('[data-testid="task-recovery-open"]').exists()).toBe(false)
  })
})
