// 单一职责：通过页面刻画任务结果读取的完整生命周期。竞态矩阵集中保留，
// 让请求完成顺序、身份切换和卸载的行为可以对照；不依赖模块内部状态。
import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TasksPage from '../../src/features/tasks/TasksPage.vue'
import { ZbaClient, ZbaRequestError, type ZbaTaskResult, type ZbaTaskSummary } from '../../src/api/zba'
import { clearPending } from '../../src/features/tasks/pending-intent'
import { clearPendingDecisions } from '../../src/features/tasks/pending-decisions'

const mounted = new Set<{ unmount: () => void }>()

function unmountPage(wrapper: { unmount: () => void }): void {
  wrapper.unmount()
  mounted.delete(wrapper)
}

afterEach(() => {
  for (const wrapper of mounted) unmountPage(wrapper)
  clearPending()
  clearPendingDecisions()
})

function task(id: string): ZbaTaskSummary {
  return {
    id, state: 1, target: 'server', sessionId: 'synthetic-session',
    createdAt: '2026-10-22T10:00:00Z', updatedAt: '2026-10-22T10:00:05Z',
    cancellationRequested: false
  }
}

function result(id: string, summary: string): ZbaTaskResult {
  return {
    id, state: 3, updatedAt: '2026-10-22T10:00:09Z', available: true,
    summary, summaryTruncated: false, summaryChars: summary.length
  }
}

function createClient(token = 'synthetic-result-token') {
  const fetchImpl = vi.fn(async () => { throw new Error('Unexpected network request') })
  const client = new ZbaClient({ baseUrl: '/zba/v1', token, fetchImpl })
  const tasks = [task('task-a'), task('task-b')]
  vi.spyOn(client, 'listEvents').mockImplementation(async (after = 0) => ({
    items: [], nextSequence: after, resyncRequired: false, generatedAt: '2026-10-22T10:00:00Z'
  }))
  vi.spyOn(client, 'listTasks').mockResolvedValue({ items: tasks, nextCursor: null })
  vi.spyOn(client, 'listApprovals').mockResolvedValue([])
  const find = vi.spyOn(client, 'findTask').mockResolvedValue({
    code: 'found', instanceId: 'synthetic-instance', task: task('task-a')
  })
  const read = vi.spyOn(client, 'getTaskResult').mockResolvedValue(result('task-a', '合成结果'))
  const submit = vi.spyOn(client, 'submitTask').mockRejectedValue(new Error('Unexpected submission'))
  const cancel = vi.spyOn(client, 'cancelTask').mockRejectedValue(new Error('Unexpected cancellation'))
  const decide = vi.spyOn(client, 'decideApproval').mockRejectedValue(new Error('Unexpected decision'))
  const ensure = vi.spyOn(client, 'ensureSession').mockRejectedValue(new Error('Unexpected session creation'))
  return { client, read, find, submit, cancel, decide, ensure, fetchImpl }
}

function mountPage(client: ZbaClient) {
  const wrapper = mount(TasksPage, {
    props: { client, mode: 'admin', writesEnabled: true, active: true },
    attachTo: document.body
  })
  mounted.add(wrapper)
  return wrapper
}

type Page = ReturnType<typeof mountPage>

function deferred<T>() {
  let resolve!: (value: T) => void
  let reject!: (reason: unknown) => void
  const promise = new Promise<T>((yes, no) => { resolve = yes; reject = no })
  return { promise, resolve, reject }
}

const oldOutcomes = ['success', 'error', 'unauthorized'] as const
type Outcome = typeof oldOutcomes[number]

function finish(read: ReturnType<typeof deferred<ZbaTaskResult | null>>, outcome: Outcome): void {
  if (outcome === 'success') read.resolve(result('task-a', '旧请求结果'))
  else if (outcome === 'unauthorized') read.reject(new ZbaRequestError(401, 'unauthorized', 'synthetic'))
  else read.reject(new ZbaRequestError(503, 'synthetic_unavailable', 'synthetic'))
}

function expectNoDetail(wrapper: Page): void {
  expect(wrapper.find('[data-testid="task-detail"]').exists()).toBe(false)
  expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
}

function expectBusy(wrapper: Page, busy: boolean): void {
  const disabled = wrapper.get('[data-testid="task-cancel-task-a"]').attributes('disabled')
  if (busy) expect(disabled).toBeDefined()
  else expect(disabled).toBeUndefined()
}

