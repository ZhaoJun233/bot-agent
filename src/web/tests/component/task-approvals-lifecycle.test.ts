/**
 * Approval lifecycle characterization through the existing page. This single-responsibility
 * test collection keeps request ordering, shared synthetic fixtures and cleanup together
 * so ownership changes and decision recovery can be reviewed as complete timelines.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { markRaw } from 'vue'
import TasksPage from '../../src/features/tasks/TasksPage.vue'
import {
  ZbaNetworkError, ZbaRequestError,
  type ZbaApprovalDecisionResult, type ZbaApprovalSummary,
  type ZbaClient, type ZbaEvent, type ZbaEventPage, type ZbaTaskSummary
} from '../../src/api/zba'
import { clearPendingDecisions } from '../../src/features/tasks/pending-decisions'
import { resetAppResourceSignals } from '../../src/shared/sync/app-resources'
import { acquireScheduler } from '../../src/shared/sync/event-scheduler'

const mounted = new Set<{ unmount(): void }>()
const leases: Array<{ release(): void }> = []
const settle: Array<() => void> = []
const at = '2026-11-11T00:00:00Z'
const task: ZbaTaskSummary = {
  id: 'synthetic-lifecycle-task', state: 1, target: 'server', sessionId: 'synthetic-session',
  createdAt: at, updatedAt: at, cancellationRequested: false
}

function approval(overrides: Partial<ZbaApprovalSummary> = {}): ZbaApprovalSummary {
  return {
    id: 'synthetic-lifecycle-approval', toolId: 'synthetic.tool', summary: 'synthetic approval',
    session: 'synthetic-session', expiresAt: '2099-01-01T00:00:00Z', policyVersion: 1,
    ...overrides
  }
}

function events(after: number, items: ZbaEvent[] = []): ZbaEventPage {
  return { items, nextSequence: items.at(-1)?.sequence ?? after, resyncRequired: false, generatedAt: at }
}

function deferred<T>(fallback: T) {
  let resolve!: (value: T) => void
  let reject!: (cause: unknown) => void
  const promise = new Promise<T>((done, fail) => { resolve = done; reject = fail })
  settle.push(() => resolve(fallback))
  return { promise, resolve, reject }
}

function clientWith(overrides: Record<string, unknown> = {}, identity = 'synthetic-approval-lifecycle'): ZbaClient {
  // The network client is a synthetic fixture. Keep the actual page, registry and scheduler.
  // As in the existing component probes, markRaw keeps one object identity in the scheduler WeakMap.
  return markRaw({
    baseUrl: '/zba/approval-lifecycle', identityLabel: identity,
    listEvents: async (after: number) => events(after),
    listTasks: async () => ({ items: [task] }),
    listApprovals: async () => [approval()],
    decideApproval: async (_id: string, approve: boolean) => ({ ok: true, decision: approve ? 'approve' : 'reject', reasonCode: '' }),
    status: async () => ({ state: 'running', ready: true, configurationVersion: 'synthetic-v1' }),
    readConfiguration: async () => ({ generation: 'synthetic-generation', version: 'synthetic-v1' }),
    listCapabilities: async () => ({ descriptorRevision: 'synthetic-revision', items: [] }),
    ...overrides
  }) as unknown as ZbaClient
}

function mountPage(client: ZbaClient, props: Partial<{ active: boolean; mode: 'read' | 'admin'; writesEnabled: boolean }> = {}) {
  const wrapper = mount(TasksPage, {
    props: { client, active: true, mode: 'admin', writesEnabled: true, ...props }, attachTo: document.body
  })
  mounted.add(wrapper)
  return wrapper
}
type Page = ReturnType<typeof mountPage>

function unmountPage(wrapper: Page) {
  mounted.delete(wrapper)
  wrapper.unmount()
}

async function reload(wrapper: Page) {
  await wrapper.get('[data-testid=task-reload]').trigger('click')
  await flushPromises()
}

function blocked(wrapper: Page, index = 0) {
  expect(wrapper.get('[data-testid=task-approve-' + index + ']').attributes('disabled')).toBeDefined()
  expect(wrapper.get('[data-testid=task-reject-' + index + ']').attributes('disabled')).toBeDefined()
}

function unknown(wrapper: Page) {
  expect(wrapper.get('[data-testid=task-approval-stale-0]').text()).toBe('结果未知')
  blocked(wrapper)
}

function shared(client: ZbaClient) {
  const lease = acquireScheduler(client, { isHidden: () => false })
  leases.push(lease)
  return lease.scheduler
}

beforeEach(() => {
  clearPendingDecisions()
  resetAppResourceSignals()
  localStorage.clear()
  sessionStorage.clear()
  // Explicit pollOnce drives real event rounds. Automatic timer ticks cannot satisfy a held-request wait.
  // Date remains real, including the product's strict future-expiry check.
  vi.useFakeTimers({ toFake: ['setTimeout', 'clearTimeout'] })
})

afterEach(async () => {
  for (const wrapper of mounted) wrapper.unmount()
  mounted.clear()
  for (const lease of leases.splice(0)) lease.release()
  for (const finish of settle.splice(0)) finish()
  await flushPromises()
  clearPendingDecisions()
  resetAppResourceSignals()
  vi.useRealTimers()
  vi.restoreAllMocks()
})

describe('Approval lifecycle through the existing page', () => {
  for (const change of ['same client', 'same-label client', 'different identity'] as const) {
    for (const late of ['success', '401'] as const) {
      it('AP-01 latest approval GET wins over late ' + late + ' with ' + change, async () => {
        const held = deferred<ZbaApprovalSummary[]>([])
        const fresh = approval({ id: 'synthetic-new-approval', summary: 'synthetic current queue' })
        const obsolete = approval({ id: 'synthetic-old-approval', summary: 'synthetic obsolete queue' })
        const reads = vi.fn(async () => [fresh])
          .mockResolvedValueOnce([approval()]).mockImplementationOnce(() => held.promise)
        const client = clientWith({ listApprovals: reads })
        const wrapper = mountPage(client)
        await flushPromises()
        await reload(wrapper)
        expect(reads).toHaveBeenCalledTimes(2)
        const newReads = vi.fn(async () => [fresh])
        if (change === 'same client') await reload(wrapper)
        else {
          const identity = change === 'same-label client' ? 'synthetic-approval-lifecycle' : 'synthetic-approval-other'
          await wrapper.setProps({ client: clientWith({ listApprovals: newReads }, identity) })
          await flushPromises()
          expect(newReads).toHaveBeenCalledTimes(1)
        }
        expect(wrapper.text()).toContain(fresh.summary)
        if (late === 'success') held.resolve([obsolete])
        else held.reject(new ZbaRequestError(401, 'auth_required', 'synthetic authentication failure'))
        await flushPromises()
        expect(wrapper.text()).toContain(fresh.summary)
        expect(wrapper.text()).not.toContain(obsolete.summary)
        expect(wrapper.emitted('auth-expired')).toBeUndefined()
        expect(reads).toHaveBeenCalledTimes(change === 'same client' ? 3 : 2)
      })
    }
  }

  for (const late of ['success', '401'] as const) {
    it('AP-01 captures busy at manual GET start across same-label client replacement and late POST ' + late, async () => {
      const post = deferred<ZbaApprovalDecisionResult>({ ok: true, decision: 'approve', reasonCode: '' })
      const oldPosts = vi.fn(() => post.promise)
      const oldReads = vi.fn(async () => [approval()])
      const wrapper = mountPage(clientWith({ listApprovals: oldReads, decideApproval: oldPosts }))
      await flushPromises()
      await wrapper.get('[data-testid=task-approve-0]').trigger('click')
      await flushPromises()
      expect(oldPosts).toHaveBeenCalledTimes(1)
      const manual = deferred<ZbaApprovalSummary[]>([])
      const newReads = vi.fn(async () => [approval()])
        .mockResolvedValueOnce([approval()]).mockImplementationOnce(() => manual.promise)
      const newPosts = vi.fn(async () => ({ ok: true, decision: 'reject', reasonCode: '' }))
      await wrapper.setProps({ client: clientWith({ listApprovals: newReads, decideApproval: newPosts }) })
      await flushPromises()
      unknown(wrapper)
      await reload(wrapper)
      expect(newReads).toHaveBeenCalledTimes(2)
      if (late === 'success') post.resolve({ ok: true, decision: 'approve', reasonCode: '' })
      else post.reject(new ZbaRequestError(401, 'auth_required', 'synthetic old authentication failure'))
      await flushPromises()
      // No later GET exists: this held manual response is still the current one.
      expect(newReads).toHaveBeenCalledTimes(2)
      expect(oldReads).toHaveBeenCalledTimes(1)
      manual.resolve([approval()])
      await flushPromises()
      unknown(wrapper)
      expect(wrapper.emitted('auth-expired')).toBeUndefined()
      expect(oldPosts).toHaveBeenCalledTimes(1)
      expect(newPosts).not.toHaveBeenCalled()
      await reload(wrapper)
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
      expect(newPosts).not.toHaveBeenCalled()
      await wrapper.get('[data-testid=task-reject-0]').trigger('click')
      await flushPromises()
      expect(newPosts).toHaveBeenCalledTimes(1)
    })
  }

  it('AP-01 captures retry eligibility after a held tasks GET, not at the manual refresh click', async () => {
    const post = deferred<ZbaApprovalDecisionResult>({ ok: true, decision: 'approve', reasonCode: '' })
    const oldPosts = vi.fn(() => post.promise)
    const oldReads = vi.fn(async () => [approval()])
    const wrapper = mountPage(clientWith({ listApprovals: oldReads, decideApproval: oldPosts }))
    await flushPromises()
    await wrapper.get('[data-testid=task-approve-0]').trigger('click')
    await flushPromises()
    expect(oldPosts).toHaveBeenCalledTimes(1)
    const heldTasks = deferred({ items: [task] })
    const tasks = vi.fn(async () => ({ items: [task] }))
      .mockResolvedValueOnce({ items: [task] }).mockImplementationOnce(() => heldTasks.promise)
    const newReads = vi.fn(async () => [approval()])
    const newPosts = vi.fn(async () => ({ ok: true, decision: 'reject', reasonCode: '' }))
    await wrapper.setProps({ client: clientWith({ listTasks: tasks, listApprovals: newReads, decideApproval: newPosts }) })
    await flushPromises()
    unknown(wrapper)
    await reload(wrapper)
    expect(tasks).toHaveBeenCalledTimes(2)
    expect(newReads).toHaveBeenCalledTimes(1)
    post.resolve({ ok: true, decision: 'approve', reasonCode: '' })
    await flushPromises()
    unknown(wrapper)
    expect(oldReads).toHaveBeenCalledTimes(1)
    expect(newReads).toHaveBeenCalledTimes(1)
    expect(newPosts).not.toHaveBeenCalled()
    // The manual approval GET starts only now, after the old POST has settled.
    heldTasks.resolve({ items: [task] })
    await flushPromises()
    expect(newReads).toHaveBeenCalledTimes(2)
    expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
    expect(oldPosts).toHaveBeenCalledTimes(1)
    expect(newPosts).not.toHaveBeenCalled()
    await wrapper.get('[data-testid=task-reject-0]').trigger('click')
    await flushPromises()
    expect(newPosts).toHaveBeenCalledTimes(1)
    expect(newPosts).toHaveBeenLastCalledWith(approval().id, false)
  })

  it('AP-01 current approval GET 401 emits the safe authentication event', async () => {
    const reads = vi.fn(async (): Promise<ZbaApprovalSummary[]> => {
      throw new ZbaRequestError(401, 'auth_required', 'synthetic authentication failure')
    })
    const wrapper = mountPage(clientWith({ listApprovals: reads }))
    await flushPromises()
    expect(reads).toHaveBeenCalledTimes(1)
    const emitted = wrapper.emitted('auth-expired')
    expect(emitted).toHaveLength(1)
    expect(emitted?.[0]?.[0]).toMatchObject({ code: 'auth_required', message: expect.any(String) })
  })

  for (const outcome of ['408', '429', 'temporary ok=false'] as const) {
    it('AP-02 ' + outcome + ' requires a fresh manual Pending and a new click', async () => {
      const decisions = vi.fn(async () => ({ ok: true, decision: 'reject', reasonCode: '' }))
      if (outcome === 'temporary ok=false') {
        decisions.mockResolvedValueOnce({ ok: false, decision: '', reasonCode: 'policy_changed' })
      } else decisions.mockRejectedValueOnce(new ZbaRequestError(Number(outcome), 'decide_unavailable', 'synthetic transient failure'))
      const wrapper = mountPage(clientWith({ decideApproval: decisions }))
      await flushPromises()
      await wrapper.get('[data-testid=task-approve-0]').trigger('click')
      await flushPromises()
      expect(decisions).toHaveBeenCalledTimes(1)
      expect(wrapper.get('[data-testid=task-approval-stale-0]').text()).toBe(outcome === 'temporary ok=false' ? '未生效' : '结果未知')
      blocked(wrapper)
      await reload(wrapper)
      expect(decisions).toHaveBeenCalledTimes(1)
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
      await wrapper.get('[data-testid=task-reject-0]').trigger('click')
      await flushPromises()
      expect(decisions).toHaveBeenCalledTimes(2)
      expect(decisions).toHaveBeenLastCalledWith(approval().id, false)
      expect(wrapper.get('[data-testid=task-decision-notice]').text()).toContain('拒绝')
    })
  }

  it('AP-02 terminal ok=false stays blocked after automatic and manual reads of future Pending', async () => {
    const reads = vi.fn(async () => [approval()])
    const decisions = vi.fn(async () => ({ ok: false, decision: 'expired', reasonCode: 'approval_expired' }))
    const wrapper = mountPage(clientWith({ listApprovals: reads, decideApproval: decisions }))
    await flushPromises()
    await wrapper.get('[data-testid=task-approve-0]').trigger('click')
    await flushPromises()
    expect(reads).toHaveBeenCalledTimes(2)
    expect(decisions).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid=task-approval-stale-0]').text()).toBe('未生效')
    blocked(wrapper)
    expect(wrapper.find('[data-testid=task-decision-notice]').exists()).toBe(false)
    await reload(wrapper)
    expect(reads).toHaveBeenCalledTimes(3)
    expect(decisions).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid=task-approval-stale-0]').text()).toBe('未生效')
    blocked(wrapper)
    expect(wrapper.find('[data-testid=task-decision-notice]').exists()).toBe(false)
  })

  it('AP-02 current approval POST 401 emits auth-expired without replaying the decision', async () => {
    const decisions = vi.fn(async (): Promise<ZbaApprovalDecisionResult> => {
      throw new ZbaRequestError(401, 'auth_required', 'synthetic authentication failure')
    })
    const wrapper = mountPage(clientWith({ decideApproval: decisions }))
    await flushPromises()
    await wrapper.get('[data-testid=task-approve-0]').trigger('click')
    await flushPromises()
    expect(decisions).toHaveBeenCalledTimes(1)
    const emitted = wrapper.emitted('auth-expired')
    expect(emitted).toHaveLength(1)
    expect(emitted?.[0]?.[0]).toMatchObject({ code: 'auth_required', message: expect.any(String) })
  })

  for (const [label, initial, leave, back] of [
    ['active while already read-only', { mode: 'read' }, { active: false }, { active: true }],
    ['writesEnabled while already read-only', { mode: 'read' }, { writesEnabled: false }, { writesEnabled: true }],
    ['mode while writes already disabled', { writesEnabled: false }, { mode: 'read' }, { mode: 'admin' }]
  ] as const) {
    it('AP-03 independently invalidates ' + label + ' during a manual tasks GET', async () => {
      const held = deferred({ items: [task] })
      const tasks = vi.fn(async () => ({ items: [task] }))
        .mockResolvedValueOnce({ items: [task] }).mockImplementationOnce(() => held.promise)
      const reads = vi.fn(async () => [approval()])
      const decisions = vi.fn(async () => ({ ok: true, decision: 'approve', reasonCode: '' }))
      const wrapper = mountPage(clientWith({ listTasks: tasks, listApprovals: reads, decideApproval: decisions }), initial)
      await flushPromises()
      await reload(wrapper)
      expect(tasks).toHaveBeenCalledTimes(2)
      expect(reads).toHaveBeenCalledTimes(1)
      await wrapper.setProps(leave)
      await wrapper.setProps(back)
      held.resolve({ items: [task] })
      await flushPromises()
      expect(reads).toHaveBeenCalledTimes(1)
      expect(decisions).not.toHaveBeenCalled()
    })
  }

  for (const outcome of ['success', 'network failure'] as const) {
    it('AP-04 retains the guard when an unmounted POST settles with ' + outcome + ' before remount', async () => {
      const post = deferred<ZbaApprovalDecisionResult>({ ok: true, decision: 'approve', reasonCode: '' })
      const decisions = vi.fn(async () => ({ ok: true, decision: 'reject', reasonCode: '' }))
        .mockImplementationOnce(() => post.promise)
      const client = clientWith({ decideApproval: decisions })
      const old = mountPage(client)
      await flushPromises()
      await old.get('[data-testid=task-approve-0]').trigger('click')
      await flushPromises()
      expect(decisions).toHaveBeenCalledTimes(1)
      unmountPage(old)
      if (outcome === 'success') post.resolve({ ok: true, decision: 'approve', reasonCode: '' })
      else post.reject(new ZbaNetworkError('synthetic late network failure'))
      await flushPromises()
      const wrapper = mountPage(client)
      await flushPromises()
      unknown(wrapper)
      await reload(wrapper)
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
      expect(decisions).toHaveBeenCalledTimes(1)
      await wrapper.get('[data-testid=task-reject-0]').trigger('click')
      await flushPromises()
      expect(decisions).toHaveBeenCalledTimes(2)
    })
  }

  it('AP-04 a remounted manual GET begun before old POST failure cannot rearm that decision', async () => {
    const post = deferred<ZbaApprovalDecisionResult>({ ok: true, decision: 'approve', reasonCode: '' })
    const decisions = vi.fn(() => post.promise)
    const reads = vi.fn(async () => [approval()])
    const client = clientWith({ listApprovals: reads, decideApproval: decisions })
    const old = mountPage(client)
    await flushPromises()
    await old.get('[data-testid=task-approve-0]').trigger('click')
    await flushPromises()
    unmountPage(old)
    const wrapper = mountPage(client)
    await flushPromises()
    unknown(wrapper)
    const manual = deferred<ZbaApprovalSummary[]>([])
    reads.mockImplementationOnce(() => manual.promise)
    await reload(wrapper)
    expect(reads).toHaveBeenCalledTimes(3)
    post.reject(new ZbaNetworkError('synthetic old network failure'))
    await flushPromises()
    expect(reads).toHaveBeenCalledTimes(3)
    manual.resolve([approval()])
    await flushPromises()
    unknown(wrapper)
    expect(decisions).toHaveBeenCalledTimes(1)
    await reload(wrapper)
    expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
    expect(decisions).toHaveBeenCalledTimes(1)
  })

  it('AP-05 mount initiates tasks then approvals without awaiting tasks', async () => {
    const held = deferred({ items: [task] })
    const trace: string[] = []
    const wrapper = mountPage(clientWith({
      listTasks: () => { trace.push('tasks'); return held.promise },
      listApprovals: async () => { trace.push('approvals'); return [approval()] }
    }))
    await flushPromises()
    expect(trace).toEqual(['tasks', 'approvals'])
    expect(wrapper.text()).toContain(approval().summary)
    held.resolve({ items: [task] })
    await flushPromises()
  })

  it('AP-05 client reset initiates both GETs and stops the old manual continuation', async () => {
    const oldTask = deferred({ items: [task] })
    const oldTasks = vi.fn(async () => ({ items: [task] }))
    const oldReads = vi.fn(async () => [approval()])
    const wrapper = mountPage(clientWith({ listTasks: oldTasks, listApprovals: oldReads }))
    await flushPromises()
    oldTasks.mockImplementationOnce(() => oldTask.promise)
    await reload(wrapper)
    const newTask = deferred({ items: [task] })
    const trace: string[] = []
    const newReads = vi.fn(async () => { trace.push('approvals'); return [approval({ summary: 'synthetic reset queue' })] })
    await wrapper.setProps({ client: clientWith({
      listTasks: () => { trace.push('tasks'); return newTask.promise }, listApprovals: newReads
    }) })
    await flushPromises()
    expect(trace).toEqual(['tasks', 'approvals'])
    expect(wrapper.text()).toContain('synthetic reset queue')
    oldTask.resolve({ items: [task] })
    await flushPromises()
    expect(oldReads).toHaveBeenCalledTimes(1)
    expect(newReads).toHaveBeenCalledTimes(1)
    newTask.resolve({ items: [task] })
    await flushPromises()
  })

  it('AP-05 manual tasks failure still proceeds to the approval GET', async () => {
    let failTasks = false
    const trace: string[] = []
    const fresh = approval({ summary: 'synthetic queue after task failure' })
    const wrapper = mountPage(clientWith({
      listTasks: async () => {
        trace.push('tasks')
        if (failTasks) throw new ZbaRequestError(503, 'tasks_unavailable', 'synthetic task read failure')
        return { items: [task] }
      },
      listApprovals: async () => { trace.push('approvals'); return failTasks ? [fresh] : [] }
    }))
    await flushPromises()
    trace.length = 0
    failTasks = true
    await reload(wrapper)
    expect(trace).toEqual(['tasks', 'approvals'])
    expect(wrapper.text()).toContain(fresh.summary)
    expect(wrapper.text()).toContain('tasks_unavailable')
  })

  for (const order of ['event first', 'manual first'] as const) {
    it('AP-05 real approval events coexist with manual recovery and POST settlement: ' + order, async () => {
      const event: ZbaEvent = order === 'event first'
        ? { sequence: 41, type: 'approval.decided', at }
        : { sequence: 41, type: 'task.updated', taskId: task.id, state: 'waiting_approval', at }
      const eventReads = vi.fn(async (after: number) => events(after))
        .mockResolvedValueOnce(events(40)).mockResolvedValueOnce(events(40, [event]))
      const reads = vi.fn(async () => [approval()])
      const post = deferred<ZbaApprovalDecisionResult>({ ok: true, decision: 'approve', reasonCode: '' })
      const decisions = vi.fn(async () => ({ ok: true, decision: 'reject', reasonCode: '' }))
        .mockImplementationOnce(() => post.promise)
      const client = clientWith({ listEvents: eventReads, listApprovals: reads, decideApproval: decisions })
      const scheduler = shared(client)
      const wrapper = mountPage(client)
      await flushPromises()
      expect(await scheduler.pollOnce()).toBe(true)
      await flushPromises()
      expect(scheduler.snapshot()).toMatchObject({ cursor: 40, state: 'live' })
      await wrapper.get('[data-testid=task-approve-0]').trigger('click')
      await flushPromises()
      expect(decisions).toHaveBeenCalledTimes(1)
      if (order === 'event first') {
        const manual = deferred<ZbaApprovalSummary[]>([])
        reads.mockImplementationOnce(() => manual.promise)
        await reload(wrapper)
        const beforeEvent = reads.mock.calls.length
        expect(await scheduler.pollOnce()).toBe(true)
        await flushPromises()
        expect(reads).toHaveBeenCalledTimes(beforeEvent + 1)
        blocked(wrapper)
        post.reject(new ZbaNetworkError('synthetic uncertain decision'))
        await flushPromises()
        manual.resolve([approval()])
        await flushPromises()
        unknown(wrapper)
        expect(decisions).toHaveBeenCalledTimes(1)
        await reload(wrapper)
      } else {
        post.reject(new ZbaNetworkError('synthetic uncertain decision'))
        await flushPromises()
        unknown(wrapper)
        const background = deferred<ZbaApprovalSummary[]>([])
        reads.mockImplementationOnce(() => background.promise)
        const beforeEvent = reads.mock.calls.length
        const polling = scheduler.pollOnce()
        await flushPromises()
        expect(reads).toHaveBeenCalledTimes(beforeEvent + 1)
        await reload(wrapper)
        expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
        expect(decisions).toHaveBeenCalledTimes(1)
        background.resolve([approval()])
        expect(await polling).toBe(true)
        await flushPromises()
      }
      expect(scheduler.snapshot()).toMatchObject({ cursor: 41, state: 'live' })
      expect(wrapper.get('[data-testid=task-reject-0]').attributes('disabled')).toBeUndefined()
      expect(decisions).toHaveBeenCalledTimes(1)
      await wrapper.get('[data-testid=task-reject-0]').trigger('click')
      await flushPromises()
      expect(decisions).toHaveBeenCalledTimes(2)
      expect(decisions).toHaveBeenLastCalledWith(approval().id, false)
    })
  }

  for (const row of ['ordinary Pending', 'Unknown'] as const) {
    it('AP-05 real approval reread failure preserves ' + row + ' and keeps the event cursor stale', async () => {
      const event: ZbaEvent = { sequence: 41, type: 'approval.decided', at }
      const eventReads = vi.fn(async (after: number) => events(after))
        .mockResolvedValueOnce(events(40))
        .mockResolvedValueOnce(events(40, [event]))
        .mockResolvedValueOnce(events(40, [event]))
      const reads = vi.fn(async () => [approval()])
      const decisions = vi.fn(async (): Promise<ZbaApprovalDecisionResult> => {
        throw new ZbaNetworkError('synthetic uncertain decision')
      })
      const client = clientWith({ listEvents: eventReads, listApprovals: reads, decideApproval: decisions })
      const scheduler = shared(client)
      const wrapper = mountPage(client)
      await flushPromises()
      expect(await scheduler.pollOnce()).toBe(true)
      await flushPromises()
      if (row === 'Unknown') {
        await wrapper.get('[data-testid=task-approve-0]').trigger('click')
        await flushPromises()
        unknown(wrapper)
      }
      reads.mockRejectedValueOnce(new ZbaRequestError(503, 'approvals_unavailable', 'synthetic approval read failure'))
      expect(await scheduler.pollOnce()).toBe(false)
      await flushPromises()
      expect(scheduler.snapshot()).toMatchObject({ state: 'stale', cursor: 40, failedResource: 'approvals' })
      expect(wrapper.text()).toContain(approval().summary)
      expect(wrapper.text()).toContain('approvals_unavailable')
      expect(wrapper.text()).toContain('数据可能过期')
      if (row === 'Unknown') unknown(wrapper)
      else expect(wrapper.get('[data-testid=task-approve-0]').attributes('disabled')).toBeUndefined()
      expect(await scheduler.pollOnce()).toBe(true)
      await flushPromises()
      expect(eventReads).toHaveBeenNthCalledWith(2, 40, 100)
      expect(eventReads).toHaveBeenNthCalledWith(3, 40, 100)
      expect(scheduler.snapshot()).toMatchObject({ state: 'live', cursor: 41, failedResource: null })
      if (row === 'Unknown') unknown(wrapper)
      expect(decisions).toHaveBeenCalledTimes(row === 'Unknown' ? 1 : 0)
    })
  }

  it('AP-05 actual resync subscribers read tasks then approvals then shared resources', async () => {
    const trace: string[] = []
    const client = clientWith({
      listEvents: async () => events(40),
      listTasks: async () => { trace.push('tasks'); return { items: [task] } },
      listApprovals: async () => { trace.push('approvals'); return [approval()] },
      status: async () => { trace.push('status'); return { state: 'running', ready: true } },
      readConfiguration: async () => { trace.push('configuration'); return { generation: 'synthetic-generation', version: 'synthetic-v1' } },
      listCapabilities: async () => { trace.push('capabilities'); return { descriptorRevision: 'synthetic-revision', items: [] } }
    })
    const scheduler = shared(client)
    mountPage(client)
    await flushPromises()
    trace.length = 0
    expect(await scheduler.pollOnce()).toBe(true)
    expect(trace).toEqual(['tasks', 'approvals', 'status', 'configuration', 'capabilities'])
  })

  it('AP-05 unmount during the actual tasks subscriber prevents the captured approval GET', async () => {
    const event: ZbaEvent = { sequence: 41, type: 'task.updated', taskId: task.id, state: 'waiting_approval', at }
    const eventReads = vi.fn(async (after: number) => events(after))
      .mockResolvedValueOnce(events(40)).mockResolvedValueOnce(events(40, [event]))
    const tasks = vi.fn(async () => ({ items: [task] }))
    const reads = vi.fn(async () => [approval()])
    const client = clientWith({ listEvents: eventReads, listTasks: tasks, listApprovals: reads })
    // This lease keeps the event round alive after the page detaches its own subscriptions.
    const scheduler = shared(client)
    const wrapper = mountPage(client)
    await flushPromises()
    expect(await scheduler.pollOnce()).toBe(true)
    const held = deferred({ items: [task] })
    tasks.mockImplementationOnce(() => held.promise)
    const beforeTasks = tasks.mock.calls.length
    const beforeApprovals = reads.mock.calls.length
    const polling = scheduler.pollOnce()
    await flushPromises()
    expect(tasks).toHaveBeenCalledTimes(beforeTasks + 1)
    expect(reads).toHaveBeenCalledTimes(beforeApprovals)
    unmountPage(wrapper)
    expect(scheduler.subscriberCountNamed('approvals')).toBe(0)
    for (const name of ['tasks', 'status', 'configuration', 'capabilities']) {
      expect(scheduler.subscriberCountNamed(name)).toBe(0)
    }
    held.resolve({ items: [task] })
    await polling
    await flushPromises()
    expect(reads).toHaveBeenCalledTimes(beforeApprovals)
  })

  it('AP-06 maps the global pending-decision capacity and does not send a 33rd POST', async () => {
    const rows = Array.from({ length: 33 }, (_, index) => approval({ id: 'synthetic-capacity-' + index }))
    const posts = rows.slice(0, 32).map(() => deferred<ZbaApprovalDecisionResult>({ ok: true, decision: 'approve', reasonCode: '' }))
    let issued = 0
    const decisions = vi.fn((_id: string, _approve: boolean): Promise<ZbaApprovalDecisionResult> => {
      const post = posts[issued++]
      if (!post) throw new Error('Unexpected capacity POST')
      return post.promise
    })
    const wrapper = mountPage(clientWith({ listApprovals: async () => rows, decideApproval: decisions }))
    await flushPromises()
    for (let index = 0; index < 32; index += 1) {
      await wrapper.get('[data-testid=task-approve-' + index + ']').trigger('click')
    }
    await flushPromises()
    expect(decisions).toHaveBeenCalledTimes(32)
    await wrapper.get('[data-testid=task-approve-32]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('pending_decision_capacity')
    expect(decisions).toHaveBeenCalledTimes(32)
    for (let index = 0; index < 32; index += 1) blocked(wrapper, index)
  })
})
