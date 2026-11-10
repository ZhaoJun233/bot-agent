import { clearPendingDecisions } from '../../src/features/tasks/pending-decisions'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import App from '../../src/App.vue'

const clients = new Map<string, Record<string, unknown>>()
vi.mock('../../src/api/zba', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../src/api/zba')>()
  class FakeZbaClient {
    constructor(options: { token?: string } = {}) {
      const token = options.token ?? ''
      return { baseUrl: '/zba/v1', identityLabel: 'id-' + token, ...(clients.get(token) ?? client()) } as never
    }
  }
  return { ...actual, ZbaClient: FakeZbaClient }
})

const approval = { id: 'synthetic-approval', toolId: 'synthetic.tool', summary: 'synthetic safe summary', session: 'synthetic-session', expiresAt: '2099-01-01T00:00:00Z', policyVersion: 1 }
function client(overrides: Record<string, unknown> = {}) {
  return {
    health: async () => ({ status: 'ok', configured: true, ready: true, contractVersion: 'zba-1.0' }),
    meta: async () => ({ contractVersion: 'zba-1.0', serverVersion: '1.0.0', authentication: 'bearer', scopes: ['read', 'admin'], sse: false, mcpManagement: false, dockerManagement: false, maxBodyBytes: 1048576, instanceId: 'synthetic-instance', features: ['sessions.list', 'sessions.ensure', 'tasks.result', 'tasks.find'] }),
    status: async () => ({ state: 'running', ready: true, version: '1.0.0', startedAt: '2026-10-24T00:00:00Z', updatedAt: '2026-10-24T00:00:00Z', configurationVersion: 'synthetic-v1', configurationApplyState: 'applied', components: [], recentErrors: [] }),
    checkAdmin: async () => true,
    listEvents: async (after: number) => ({ items: [], nextSequence: after, resyncRequired: false, generatedAt: '2026-10-24T00:00:00Z' }),
    listTasks: async () => ({ items: [] }),
    listApprovals: async () => [approval],
    ...overrides
  }
}
function deferred<T>() { let resolve!: (value: T) => void; const promise = new Promise<T>(r => { resolve = r }); return { promise, resolve } }
const mounted: Array<{ unmount(): void }> = []
afterEach(() => { for (const wrapper of mounted.splice(0)) wrapper.unmount(); clients.clear(); clearPendingDecisions() })

describe('Spec: same-document session recovery', () => {
  for (const ending of ['lock', 'auth-expired'] as const) {
    it(ending + ': must retain pending decision exclusion after same-identity reconnect', async () => {
      const gate = deferred<{ ok: boolean; decision: string; reasonCode: string }>()
      const decisions: boolean[] = []
      clients.set('synthetic-A', client({ decideApproval: async (_id: string, approve: boolean) => { decisions.push(approve); return gate.promise } }))
      const wrapper = mount(App, { attachTo: document.body }); mounted.push(wrapper)
      const vm = wrapper.vm as unknown as { connect(value: string): Promise<void>; enableAdmin(): Promise<void>; openTasks(): Promise<void>; lock(): Promise<void>; onAuthExpired(error: {code: string; message: string; status: number}): void }
      await vm.connect('synthetic-A'); await vm.enableAdmin(); await vm.openTasks(); await flushPromises()
      await wrapper.get('[data-testid=task-approve-0]').trigger('click'); await flushPromises()
      expect(decisions).toEqual([true])
      if (ending === 'lock') await vm.lock(); else vm.onAuthExpired({ code: 'unauthorized', message: 'synthetic expiry', status: 401 })
      await flushPromises()
      expect(wrapper.find('[data-testid=task-approve-0]').exists()).toBe(false)
      await vm.connect('synthetic-B'); await vm.enableAdmin(); await vm.openTasks(); await flushPromises()
      expect(wrapper.get('[data-testid=task-approve-0]').attributes('disabled')).toBeUndefined()
      await vm.lock(); await flushPromises()
      await vm.connect('synthetic-A'); await vm.enableAdmin(); await vm.openTasks(); await flushPromises()
      // Actual production App lock/unmount/reconnect; no document reload or module reset.
      const blocked = wrapper.get('[data-testid=task-approve-0]').attributes('disabled') !== undefined
      const markedUnknown = wrapper.find('[data-testid=task-approval-stale-0]').exists()
      await wrapper.get('[data-testid=task-reject-0]').trigger('click'); await flushPromises()
      const observed = { ending, blocked, markedUnknown, decisions: [...decisions] }
      // A manual read cannot rearm a POST that is still in flight.
      await wrapper.get('[data-testid=task-reload]').trigger('click'); await flushPromises()
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeDefined()
      gate.resolve({ ok: true, decision: 'approve', reasonCode: '' }); await flushPromises()
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeDefined()
      await wrapper.get('[data-testid=task-reload]').trigger('click'); await flushPromises()
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
      await wrapper.get('[data-testid=task-reject-0]').trigger('click'); await flushPromises()
      expect(decisions).toEqual([true, false])
      expect(observed).toEqual({ ending, blocked: true, markedUnknown: true, decisions: [true] })
    })
  }
})