function expectCurrentResult(wrapper: Page): void {
  expect(wrapper.get('[data-testid="task-result-summary"]').text()).toBe('当前请求结果')
  expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('任务 task-b')
  expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
  expectBusy(wrapper, false)
}

describe('Task result reading through TasksPage', () => {
  for (const outcome of oldOutcomes) {
    for (const oldFirst of [true, false]) {
      it('TR-01 latest click wins over old ' + outcome + ', old finishes first=' + oldFirst, async () => {
        const api = createClient()
        const old = deferred<ZbaTaskResult | null>()
        const current = deferred<ZbaTaskResult | null>()
        api.read.mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise)
        const wrapper = mountPage(api.client)
        await flushPromises()
        await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
        await wrapper.get('[data-testid="task-result-task-b"]').trigger('click')
        expect(api.read.mock.calls).toEqual([['task-a'], ['task-b']])
        expectNoDetail(wrapper)
        expectBusy(wrapper, true)
        if (oldFirst) {
          finish(old, outcome)
          await flushPromises()
          expectNoDetail(wrapper)
          expectBusy(wrapper, true)
          await wrapper.get('[data-testid="task-cancel-task-a"]').trigger('click')
          expect(api.cancel).not.toHaveBeenCalled()
        }
        current.resolve(result('task-b', '当前请求结果'))
        await flushPromises()
        expectCurrentResult(wrapper)
        if (!oldFirst) {
          finish(old, outcome)
          await flushPromises()
          expectCurrentResult(wrapper)
        }
      })
    }
  }

  for (const displayed of ['success', 'error'] as const) {
    it('TR-01 a new read clears an already displayed ' + displayed + ' before completing', async () => {
      const api = createClient()
      if (displayed === 'error') api.read.mockRejectedValueOnce(new ZbaRequestError(503, 'previous_error', 'synthetic'))
      const wrapper = mountPage(api.client)
      await flushPromises()
      await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
      await flushPromises()
      expect(wrapper.get('[data-testid="task-detail"]').text()).toContain(displayed === 'success' ? '合成结果' : 'previous_error')
      const current = deferred<ZbaTaskResult | null>()
      api.read.mockReturnValueOnce(current.promise)
      await wrapper.get('[data-testid="task-result-task-b"]').trigger('click')
      expectNoDetail(wrapper)
      expectBusy(wrapper, true)
      current.resolve(result('task-b', '当前请求结果'))
      await flushPromises()
      expectCurrentResult(wrapper)
    })
  }

  for (const sameIdentity of [true, false]) {
    for (const displayed of ['success', 'error'] as const) {
      it('TR-02 client replacement clears displayed ' + displayed + ', same identity=' + sameIdentity, async () => {
        const previous = createClient()
        const next = createClient(sameIdentity ? 'synthetic-result-token' : 'synthetic-other-token')
        expect(previous.client === next.client).toBe(false)
        expect(previous.client.identityLabel === next.client.identityLabel).toBe(sameIdentity)
        if (displayed === 'error') previous.read.mockRejectedValueOnce(new ZbaRequestError(503, 'previous_error', 'synthetic'))
        const wrapper = mountPage(previous.client)
        await flushPromises()
        await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
        await flushPromises()
        expect(wrapper.get('[data-testid="task-detail"]').text()).toContain(displayed === 'success' ? '合成结果' : 'previous_error')
        await wrapper.setProps({ client: next.client })
        await flushPromises()
        expectNoDetail(wrapper)
        expectBusy(wrapper, false)
        expect(next.read).not.toHaveBeenCalled()
      })
    }
  }

  for (const sameIdentity of [true, false]) {
    for (const outcome of oldOutcomes) {
      for (const oldFirst of [true, false]) {
        it('TR-02 client replacement rejects old ' + outcome + ', same identity=' + sameIdentity
          + ', old finishes first=' + oldFirst, async () => {
          const previous = createClient()
          const next = createClient(sameIdentity ? 'synthetic-result-token' : 'synthetic-other-token')
          expect(previous.client === next.client).toBe(false)
          expect(previous.client.identityLabel === next.client.identityLabel).toBe(sameIdentity)
          const old = deferred<ZbaTaskResult | null>()
          const current = deferred<ZbaTaskResult | null>()
          previous.read.mockReturnValueOnce(old.promise)
          next.read.mockReturnValueOnce(current.promise)
          const wrapper = mountPage(previous.client)
          await flushPromises()
          await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
          await wrapper.setProps({ client: next.client })
          await flushPromises()
          expectNoDetail(wrapper)
          expectBusy(wrapper, false)
          await wrapper.get('[data-testid="task-result-task-b"]').trigger('click')
          expect(next.read).toHaveBeenCalledExactlyOnceWith('task-b')
          if (oldFirst) {
            finish(old, outcome)
            await flushPromises()
            expectNoDetail(wrapper)
            expectBusy(wrapper, true)
          }
          current.resolve(result('task-b', '当前请求结果'))
          await flushPromises()
          expectCurrentResult(wrapper)
          if (!oldFirst) {
            finish(old, outcome)
            await flushPromises()
            expectCurrentResult(wrapper)
          }
        })
      }
    }
  }

  for (const outcome of oldOutcomes) {
    it('TR-03 unmount rejects late ' + outcome + ' while a new mount reads', async () => {
      const api = createClient()
      const old = deferred<ZbaTaskResult | null>()
      const current = deferred<ZbaTaskResult | null>()
      api.read.mockReturnValueOnce(old.promise).mockReturnValueOnce(current.promise)
      const previous = mountPage(api.client)
      await flushPromises()
      await previous.get('[data-testid="task-result-task-a"]').trigger('click')
      unmountPage(previous)
      const wrapper = mountPage(api.client)
      await flushPromises()
      expectNoDetail(wrapper)
      expectBusy(wrapper, false)
      await wrapper.get('[data-testid="task-result-task-b"]').trigger('click')
      finish(old, outcome)
      await flushPromises()
      expect(previous.emitted('auth-expired') ?? []).toHaveLength(0)
      expectNoDetail(wrapper)
      expectBusy(wrapper, true)
      current.resolve(result('task-b', '当前请求结果'))
      await flushPromises()
      expectCurrentResult(wrapper)
    })
  }

  const readPreservingChanges = [
    { name: 'inactive', props: { active: false } },
    { name: 'read mode', props: { mode: 'read' as const } },
    { name: 'writes disabled', props: { writesEnabled: false } }
  ]
  for (const change of readPreservingChanges) {
    for (const outcome of oldOutcomes) {
      it('TR-04 ' + change.name + ' preserves the current read ' + outcome, async () => {
        const api = createClient()
        const current = deferred<ZbaTaskResult | null>()
        api.read.mockReturnValueOnce(current.promise)
        const wrapper = mountPage(api.client)
        await flushPromises()
        await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
        await wrapper.setProps(change.props)
        finish(current, outcome)
        await flushPromises()
        if (outcome === 'success') {
          expect(wrapper.get('[data-testid="task-result-summary"]').text()).toBe('旧请求结果')
          expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
        } else if (outcome === 'error') {
          expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('synthetic_unavailable')
          expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
        } else {
          expect(wrapper.find('[data-testid="task-detail"]').exists()).toBe(false)
          expect(wrapper.emitted('auth-expired')).toEqual([[
            expect.objectContaining({ code: 'unauthorized', status: 401 })
          ]])
        }
        expect(api.read).toHaveBeenCalledExactlyOnceWith('task-a')
      })
    }
  }

  it('TR-05 a current success displays its result without changing the list state', async () => {
    const api = createClient()
    const wrapper = mountPage(api.client)
    await flushPromises()
    await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-result-summary"]').text()).toBe('合成结果')
    expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('已完成')
    expect(wrapper.get('[data-testid="task-item-task-a"] [data-testid="task-state"]').text()).toBe('执行中')
    expectBusy(wrapper, false)
    expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
  })

  it('TR-05 a current ordinary error is displayed only in the result detail', async () => {
    const api = createClient()
    api.read.mockRejectedValueOnce(new ZbaRequestError(503, 'synthetic_unavailable', 'synthetic'))
    const wrapper = mountPage(api.client)
    await flushPromises()
    await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('ZBA 服务暂不可用。')
    expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('synthetic_unavailable')
    expect(wrapper.findAll('[data-testid="error-notice"]')).toHaveLength(1)
    expect(wrapper.get('[data-testid="task-item-task-a"] [data-testid="task-state"]').text()).toBe('执行中')
    expectBusy(wrapper, false)
    expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
  })

  it('TR-05 a current 401 emits auth-expired instead of a result error', async () => {
    const api = createClient()
    api.read.mockRejectedValueOnce(new ZbaRequestError(401, 'unauthorized', 'synthetic'))
    const wrapper = mountPage(api.client)
    await flushPromises()
    await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="task-detail"]').exists()).toBe(false)
    expect(wrapper.emitted('auth-expired')).toEqual([[
      expect.objectContaining({ code: 'unauthorized', status: 401, message: '令牌无效或已过期，请重新输入。' })
    ]])
    expectBusy(wrapper, false)
  })

  it('TR-05 null from the client displays task_not_found', async () => {
    const api = createClient()
    api.read.mockResolvedValueOnce(null)
    const wrapper = mountPage(api.client)
    await flushPromises()
    await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('任务不存在或已被清理。')
    expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('task_not_found')
    expect(wrapper.find('[data-testid="task-result-summary"]').exists()).toBe(false)
    expect(wrapper.emitted('auth-expired') ?? []).toHaveLength(0)
    expectBusy(wrapper, false)
  })

  it('TR-05 an unavailable result shows its reason without fabricating a summary', async () => {
    const api = createClient()
    api.read.mockResolvedValueOnce({
      id: 'task-a', state: 3, updatedAt: '2026-10-22T10:00:09Z', available: false,
      reasonCode: 'no_summary', summaryTruncated: false, summaryChars: 0
    })
    const wrapper = mountPage(api.client)
    await flushPromises()
    await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-result-unavailable"]').text()).toContain('no_summary')
    expect(wrapper.get('[data-testid="task-detail"]').text()).toContain('已完成')
    expect(wrapper.find('[data-testid="task-result-summary"]').exists()).toBe(false)
    expect(wrapper.get('[data-testid="task-item-task-a"] [data-testid="task-state"]').text()).toBe('执行中')
    expectBusy(wrapper, false)
  })

  for (const firstOutcome of ['success', 'error'] as const) {
    it('TR-06 closes a displayed ' + firstOutcome + ' and allows another read', async () => {
      const api = createClient()
      if (firstOutcome === 'error') api.read.mockRejectedValueOnce(new ZbaRequestError(503, 'old_error', 'synthetic'))
      const wrapper = mountPage(api.client)
      await flushPromises()
      await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
      await flushPromises()
      expect(wrapper.get('[data-testid="task-detail"]').text()).toContain(firstOutcome === 'success' ? '合成结果' : 'old_error')
      await wrapper.get('[data-testid="task-detail-close"]').trigger('click')
      expectNoDetail(wrapper)
      expectBusy(wrapper, false)
      const next = deferred<ZbaTaskResult | null>()
      api.read.mockReturnValueOnce(next.promise)
      await wrapper.get('[data-testid="task-result-task-b"]').trigger('click')
      expectNoDetail(wrapper)
      expectBusy(wrapper, true)
      next.resolve(result('task-b', '当前请求结果'))
      await flushPromises()
      expectCurrentResult(wrapper)
      expect(api.read.mock.calls).toEqual([['task-a'], ['task-b']])
    })
  }

  for (const entry of ['task row', 'recovery lookup'] as const) {
    it('TR-07 opens through ' + entry + ' without submitting, cancelling or deciding', async () => {
      const api = createClient()
      const wrapper = mountPage(api.client)
      await flushPromises()
      if (entry === 'recovery lookup') {
        await wrapper.get('[data-testid="task-recovery-key"]').setValue('synthetic-original-key')
        await wrapper.get('[data-testid="task-recovery"] form').trigger('submit')
        await flushPromises()
        expect(api.find).toHaveBeenCalledExactlyOnceWith('synthetic-original-key')
        await wrapper.get('[data-testid="task-recovery-open"]').trigger('click')
      } else {
        await wrapper.get('[data-testid="task-result-task-a"]').trigger('click')
      }
      await flushPromises()
      expect(api.read).toHaveBeenCalledExactlyOnceWith('task-a')
      expect(wrapper.get('[data-testid="task-result-summary"]').text()).toBe('合成结果')
      expect(wrapper.get('[data-testid="task-item-task-a"] [data-testid="task-state"]').text()).toBe('执行中')
      expect(api.submit).not.toHaveBeenCalled()
      expect(api.cancel).not.toHaveBeenCalled()
      expect(api.decide).not.toHaveBeenCalled()
      expect(api.ensure).not.toHaveBeenCalled()
      expect(api.fetchImpl).not.toHaveBeenCalled()
    })
  }
})
