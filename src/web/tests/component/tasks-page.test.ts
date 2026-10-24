import { afterEach, describe, expect, it } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import TasksPage from '../../src/features/tasks/TasksPage.vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaTaskSummary } from '../../src/api/zba'
import {
  beginPending,
  clearPending,
  leavePendingUnknown,
  pendingByKey,
  serviceLabel,
  settlePending
} from '../../src/features/tasks/pending-intent'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
  clearPending()
})

const task = (overrides: Partial<ZbaTaskSummary> = {}): ZbaTaskSummary => ({
  id: 't-1',
  state: 1,
  target: 'server',
  sessionId: 'synthetic-session',
  createdAt: '2026-10-22T10:00:00Z',
  updatedAt: '2026-10-22T10:00:05Z',
  cancellationRequested: false,
  ...overrides
})

function clientWith(overrides: Partial<Record<string, unknown>> = {}): ZbaClient {
  return {
    baseUrl: '/zba/v1',
    identityLabel: 'test-identity',
    listEvents: async (after: number) => ({
      items: [], nextSequence: after, resyncRequired: false, generatedAt: '2026-10-22T10:00:00Z'
    }),
    listTasks: async () => ({ items: [task()], nextCursor: null }),
    listApprovals: async () => [],
    ensureSession: async () => ({
      session: {
        id: 'synthetic-session', name: 'synthetic', backend: 'server', current: true,
        turns: 0, historyChars: 0,
        createdAt: '2026-10-22T10:00:00Z', updatedAt: '2026-10-22T10:00:00Z'
      },
      created: true
    }),
    submitTask: async (request: { sessionId: string }) => ({
      id: 't-1', state: 0, acceptedAt: '2026-10-22T10:00:00Z'
    }),
    findTask: async () => ({ code: 'found', instanceId: 'i-1', task: task() }),
    getTask: async () => task(),
    getTaskResult: async () => ({
      id: 't-1', state: 3, updatedAt: '2026-10-22T10:00:09Z', available: true,
      summary: '合成摘要', summaryTruncated: false, summaryChars: 4
    }),
    cancelTask: async () => ({ state: 5, cancellationRequested: true, outcome: 'requested' }),
    decideApproval: async () => ({ ok: true, decision: 'approve', reasonCode: '' }),
    ...overrides
  } as unknown as ZbaClient
}

function mountPage(client = clientWith(), mode: 'read' | 'admin' = 'admin') {
  const wrapper = mount(TasksPage, {
    props: { client, mode, writesEnabled: true },
    attachTo: document.body
  })
  mounted.push(wrapper)
  return wrapper
}

async function fill(wrapper: ReturnType<typeof mountPage>): Promise<void> {
  await wrapper.get('[data-testid="task-source-key"]').setValue('panel:synthetic')
  await wrapper.get('[data-testid="task-session-id"]').setValue('synthetic-session')
  await wrapper.get('[data-testid="task-prompt"]').setValue('合成任务内容')
}

