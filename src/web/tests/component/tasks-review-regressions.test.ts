import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { markRaw } from 'vue'
import TasksPage from '../../src/features/tasks/TasksPage.vue'
import { ZbaNetworkError, ZbaRequestError, type ZbaClient, type ZbaTaskSummary } from '../../src/api/zba'
import { clearPending, pendingByKey } from '../../src/features/tasks/pending-intent'
import { acquireScheduler } from '../../src/shared/sync/event-scheduler'

// Original three desired assertions retained from the read-only review evidence.
const mounted: Array<{ unmount(): void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
  clearPending()
})
function deferred<T>() {
  let resolve!: (value: T) => void
  const promise = new Promise<T>((done) => { resolve = done })
  return { promise, resolve }
}
const task: ZbaTaskSummary = {
  id: 't-synthetic-1', state: 1, target: 'server', sessionId: 'synthetic-session',
  createdAt: '2026-10-30T12:00:00Z', updatedAt: '2026-10-30T12:00:00Z', cancellationRequested: false
}
const approval = {
  id: 'a-synthetic-1', toolId: 'synthetic.tool', summary: 'synthetic safe summary',
  session: 'synthetic-session', expiresAt: '2099-01-01T00:00:00Z', policyVersion: 1
}
function clientWith(overrides: Partial<Record<string, unknown>> = {}): ZbaClient {
  // Real ZbaClient identity must be stable across the scheduler WeakMap.
  // markRaw prevents Vue from giving this object-shaped stub a different proxy key.
  return markRaw({
    baseUrl: '/zba/tasks-review-regression', identityLabel: 'synthetic-tasks-review-regression',
    listEvents: async (after: number) => ({ items: [], nextSequence: after, resyncRequired: false, generatedAt: '2026-10-30T12:00:00Z' }),
    listTasks: async () => ({ items: [task] }),
    listApprovals: async () => [],
    ensureSession: async () => ({ session: { id: 'synthetic-session', name: 'synthetic', backend: 'server', current: true, turns: 0, historyChars: 0, createdAt: '2026-10-30T12:00:00Z', updatedAt: '2026-10-30T12:00:00Z' }, created: false }),
    ...overrides
  }) as unknown as ZbaClient
}
function mountPage(client: ZbaClient) {
  const wrapper = mount(TasksPage, { props: { client, mode: 'admin', writesEnabled: true }, attachTo: document.body })
  mounted.push(wrapper)
  return wrapper
}

describe('Tasks review counterexamples: desired behavior', () => {
  it('TASK-01: accepted old POST must not erase a newly edited prompt', async () => {
    const accepted = deferred<{ id: string; state: number; acceptedAt: string }>()
    const sent: string[] = []
    const wrapper = mountPage(clientWith({
      submitTask: (request: { prompt: string }) => { sent.push(request.prompt); return accepted.promise }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-source-key"]').setValue('panel:synthetic')
    await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic first task')
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    expect(sent).toEqual(['synthetic first task'])
    await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic second draft')
    accepted.resolve({ id: 't-synthetic-accepted', state: 0, acceptedAt: '2026-10-30T12:00:00Z' })
    await flushPromises()
    expect(wrapper.get('[data-testid="task-submission-state"]').text()).toContain('已受理')
    // Candidate actually yields empty string at TasksPage.vue:691.
    expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('synthetic second draft')
  })

  it('TASK-02: after ambiguous 503, explicit reread of still-pending approval must offer a fresh decision', async () => {
    let reads = 0
    const wrapper = mountPage(clientWith({
      listApprovals: async () => { reads += 1; return [approval] },
      decideApproval: async () => { throw new ZbaRequestError(503, 'approvals_unavailable', 'synthetic') }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    expect(reads).toBeGreaterThanOrEqual(2)
    expect(wrapper.text()).toContain('结果未知')
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(reads).toBeGreaterThanOrEqual(3)
    // Candidate leaves this disabled forever through staleIds, even with current pending evidence.
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeUndefined()
  })

  it('TASK-03: failed background reread during interactive reread must retain last successful tasks', async () => {
    const manual = deferred<{ items: ZbaTaskSummary[] }>()
    let reads = 0
    const client = clientWith({ listTasks: () => {
      reads += 1
      if (reads === 1) return Promise.resolve({ items: [task] })
      if (reads === 2) return manual.promise
      return Promise.reject(new ZbaRequestError(500, 'synthetic_failure', 'synthetic'))
    } })
    const wrapper = mountPage(client)
    await flushPromises()
    expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(1)
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(reads).toBe(2)
    const shared = acquireScheduler(client)
    try {
      // First poll performs the real subscription resync path, without wall-clock waits.
      expect(await shared.scheduler.pollOnce()).toBe(false)
      expect(reads).toBe(3)
      manual.resolve({ items: [task] })
      await flushPromises()
      // Candidate shows zero rows: later failed request snapshotted transient empty UI.
      expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(1)
    } finally { shared.release() }
  })
})

async function fillDraft(wrapper: ReturnType<typeof mountPage>) {
  await wrapper.get('[data-testid="task-source-key"]').setValue('panel:synthetic')
  await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic first task')
}
const acceptance = { id: 't-synthetic-accepted', state: 0, acceptedAt: '2026-10-30T12:00:00Z' }

describe('Task submission draft and ownership boundaries', () => {
  it('unchanged accepted draft is cleared', async () => {
    const wrapper = mountPage(clientWith({ submitTask: async () => acceptance }))
    await flushPromises()
    await fillDraft(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('')
  })

  it('editing away and back to the same value is still a new draft revision', async () => {
    const accepted = deferred<typeof acceptance>()
    const wrapper = mountPage(clientWith({ submitTask: () => accepted.promise }))
    await flushPromises()
    await fillDraft(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic second draft')
    await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic first task')
    accepted.resolve(acceptance)
    await flushPromises()
    expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('synthetic first task')
  })

  for (const field of ['source-key', 'session-id', 'timeout']) {
    it('changing ' + field + ' keeps the current draft after old acceptance', async () => {
      const accepted = deferred<typeof acceptance>()
      const wrapper = mountPage(clientWith({ submitTask: () => accepted.promise }))
      await flushPromises()
      await fillDraft(wrapper)
      await wrapper.get('[data-testid="task-submit"]').trigger('click')
      await flushPromises()
      await wrapper.get('[data-testid="task-' + field + '"]').setValue(field === 'timeout' ? 250 : 'synthetic-new-value')
      accepted.resolve(acceptance)
      await flushPromises()
      expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('synthetic first task')
    })
  }

  it('draft edits during ensure do not alter the frozen POST or session input', async () => {
    const ensure = deferred<{ session: { id: string }; created: boolean }>()
    const sent: Array<{ prompt: string; timeoutSeconds: number; sessionId: string; sourceKey: string }> = []
    const wrapper = mountPage(clientWith({
      ensureSession: () => ensure.promise,
      submitTask: async (request: typeof sent[number]) => { sent.push(request); return acceptance }
    }))
    await flushPromises()
    await fillDraft(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic second draft')
    await wrapper.get('[data-testid="task-session-id"]').setValue('synthetic-new-session')
    await wrapper.get('[data-testid="task-timeout"]').setValue(250)
    ensure.resolve({ session: { id: 'synthetic-original-session' }, created: false })
    await flushPromises()
    expect(sent).toHaveLength(1)
    expect(sent[0]).toMatchObject({ prompt: 'synthetic first task', timeoutSeconds: 120, sessionId: 'synthetic-original-session', sourceKey: 'panel:synthetic' })
    expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('synthetic second draft')
    expect((wrapper.get('[data-testid="task-session-id"]').element as HTMLInputElement).value).toBe('synthetic-new-session')
  })

  it('late old-client acceptance settles its registry without clearing or reloading the new view', async () => {
    const accepted = deferred<typeof acceptance>()
    const wrapper = mountPage(clientWith({ submitTask: () => accepted.promise }))
    await flushPromises()
    await fillDraft(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    const key = wrapper.get('[data-testid="task-idempotency-key"]').text()
    let reads = 0
    await wrapper.setProps({ client: clientWith({
      baseUrl: '/zba/tasks-new-owner', identityLabel: 'synthetic-new-owner',
      listTasks: async () => { reads += 1; return { items: [{ ...task, id: 't-new-owner' }] } }
    }) })
    await flushPromises()
    await wrapper.get('[data-testid="task-prompt"]').setValue('synthetic new owner draft')
    const before = reads
    accepted.resolve(acceptance)
    await flushPromises()
    expect(pendingByKey(key)?.phase).toBe('resolved')
    expect(pendingByKey(key)?.taskId).toBe(acceptance.id)
    expect(reads).toBe(before)
    expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('synthetic new owner draft')
    expect(wrapper.find('[data-testid="task-item-t-new-owner"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="task-submission"]').exists()).toBe(false)
  })

  it('leave and return cannot restore an old POST owner even with unchanged draft', async () => {
    const accepted = deferred<typeof acceptance>()
    let reads = 0
    const wrapper = mountPage(clientWith({
      submitTask: () => accepted.promise,
      listTasks: async () => { reads += 1; return { items: [task] } }
    }))
    await flushPromises()
    await fillDraft(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    const key = wrapper.get('[data-testid="task-idempotency-key"]').text()
    await wrapper.setProps({ active: false })
    await wrapper.setProps({ active: true })
    const before = reads
    accepted.resolve(acceptance)
    await flushPromises()
    expect(pendingByKey(key)?.phase).toBe('resolved')
    expect(reads).toBe(before)
    expect((wrapper.get('[data-testid="task-prompt"]').element as HTMLTextAreaElement).value).toBe('synthetic first task')
  })
})

describe('Approval unknown and terminal outcomes', () => {
  for (const [label, failure, unknown] of [
    ['network', new ZbaNetworkError('timeout'), true],
    ['503', new ZbaRequestError(503, 'approvals_unavailable', 'synthetic'), true],
    ['unreadable 200', new ZbaRequestError(200, 'invalid_response', 'synthetic'), true],
    ['temporary 403', new ZbaRequestError(403, 'approvals_disabled', 'synthetic'), false]
  ] as const) {
    it(label + ' requires successful manual fresh Pending and a new click, never automatic replay', async () => {
      let decisions = 0
      const wrapper = mountPage(clientWith({
        listApprovals: async () => [approval],
        decideApproval: async () => {
          decisions += 1
          if (decisions === 1) throw failure
          return { ok: true, decision: 'reject', reasonCode: '' }
        }
      }))
      await flushPromises()
      await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
      await flushPromises()
      expect(decisions).toBe(1)
      expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
      expect(wrapper.get('[data-testid="task-approval-stale-0"]').text()).toBe(unknown ? '结果未知' : '未生效')
      await wrapper.get('[data-testid="task-reload"]').trigger('click')
      await flushPromises()
      expect(decisions).toBe(1)
      expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeUndefined()
      await wrapper.get('[data-testid="task-reject-0"]').trigger('click')
      await flushPromises()
      expect(decisions).toBe(2)
      expect(wrapper.get('[data-testid="task-decision-notice"]').text()).toContain('拒绝')
    })
  }

  it('unknown approval absent from the fresh queue remains unknown, not failed', async () => {
    let decided = false
    const wrapper = mountPage(clientWith({
      listApprovals: async () => decided ? [] : [approval],
      decideApproval: async () => { decided = true; throw new ZbaNetworkError('timeout') }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    const row = wrapper.get('[data-testid="task-approval-0"]')
    expect(row.text()).toContain('结果未知')
    expect(row.text()).toContain('不在待审批队列')
    expect(row.text()).not.toContain('未生效')
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
  })

  it('failed manual approval reread never reenables an unknown old row', async () => {
    let reads = 0
    const wrapper = mountPage(clientWith({
      listApprovals: async () => { reads += 1; if (reads >= 3) throw new ZbaRequestError(503, 'synthetic_unavailable', 'synthetic'); return [approval] },
      decideApproval: async () => { throw new ZbaNetworkError('timeout') }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(reads).toBe(3)
    expect(wrapper.get('[data-testid="task-approval-stale-0"]').text()).toBe('结果未知')
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
  })

  for (const reason of ['expired', 'already_decided', 'source_retired']) {
    it('explicit ' + reason + ' remains terminal after manual reread', async () => {
      const wrapper = mountPage(clientWith({
        listApprovals: async () => [approval],
        decideApproval: async () => { throw new ZbaRequestError(409, 'approval_rejected', 'synthetic', reason) }
      }))
      await flushPromises()
      await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
      await flushPromises()
      await wrapper.get('[data-testid="task-reload"]').trigger('click')
      await flushPromises()
      expect(wrapper.get('[data-testid="task-approval-stale-0"]').text()).toBe('未生效')
      expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
    })
  }

  for (const expiresAt of ['2000-01-01T00:00:00Z', 'invalid']) {
    it('expired or malformed fresh Pending expiry cannot rearm an unknown decision: ' + expiresAt, async () => {
      const wrapper = mountPage(clientWith({
        listApprovals: async () => [{ ...approval, expiresAt }],
        decideApproval: async () => { throw new ZbaNetworkError('timeout') }
      }))
      await flushPromises()
      await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
      await flushPromises()
      await wrapper.get('[data-testid="task-reload"]').trigger('click')
      await flushPromises()
      expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
    })
  }
})

describe('Task last-good snapshot boundaries', () => {
  it('overlapping failure preserves paged last-good rows and the exact next cursor', async () => {
    const manual = deferred<{ items: ZbaTaskSummary[] }>()
    const cursors: Array<string | undefined> = []
    let firstReads = 0
    const client = clientWith({ listTasks: ({ cursor }: { cursor?: string }) => {
      cursors.push(cursor)
      if (cursor === 'synthetic-cursor-1') return Promise.resolve({ items: [{ ...task, id: 't-synthetic-2' }], nextCursor: 'synthetic-cursor-2' })
      if (cursor === 'synthetic-cursor-2') return Promise.resolve({ items: [{ ...task, id: 't-synthetic-3' }] })
      firstReads += 1
      if (firstReads === 1) return Promise.resolve({ items: [task], nextCursor: 'synthetic-cursor-1' })
      if (firstReads === 2) return manual.promise
      return Promise.reject(new ZbaRequestError(500, 'synthetic_failure', 'synthetic'))
    } })
    const wrapper = mountPage(client)
    await flushPromises()
    await wrapper.get('[data-testid="task-load-more"]').trigger('click')
    await flushPromises()
    expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(2)
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    const shared = acquireScheduler(client)
    try {
      expect(await shared.scheduler.pollOnce()).toBe(false)
      manual.resolve({ items: [{ ...task, id: 't-obsolete-response' }] })
      await flushPromises()
      expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(2)
      expect(wrapper.find('[data-testid="task-item-t-obsolete-response"]').exists()).toBe(false)
      expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
      await wrapper.get('[data-testid="task-load-more"]').trigger('click')
      await flushPromises()
      expect(cursors[cursors.length - 1]).toBe('synthetic-cursor-2')
      expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(3)
    } finally { shared.release() }
  })

  it('successful empty snapshot replaces old nonempty last-good data', async () => {
    let reads = 0
    const wrapper = mountPage(clientWith({ listTasks: async () => {
      reads += 1
      if (reads === 1) return { items: [task] }
      if (reads === 2) return { items: [] }
      throw new ZbaRequestError(500, 'synthetic_failure', 'synthetic')
    } }))
    await flushPromises()
    await wrapper.vm.reload()
    expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(0)
    await wrapper.vm.reload()
    await flushPromises()
    expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(0)
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
  })

  it('new client failures cannot restore old client last-good data', async () => {
    const wrapper = mountPage(clientWith())
    await flushPromises()
    expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(1)
    await wrapper.setProps({ client: clientWith({
      baseUrl: '/zba/tasks-isolated-owner', identityLabel: 'synthetic-isolated-owner',
      listTasks: async () => { throw new ZbaRequestError(503, 'synthetic_failure', 'synthetic') }
    }) })
    await flushPromises()
    await wrapper.vm.reload()
    await flushPromises()
    expect(wrapper.findAll('[data-testid="task-state"]')).toHaveLength(0)
    expect(wrapper.find('[data-testid="error-notice"]').exists()).toBe(true)
  })
})


describe('Approval read and decision ordering', () => {
  it('manual GET started before the unknown result cannot rearm the old decision', async () => {
    const decision = deferred<void>()
    const fresh = deferred<typeof approval[]>()
    let reads = 0
    let decisions = 0
    const wrapper = mountPage(clientWith({
      listApprovals: async () => { reads += 1; return reads === 2 ? fresh.promise : [approval] },
      decideApproval: async () => { decisions += 1; await decision.promise; throw new ZbaNetworkError('timeout') }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(reads).toBe(2)
    decision.resolve()
    await flushPromises()
    expect(reads).toBe(3)
    fresh.resolve([approval])
    await flushPromises()
    expect(decisions).toBe(1)
    expect(wrapper.get('[data-testid="task-approval-stale-0"]').text()).toBe('结果未知')
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(decisions).toBe(1)
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeUndefined()
  })

  it('old-client approval completion never reads or overwrites the new queue', async () => {
    const decision = deferred<{ ok: boolean; decision: string; reasonCode: string }>()
    const wrapper = mountPage(clientWith({ listApprovals: async () => [approval], decideApproval: () => decision.promise }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    let reads = 0
    await wrapper.setProps({ client: clientWith({
      baseUrl: '/zba/approval-new-owner', identityLabel: 'synthetic-approval-new-owner',
      listApprovals: async () => { reads += 1; return [{ ...approval, id: 'a-new-owner', toolId: 'synthetic.new-tool' }] }
    }) })
    await flushPromises()
    const before = reads
    decision.resolve({ ok: true, decision: 'approve', reasonCode: '' })
    await flushPromises()
    expect(reads).toBe(before)
    expect(wrapper.get('[data-testid="task-approval-0"]').text()).toContain('synthetic.new-tool')
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeUndefined()
    expect(wrapper.find('[data-testid="task-decision-notice"]').exists()).toBe(false)
  })

  it('mismatched receipt remains unknown until fresh Pending manual reread', async () => {
    let decisions = 0
    const wrapper = mountPage(clientWith({
      listApprovals: async () => [approval],
      decideApproval: async () => { decisions += 1; return { ok: true, decision: 'reject', reasonCode: '' } }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-approval-stale-0"]').text()).toBe('结果未知')
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
    expect(wrapper.find('[data-testid="task-decision-notice"]').exists()).toBe(false)
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(decisions).toBe(1)
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeUndefined()
  })
})