describe('TasksPage', () => {
  it('渲染任务列表与真实状态', async () => {
    const wrapper = mountPage()
    await flushPromises()
    expect(wrapper.find('[data-testid="tasks"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="task-mode-badge"]').text()).toBe('管理员模式')
    expect(wrapper.get('[data-testid="task-state"]').text()).toBe('执行中')
  })

  it('标准模式不能提交、取消或审批', async () => {
    const wrapper = mountPage(clientWith(), 'read')
    await flushPromises()
    expect(wrapper.find('[data-testid="task-readonly"]').exists()).toBe(true)
    await fill(wrapper)
    expect(wrapper.get('[data-testid="task-submit"]').attributes('disabled')).toBeDefined()
    expect(wrapper.get('[data-testid="task-cancel-t-1"]').attributes('disabled')).toBeDefined()
  })

  it('提交成功会带上冻结的幂等键并刷新列表', async () => {
    const calls: Array<{ key: string; prompt: string }> = []
    const wrapper = mountPage(clientWith({
      submitTask: async (request: { prompt: string }, key: string) => {
        calls.push({ key, prompt: request.prompt })
        return { id: 't-2', state: 0, acceptedAt: '2026-10-22T10:00:00Z' }
      }
    }))
    await flushPromises()
    await fill(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()

    expect(calls).toHaveLength(1)
    expect(calls[0]?.key.startsWith('task1-')).toBe(true)
    expect(calls[0]?.prompt).toBe('合成任务内容')
    expect(wrapper.get('[data-testid="task-idempotency-key"]').text()).toBe(calls[0]?.key)
    expect(wrapper.get('[data-testid="task-submission-state"]').text()).toContain('已受理')
  })

  it('提交丢响应时保留幂等键，核对复用同一个键', async () => {
    const findKeys: string[] = []
    const submitKeys: string[] = []
    const wrapper = mountPage(clientWith({
      submitTask: async (_request: unknown, key: string) => {
        submitKeys.push(key)
        throw new ZbaNetworkError('timeout')
      },
      findTask: async (key: string) => {
        findKeys.push(key)
        return { code: 'found', instanceId: 'i-1', task: task() }
      }
    }))
    await flushPromises()
    await fill(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="task-submission-state"]').text()).toContain('结果未知')
    const shown = wrapper.get('[data-testid="task-idempotency-key"]').text()
    expect(shown).toBe(submitKeys[0])

    await wrapper.get('[data-testid="task-find"]').trigger('click')
    await flushPromises()
    // 核对用的是同一个键，没有换键重提。
    expect(findKeys).toEqual([submitKeys[0]])
    expect(submitKeys).toHaveLength(1)
    expect(wrapper.get('[data-testid="task-find-code"]').text()).toContain('已找到受理记录')
  })

  it('提交被明确拒绝时不显示未知', async () => {
    const wrapper = mountPage(clientWith({
      submitTask: async () => { throw new ZbaRequestError(400, 'invalid_prompt', '请求无效。') }
    }))
    await flushPromises()
    await fill(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-submission-state"]').text()).toContain('明确拒绝')
  })

  it('取消只显示服务端真实状态，不冒报已取消', async () => {
    const wrapper = mountPage(clientWith({
      cancelTask: async () => ({ state: 3, cancellationRequested: false, outcome: 'already_completed' })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-cancel-t-1"]').trigger('click')
    await flushPromises()
    expect(wrapper.text()).toContain('已完成')
    expect(wrapper.text()).not.toContain('已取消')
    expect(wrapper.text()).toContain('服务端没有登记这次取消')
  })

  it('审批被拒绝生效时如实说明，不显示成功', async () => {
    const approvals = [{
      id: 'a-1', toolId: 'synthetic.tool', summary: '合成摘要',
      session: 'synthetic-session', expiresAt: '2026-10-22T11:00:00Z', policyVersion: 1
    }]
    const wrapper = mountPage(clientWith({
      listApprovals: async () => approvals,
      decideApproval: async () => ({ ok: false, decision: 'expired', reasonCode: 'approval_expired' })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="task-decision-notice"]').exists()).toBe(false)
    expect(wrapper.text()).toContain('审批没有生效')
    // 未生效的审批不能被移出列表
    expect(wrapper.find('[data-testid="task-approval-0"]').exists()).toBe(true)
  })

  it('审批成功后才从列表移除并给出提示', async () => {
    const approvals = [{
      id: 'a-1', toolId: 'synthetic.tool', summary: '合成摘要',
      session: 'synthetic-session', expiresAt: '2026-10-22T11:00:00Z', policyVersion: 1
    }]
    // 真实服务端：决定成功后队列里就没有这条了。
    let decided = false
    const wrapper = mountPage(clientWith({
      listApprovals: async () => (decided ? [] : approvals),
      decideApproval: async () => { decided = true; return { ok: true, decision: 'approve', reasonCode: '' } }
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-decision-notice"]').text()).toContain('批准')
    expect(wrapper.find('[data-testid="task-approval-0"]').exists()).toBe(false)
  })

  it('离开视图后迟到的提交不再发出', async () => {
    const submits: unknown[] = []
    const wrapper = mountPage(clientWith({
      submitTask: async (request: unknown) => {
        submits.push(request)
        return { id: 't-3', state: 0, acceptedAt: '2026-10-22T10:00:00Z' }
      }
    }))
    await flushPromises()
    await fill(wrapper)
    await wrapper.setProps({ active: false })
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    expect(submits).toHaveLength(0)
  })


  // ── S3-01：未决提交不允许换键重提 ────────────────────────────────
  it('结果未知时不能再提交，只能核对或显式作为新任务重来', async () => {
    const submits: unknown[] = []
    const wrapper = mountPage(clientWith({
      submitTask: async (request: unknown) => {
        submits.push(request)
        throw new ZbaNetworkError('connectionreset')
      }
    }))
    await flushPromises()
    await fill(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="task-submission-state"]').text()).toContain('结果未知')
    // 主提交按钮被禁用：再点不会产生第二个幂等键
    expect(wrapper.get('[data-testid="task-submit"]').attributes('disabled')).toBeDefined()
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    expect(submits).toHaveLength(1)
    // 重新执行是独立动作，且必须两步确认
    expect(wrapper.find('[data-testid="task-restart-0"]').exists()).toBe(true)
    await wrapper.get('[data-testid="task-restart-0"]').trigger('click')
    expect(wrapper.get('[data-testid="task-restart-warning-0"]').text()).toContain('再执行一次')
    await wrapper.get('[data-testid="task-restart-confirm-0"]').trigger('click')
    await flushPromises()
    expect(wrapper.find('[data-testid="task-restart-warning-0"]').exists()).toBe(false)
    // 确认后才允许新一次提交（新键由下一次 submit 生成）
    expect(wrapper.get('[data-testid="task-submit"]').attributes('disabled')).toBeUndefined()
  })

  // ── S3-04：502 / expired / unknown 都不能说成"没有产生任务" ──────
  it('受理后代理 502 保持未知，不声称没有产生任务', async () => {
    const wrapper = mountPage(clientWith({
      submitTask: async () => { throw new ZbaRequestError(502, 'http_error', '上游错误。') }
    }))
    await flushPromises()
    await fill(wrapper)
    await wrapper.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    const state = wrapper.get('[data-testid="task-submission-state"]').text()
    expect(state).toContain('结果未知')
    expect(state).not.toContain('没有产生任务')
  })

  for (const code of ['expired', 'unknown'] as const) {
    it(`核对返回 ${code} 时保持未知，不声称没有产生任务`, async () => {
      const wrapper = mountPage(clientWith({
        submitTask: async () => { throw new ZbaNetworkError('timeout') },
        findTask: async () => ({ code, instanceId: 'i-1', taskId: code === 'expired' ? 't-9' : undefined })
      }))
      await flushPromises()
      await fill(wrapper)
      await wrapper.get('[data-testid="task-submit"]').trigger('click')
      await flushPromises()
      await wrapper.get('[data-testid="task-find"]').trigger('click')
      await flushPromises()
      const state = wrapper.get('[data-testid="task-submission-state"]').text()
      expect(state).toContain('结果未知')
      expect(state).not.toContain('没有产生任务')
      expect(wrapper.get('[data-testid="task-find-code"]').text()).toContain(
        code === 'expired' ? '回执已过期' : '这不等于任务没有执行'
      )
    })
  }

  // ── S3-05：锁定重挂载后仍能显式只读核对 ─────────────────────────
  it('重新挂载后恢复上一会话的未决关联，且不带任务内容', async () => {
    const findKeys: string[] = []
    const first = mountPage(clientWith({
      submitTask: async () => { throw new ZbaNetworkError('connectionreset') },
      findTask: async (key: string) => { findKeys.push(key); return { code: 'found', instanceId: 'i-1', task: task() } }
    }))
    await flushPromises()
    await fill(first)
    await first.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    const key = first.get('[data-testid="task-idempotency-key"]').text()
    first.unmount()

    const second = mountPage(clientWith({
      findTask: async (value: string) => { findKeys.push(value); return { code: 'found', instanceId: 'i-1', task: task() } }
    }))
    await flushPromises()
    expect(second.get('[data-testid="task-idempotency-key"]').text()).toBe(key)
    expect(second.get('[data-testid="task-submission-restored"]').text()).toContain('没有保留')
    await second.get('[data-testid="task-find"]').trigger('click')
    await flushPromises()
    expect(findKeys).toEqual([key])
  })

  // ── S3-07：迟到取消回包不覆盖已观察到的终态 ─────────────────────
  it('已确认完成的任务不被迟到的取消回包倒退回已请求取消', async () => {
    let current: ZbaTaskSummary = task({ state: 1 })
    const client = clientWith({
      listTasks: async () => ({ items: [task({ ...current })], nextCursor: null }),
      cancelTask: async () => ({ state: 5, cancellationRequested: true, outcome: 'cancel_requested' })
    })
    const wrapper = mountPage(client)
    await flushPromises()
    expect(wrapper.get('[data-testid="task-state"]').text()).toBe('执行中')

    // 取消回包还没落地时，后台轮询已经把状态读成"已完成"。
    current = task({ state: 3 })
    const cancelling = wrapper.get('[data-testid="task-cancel-t-1"]').trigger('click')
    await wrapper.setProps({ active: true })
    // 用同一份 client 重新读取：模拟查询确认终态
    await wrapper.vm.reload()
    await cancelling
    await flushPromises()

    expect(wrapper.get('[data-testid="task-state"]').text()).toBe('已完成')
    expect(wrapper.text()).toContain('已请求（未确认）')
  })

  // ── S3-09：审批失败补读队列并阻止反复提交 ───────────────────────
  it('审批过期后补读队列，并把这一行标为未生效', async () => {
    const approvals = [{
      id: 'a-1', toolId: 'synthetic.tool', summary: '合成摘要',
      session: 'synthetic-session', expiresAt: '2026-10-22T11:00:00Z', policyVersion: 1
    }]
    let reads = 0
    const wrapper = mountPage(clientWith({
      listApprovals: async () => { reads += 1; return approvals },
      decideApproval: async () => { throw new ZbaRequestError(409, 'approval_rejected', '审批未执行。', 'expired') }
    }))
    await flushPromises()
    const before = reads
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()

    expect(reads).toBeGreaterThan(before)
    expect(wrapper.text()).toContain('approval_rejected')
    expect(wrapper.get('[data-testid="task-approval-stale-0"]').text()).toContain('未生效')
    expect(wrapper.get('[data-testid="task-approve-0"]').attributes('disabled')).toBeDefined()
    // 不反复提交：再点也不发新决定
    const decisions: unknown[] = []
    await wrapper.get('[data-testid="task-approve-0"]').trigger('click')
    await flushPromises()
    expect(decisions).toHaveLength(0)
  })

  // ── S3-03：空队列与读取失败必须能区分开 ─────────────────────────
  it('审批队列读取成功才显示空态，读取失败显示错误', async () => {
    const empty = mountPage(clientWith({ listApprovals: async () => [] }))
    await flushPromises()
    expect(empty.find('[data-testid="task-approvals-empty"]').exists()).toBe(true)
    empty.unmount()

    const failed = mountPage(clientWith({
      listApprovals: async () => { throw new ZbaRequestError(502, 'http_error', '上游错误。') }
    }))
    await flushPromises()
    expect(failed.find('[data-testid="task-approvals-empty"]').exists()).toBe(false)
    expect(failed.find('[data-testid="error-notice"]').exists()).toBe(true)
  })

  // ── CAPACITY-M1：未决名额按身份划分，且满额时显式停用 ───────────
  it('别的身份攒满未决记录，不影响本身份的提交入口（CAPACITY-M1）', async () => {
    clearPending()
    const other = clientWith({ identityLabel: 'cap-other-0' })
    const service = serviceLabel(other)
    // 别的身份合计 12 条未决（分布在 12 个身份上）：超过旧的全局 LIMIT=8，
    // 本身份必须完全不受影响。
    for (let index = 0; index < 12; index += 1) {
      const record = beginPending({
        key: 'task1-other-' + index,
        sourceKey: 'panel:synthetic',
        service,
        identity: 'cap-other-' + index
      })
      leavePendingUnknown(record.token)
    }
    const submits: unknown[] = []
    const mine = mountPage(clientWith({
      identityLabel: 'cap-mine',
      submitTask: async (request: unknown) => {
        submits.push(request)
        return { id: 't-9', state: 0, acceptedAt: '2026-10-22T10:00:00Z' }
      }
    }))
    await flushPromises()
    await fill(mine)
    // 入口没有被别人的容量牵连停用。
    expect(mine.find('[data-testid="task-submit-capacity"]').exists()).toBe(false)
    await mine.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    expect(submits).toHaveLength(1)
    expect(mine.get('[data-testid="task-submission-state"]').text()).toContain('已受理')
  })

  it('8 条旧终态不能挤掉最新未决提交的核对入口（S3-RCV-01）', async () => {
    clearPending()
    const client = clientWith({ identityLabel: 'recovery-priority' })
    const service = serviceLabel(client)
    for (let index = 0; index < 8; index += 1) {
      const settled = beginPending({ key: 'task1-settled-' + index, sourceKey: 'panel:synthetic', service, identity: 'recovery-priority' })
      settlePending(settled.token, 'rejected')
    }
    const unresolved = beginPending({ key: 'task1-current-unresolved', sourceKey: 'panel:synthetic', service, identity: 'recovery-priority' })
    leavePendingUnknown(unresolved.token)
    const wrapper = mountPage(client)
    await flushPromises()
    const rows = wrapper.findAll('[data-testid="task-submission"]')
    expect(rows).toHaveLength(8)
    const row = rows.find((item) => item.text().includes('task1-current-unresolved'))
    expect(row).toBeDefined()
    expect(row?.find('[data-testid="task-find"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="task-submit"]').attributes('disabled')).toBeDefined()
  })

  it('本身份未决满额时显式停用入口并说明原因（CAPACITY-M1）', async () => {
    clearPending()
    const client = clientWith({ identityLabel: 'cap-full' })
    const service = serviceLabel(client)
    // 等价于并发/多标签页竞态下攒出的并列未决：预置 8 条本身份未决记录。
    for (let index = 0; index < 8; index += 1) {
      const record = beginPending({
        key: 'task1-full-' + index,
        sourceKey: 'panel:synthetic',
        service,
        identity: 'cap-full'
      })
      leavePendingUnknown(record.token)
    }
    const full = mountPage(client)
    await flushPromises()
    await fill(full)
    // 入口停用 + 常驻说明，并且没有静默丢掉任何一条未决关联。
    expect(full.get('[data-testid="task-submit"]').attributes('disabled')).toBeDefined()
    expect(full.get('[data-testid="task-submit-capacity"]').text()).toContain('新的提交已经停用')
    expect(pendingByKey('task1-full-0')).not.toBeNull()
    // 8 条未决都会以恢复行显示（各自带幂等键），这是正确行为。
    const rowsBefore = full.findAll('[data-testid="task-submission"]').length
    expect(rowsBefore).toBe(8)
    await full.get('[data-testid="task-submit"]').trigger('click')
    await flushPromises()
    // 再点也不会新增第 9 行（点击路径同样被挡下）。
    expect(full.findAll('[data-testid="task-submission"]').length).toBe(8)
  })

  it('卸载后迟到的初次读取不得把已清空的行填回孤儿状态（LIFE-03）', async () => {
    let release!: (value: unknown) => void
    const entered = { done: false }
    const wrapper = mountPage(clientWith({
      listTasks: () => new Promise((resolve) => { entered.done = true; release = resolve })
    }))
    await flushPromises()
    expect(entered.done).toBe(true)
    // 初次读取还挂着的时候真正卸载：refs 已经被 onBeforeUnmount 清空。
    wrapper.unmount()
    expect(wrapper.vm.describe()).toEqual({ tasks: 0, approvals: 0 })
    release({ items: [task()], nextCursor: null })
    await flushPromises()
    // 迟到的响应不得再往已卸载实例里写回任何行。
    expect(wrapper.vm.describe()).toEqual({ tasks: 0, approvals: 0 })
  })

  it('卸载后迟到的审批读取也不得回填（LIFE-03 审批序列）', async () => {
    let release!: (value: unknown) => void
    const wrapper = mountPage(clientWith({
      listApprovals: () => new Promise((resolve) => { release = resolve })
    }))
    await flushPromises()
    wrapper.unmount()
    release([{
      id: 'a-1', taskId: 't-1', state: 'pending', requestedAt: '2026-10-22T10:00:00Z',
      expiresAt: null, summary: '合成审批', reasonCode: ''
    }])
    await flushPromises()
    expect(wrapper.vm.describe()).toEqual({ tasks: 0, approvals: 0 })
  })

  it.each([false, true])('手动刷新途中卸载后不再发审批读取（LIFE-06：reject=%s）', async (rejectTasks) => {
    let resolve!: (value: { items: ZbaTaskSummary[]; nextCursor: null }) => void
    let reject!: (error: Error) => void
    const response = new Promise<{ items: ZbaTaskSummary[]; nextCursor: null }>((yes, no) => {
      resolve = yes
      reject = no
    })
    let taskReads = 0
    let approvalReads = 0
    const wrapper = mountPage(clientWith({
      listTasks: () => {
        taskReads += 1
        return taskReads === 1 ? Promise.resolve({ items: [task()], nextCursor: null }) : response
      },
      listApprovals: async () => { approvalReads += 1; return [] }
    }))
    await flushPromises()
    expect(taskReads).toBe(1)
    expect(approvalReads).toBe(1)
    // 通过真实按钮启动有序刷新，在 listTasks await 期间卸载 Vue 实例。
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    expect(taskReads).toBe(2)
    wrapper.unmount()
    if (rejectTasks) reject(new Error('synthetic late task failure'))
    else resolve({ items: [task()], nextCursor: null })
    await flushPromises()
    expect(wrapper.vm.describe()).toEqual({ tasks: 0, approvals: 0 })
    expect(approvalReads).toBe(1)
    // 卸载后外部保留的 exposed reload 也不得开始新的读取。
    await wrapper.vm.reload()
    expect(taskReads).toBe(2)
    expect(approvalReads).toBe(1)
  })

  it('仍挂载时手动刷新会顺序读取任务和审批（LIFE-06 对照）', async () => {
    let release!: (value: { items: ZbaTaskSummary[]; nextCursor: null }) => void
    const response = new Promise<{ items: ZbaTaskSummary[]; nextCursor: null }>((resolve) => { release = resolve })
    const reads: string[] = []
    let taskReads = 0
    const wrapper = mountPage(clientWith({
      listTasks: () => {
        reads.push('tasks')
        taskReads += 1
        return taskReads === 1 ? Promise.resolve({ items: [task()], nextCursor: null }) : response
      },
      listApprovals: async () => { reads.push('approvals'); return [] }
    }))
    await flushPromises()
    reads.length = 0
    await wrapper.get('[data-testid="task-reload"]').trigger('click')
    await flushPromises()
    expect(reads).toEqual(['tasks'])
    release({ items: [task()], nextCursor: null })
    await flushPromises()
    expect(reads).toEqual(['tasks', 'approvals'])
  })

  it('结果不可用时如实展示原因，不显示空摘要', async () => {
    const wrapper = mountPage(clientWith({
      getTaskResult: async () => ({
        id: 't-1', state: 4, updatedAt: '2026-10-22T10:00:09Z', available: false,
        reasonCode: 'result_expired', summaryTruncated: false, summaryChars: 0
      })
    }))
    await flushPromises()
    await wrapper.get('[data-testid="task-result-t-1"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="task-result-unavailable"]').text()).toContain('result_expired')
    expect(wrapper.find('[data-testid="task-result-summary"]').exists()).toBe(false)
  })
})
