import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import WorkspacePage from '../../src/features/workspace/WorkspacePage.vue'
import { createIframeBridgeController } from '../../src/features/capabilities/plugin-ui-iframe-bridge'
import { type ZbaPluginUiSession } from '../../src/api/plugin-ui'
import type { ZbaClient } from '../../src/api/zba'

const mounted: Array<{ unmount: () => void }> = []
afterEach(() => {
  for (const wrapper of mounted.splice(0)) wrapper.unmount()
})

const SHA_A = 'a'.repeat(64)

function makeClient(overrides: Partial<Record<string, unknown>> = {}): ZbaClient {
  return {
    baseUrl: '',
    token: 'test-token',
    events: vi.fn(async () => ({ items: [], nextSequence: 0, resyncRequired: false, serverTime: '2026-11-23T00:00:00Z' })),
    listPersonalTodos: vi.fn(async () => ({
      revision: 1,
      items: [
        {
          todoId: 'todo-1',
          ownerScopeKey: 'workspace:owner',
          title: '已有待办项',
          notes: '备注',
          priority: 1,
          status: 'open',
          dueUnix: 1795400000,
          linkedJobId: 'job-todo-1',
          sourceHint: 'workspace',
          createdUnix: 1795300000,
          updatedUnix: 1795300000,
        },
      ],
    })),
    savePersonalTodo: vi.fn(async (req: { status?: string; title: string; todoId?: string | null }) => ({
      ok: true,
      replayed: false,
      reasonCode: req.todoId ? 'updated' : 'created',
      snapshot: {
        revision: 2,
        items: [
          {
            todoId: req.todoId ?? 'todo-2',
            ownerScopeKey: 'workspace:owner',
            title: req.title,
            notes: null,
            priority: 2,
            status: req.status ?? 'open',
            dueUnix: null,
            linkedJobId: null,
            sourceHint: null,
            createdUnix: 1795300100,
            updatedUnix: 1795300100,
          },
        ],
      },
    })),
    deletePersonalTodo: vi.fn(async () => ({
      ok: true,
      replayed: false,
      reasonCode: 'deleted',
      snapshot: { revision: 3, items: [] },
    })),
    listScheduledJobs: vi.fn(async () => ({
      revision: 1,
      jobs: [
        {
          jobId: 'job-1',
          scopeKey: 'workspace:owner',
          maskedScope: 'workspace:owner',
          kind: 'cron_text',
          cronExpr: '*/15 * * * *',
          timezoneIana: 'Asia/Shanghai',
          text: '定时饮水提醒',
          missedPolicy: 'skip_and_notify',
          enabled: true,
          nextFireUnix: 1795400900,
          lastFiredUnix: null,
          failureCount: 0,
        },
      ],
    })),
    saveScheduledJob: vi.fn(async (req: { jobId?: string | null; enabled: boolean; text: string; scopeKey: string; kind: string; cronExpr?: string | null }) => ({
      ok: true,
      replayed: false,
      reasonCode: req.jobId ? 'updated' : 'created',
      snapshot: {
        revision: 2,
        jobs: [
          {
            jobId: req.jobId ?? 'job-2',
            scopeKey: req.scopeKey,
            maskedScope: req.scopeKey,
            kind: req.kind,
            cronExpr: req.cronExpr ?? null,
            timezoneIana: 'Asia/Shanghai',
            text: req.text,
            missedPolicy: 'skip_and_notify',
            enabled: req.enabled,
            nextFireUnix: 1795401800,
            lastFiredUnix: null,
            failureCount: 0,
          },
        ],
      },
    })),
    deleteScheduledJob: vi.fn(async () => ({
      ok: true,
      replayed: false,
      reasonCode: 'deleted',
      snapshot: { revision: 3, jobs: [] },
    })),
    listScheduledRuns: vi.fn(async () => ({
      runs: [
        {
          runKey: 'job-1:1795400900',
          jobId: 'job-1',
          scheduledUnix: 1795400900,
          startedUnix: 1795400901,
          finishedUnix: 1795400902,
          deliveryStatus: 'succeeded',
          reasonCode: 'workspace_delivered',
        },
      ],
    })),
    readWorkspaceIdentity: vi.fn(async () => ({
      enabled: false,
      shareTodos: false,
      shareKnowledge: false,
      sharePreferences: false,
      links: [],
    })),
    toggleWorkspaceIdentity: vi.fn(async (req: { enabled: boolean; shareTodos: boolean; shareKnowledge: boolean; sharePreferences: boolean }) => ({
      ...req,
      links: [],
    })),
    readWorkspaceMessages: vi.fn(async () => ({
      scopeKey: 'workspace:owner',
      lastSequence: 1,
      messages: [
        { sequence: 1, role: 'assistant', text: '欢迎使用个人工作台', mode: 'chat', attachmentName: null, createdUnix: 1795300000, taskId: null, status: 'completed' },
      ],
    })),
    sendWorkspaceMessage: vi.fn(async (req: { idempotencyKey: string; text: string; mode: string }) => ({
      ok: true,
      replayed: false,
      operationKey: req.idempotencyKey,
      reasonCode: null,
      taskId: null,
      snapshot: {
        scopeKey: 'workspace:owner',
        lastSequence: 2,
        messages: [
          { sequence: 1, role: 'assistant', text: '欢迎使用个人工作台', mode: 'chat', attachmentName: null, createdUnix: 1795300000, taskId: null, status: 'completed' },
          { sequence: 2, role: 'user', text: req.text, mode: req.mode, attachmentName: null, createdUnix: 1795300050, taskId: null, status: 'accepted' },
        ],
      },
    })),
    clearWorkspaceMessages: vi.fn(async () => ({
      ok: true,
      snapshot: { scopeKey: 'workspace:owner', lastSequence: 2, messages: [] },
    })),
    listTasks: vi.fn(async () => ({
      items: [{ id: 'task-1', state: 1, createdAt: '2026-11-23T00:00:00Z', updatedAt: '2026-11-23T00:00:00Z' }],
      nextCursor: null,
    })),
    listApprovals: vi.fn(async () => [
      { id: 'appr-1', toolId: 'mcp.docs.write', summary: '写入工作台文档', session: 'workspace:owner', expiresAt: '2026-11-23T01:00:00Z', policyVersion: 1 },
    ]),
    decideApproval: vi.fn(async () => ({ accepted: true, decision: 'approved', reason: 'ok' })),
    ...overrides,
  } as unknown as ZbaClient
}

describe('Phase 5 WorkspacePage & iframe bridge ownership', () => {
  it('gates workspace messages and todos in read mode, never calling admin endpoints', async () => {
    const client = makeClient()
    const wrapper = mount(WorkspacePage, {
      props: { client, mode: 'read', writesEnabled: false, active: true },
    })
    mounted.push(wrapper)
    await flushPromises()

    expect(wrapper.find('[data-testid="ws-readonly-banner"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="ws-messages"]').exists()).toBe(false)
    expect(client.readWorkspaceMessages).not.toHaveBeenCalled()
    expect(client.listPersonalTodos).not.toHaveBeenCalled()

    await wrapper.get('[data-testid="ws-unlock-admin"]').trigger('click')
    expect(wrapper.emitted('enable-admin')?.length).toBe(1)
  })

  it('handles todo cascade cancel, scheduler CRUD, workspace send, attachment >10MiB rejection, identity toggle & approval', async () => {
    const client = makeClient()
    const wrapper = mount(WorkspacePage, {
      props: { client, mode: 'admin', writesEnabled: true, active: true },
    })
    mounted.push(wrapper)
    await flushPromises()

    expect(client.readWorkspaceMessages).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="ws-todo-list"]').text()).toContain('已有待办项')

    // Complete todo with linkedJobId -> cascades to reload scheduled jobs
    await wrapper.get('[data-testid="ws-todo-complete-todo-1"]').trigger('click')
    await flushPromises()
    expect(client.savePersonalTodo).toHaveBeenCalledTimes(1)
    expect(client.listScheduledJobs).toHaveBeenCalledTimes(2)

    // Add new todo
    await wrapper.get('[data-testid="ws-todo-title"]').setValue('新待办任务')
    await wrapper.get('[data-testid="ws-todo-add"]').trigger('click')
    await flushPromises()
    expect(client.savePersonalTodo).toHaveBeenCalledTimes(2)

    // Reject invalid once_reminder without silent +5m fallback
    await wrapper.get('[data-testid="ws-sched-when"]').setValue('not-a-valid-date')
    await wrapper.get('[data-testid="ws-sched-text"]').setValue('无效日期测试')
    await wrapper.get('[data-testid="ws-sched-add"]').trigger('click')
    await flushPromises()
    expect(client.saveScheduledJob).not.toHaveBeenCalled()
    expect(wrapper.get('[data-testid="ws-error"]').text()).toContain('时间格式无效')

    // Create valid cron scheduled job & inspect runs
    await wrapper.get('[data-testid="ws-sched-kind"]').setValue('cron_text')
    await wrapper.get('[data-testid="ws-sched-when"]').setValue('*/15 * * * *')
    await wrapper.get('[data-testid="ws-sched-text"]').setValue('每15分钟测试')
    await wrapper.get('[data-testid="ws-sched-add"]').trigger('click')
    await flushPromises()
    expect(client.saveScheduledJob).toHaveBeenCalledTimes(1)
    const saveArg = (client.saveScheduledJob as ReturnType<typeof vi.fn>).mock.calls[0]?.[0] as { cronExpr?: string } | undefined
    expect(saveArg?.cronExpr).toBe('*/15 * * * *')

    await wrapper.get('[data-testid="ws-sched-runs"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="ws-sched-runs-list"]').text()).toContain('workspace_delivered')

    // Send workspace message
    await wrapper.get('[data-testid="ws-input"]').setValue('请总结今日工作台待办')
    await wrapper.get('form.ws-composer').trigger('submit')
    await flushPromises()
    expect(client.sendWorkspaceMessage).toHaveBeenCalledTimes(1)
    expect(wrapper.get('[data-testid="ws-messages"]').text()).toContain('请总结今日工作台待办')

    // HTTP 200 ok:false preserves draft and renders error
    ;(client.sendWorkspaceMessage as ReturnType<typeof vi.fn>).mockResolvedValueOnce({
      ok: false, replayed: false, operationKey: 'k-fail', reasonCode: 'model_unavailable', taskId: null,
      snapshot: { scopeKey: 'workspace:owner', lastSequence: 2, messages: [] },
    })
    await wrapper.get('[data-testid="ws-input"]').setValue('保留草稿内容')
    await wrapper.get('form.ws-composer').trigger('submit')
    await flushPromises()
    expect((wrapper.get('[data-testid="ws-input"]').element as HTMLTextAreaElement).value).toBe('保留草稿内容')
    expect(wrapper.find('[data-testid="ws-error"]').exists()).toBe(true)

    // Reject >10 MiB file upload client-side
    const fileInput = wrapper.get('[data-testid="ws-file"]').element as HTMLInputElement
    const hugeFile = { name: 'huge.txt', size: 10 * 1024 * 1024 + 5, arrayBuffer: async () => new ArrayBuffer(0) } as unknown as File
    Object.defineProperty(fileInput, 'files', { value: [hugeFile], configurable: true })
    await wrapper.get('[data-testid="ws-file"]').trigger('change')
    await flushPromises()
    expect(wrapper.get('[data-testid="ws-error"]').text()).toContain('10 MiB')

    // Toggle identity switch with inline confirmation
    await wrapper.get('[data-testid="ws-identity-enabled"]').setValue(true)
    await wrapper.get('[data-testid="ws-identity-save"]').trigger('click')
    expect(client.toggleWorkspaceIdentity).not.toHaveBeenCalled()
    await wrapper.get('[data-testid="ws-identity-confirm-enable"]').trigger('click')
    await flushPromises()
    expect(client.toggleWorkspaceIdentity).toHaveBeenCalledTimes(1)

    // Approve pending tool request
    await wrapper.get('[data-testid="ws-approval-approve-appr-1"]').trigger('click')
    await flushPromises()
    expect(client.decideApproval).toHaveBeenCalledWith('appr-1', true)

    // Downgrade to read mode clears messages, todos, and unsent draft immediately
    await wrapper.setProps({ mode: 'read', writesEnabled: false })
    await flushPromises()
    expect((wrapper.find('[data-testid="ws-messages"]').exists())).toBe(false)
    expect(wrapper.get('[data-testid="ws-todo-list"]').text()).not.toContain('新待办任务')
  })

  it('enforces scheduler.* and todo.* prefix ownership inside iframe bridge', async () => {
    const client = makeClient()
    const schedSession: ZbaPluginUiSession = {
      sessionId: 's-sched',
      pluginId: 'preset.feature.scheduler',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-s',
      contractVersion: 1,
      entryUrl: `/zba/v1/plugins/preset.feature.scheduler/ui/${SHA_A}/t1/web/index.html`,
      allowedActions: ['scheduler.list', 'scheduler.save', 'todo.list', 'quotas.list'],
      backendReady: true,
      versionMatched: true,
      adoptionMode: 'cold',
      kind: 'panel',
      title: '定时与提醒',
      expiresAt: '2026-11-23T05:00:00Z',
    }
    const schedBridge = createIframeBridgeController(schedSession, () => ({
      client,
      mode: 'admin',
      writesEnabled: true,
      active: true,
      theme: 'light',
      currentGenerationId: 'gen-s',
      onClose: () => {},
    }))

    const listOk = await schedBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r1',
      channelId: schedBridge.channelId,
      pluginId: 'preset.feature.scheduler',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-s',
      action: 'scheduler.list',
    })
    expect(listOk.ok).toBe(true)

    const schedSaveOk = await schedBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r1-save',
      channelId: schedBridge.channelId,
      pluginId: 'preset.feature.scheduler',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-s',
      action: 'scheduler.save',
      payload: { expectedRevision: 0, scopeKey: 'workspace:owner', kind: 'cron_text', cronExpr: '*/15 * * * *', text: '桥接提醒', idempotencyKey: 'sched-valid-key-1' },
    })
    expect(schedSaveOk.ok).toBe(true)

    const schedBadKey = await schedBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r1-bad-key',
      channelId: schedBridge.channelId,
      pluginId: 'preset.feature.scheduler',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-s',
      action: 'scheduler.save',
      payload: { expectedRevision: 0, scopeKey: 'workspace:owner', kind: 'cron_text', cronExpr: '*/15 * * * *', text: '桥接提醒', idempotencyKey: 'bad' },
    })
    expect(schedBadKey).toEqual({ ok: false, error: 'invalid_idempotency_key' })

    const crossTodo = await schedBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r2',
      channelId: schedBridge.channelId,
      pluginId: 'preset.feature.scheduler',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-s',
      action: 'todo.list',
    })
    expect(crossTodo).toEqual({ ok: false, error: 'plugin_ui_action_forbidden' })

    const crossQuota = await schedBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r3',
      channelId: schedBridge.channelId,
      pluginId: 'preset.feature.scheduler',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-s',
      action: 'quotas.list',
    })
    expect(crossQuota).toEqual({ ok: false, error: 'plugin_ui_action_forbidden' })
    schedBridge.dispose()

    const todoSession: ZbaPluginUiSession = {
      ...schedSession,
      sessionId: 's-todo',
      pluginId: 'preset.feature.personal-todo',
      entryUrl: `/zba/v1/plugins/preset.feature.personal-todo/ui/${SHA_A}/t2/web/index.html`,
      allowedActions: ['todo.list', 'todo.save', 'scheduler.list'],
      generationId: 'gen-t',
      title: '个人待办',
    }
    const todoBridge = createIframeBridgeController(todoSession, () => ({
      client,
      mode: 'admin',
      writesEnabled: true,
      active: true,
      theme: 'dark',
      currentGenerationId: 'gen-t',
      onClose: () => {},
    }))
    const todoOk = await todoBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r4',
      channelId: todoBridge.channelId,
      pluginId: 'preset.feature.personal-todo',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-t',
      action: 'todo.list',
    })
    expect(todoOk.ok).toBe(true)

    const todoBadKey = await todoBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r4-bad',
      channelId: todoBridge.channelId,
      pluginId: 'preset.feature.personal-todo',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-t',
      action: 'todo.save',
      payload: { expectedRevision: 0, title: '合法待办', idempotencyKey: 'short' },
    })
    expect(todoBadKey).toEqual({ ok: false, error: 'invalid_idempotency_key' })

    const todoCross = await todoBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r5',
      channelId: todoBridge.channelId,
      pluginId: 'preset.feature.personal-todo',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-t',
      action: 'scheduler.list',
    })
    expect(todoCross).toEqual({ ok: false, error: 'plugin_ui_action_forbidden' })
    todoBridge.dispose()

    // Read mode blocks todo.list
    const todoReadBridge = createIframeBridgeController(todoSession, () => ({
      client,
      mode: 'read',
      writesEnabled: false,
      active: true,
      theme: 'light',
      currentGenerationId: 'gen-t',
      onClose: () => {},
    }))
    const todoReadRes = await todoReadBridge.dispatch({
      type: 'zba.ui.request',
      requestId: 'r6',
      channelId: todoReadBridge.channelId,
      pluginId: 'preset.feature.personal-todo',
      version: '1.0.0',
      releaseSha256: SHA_A,
      generationId: 'gen-t',
      action: 'todo.list',
    })
    expect(todoReadRes).toEqual({ ok: false, error: 'forbidden' })
    todoReadBridge.dispose()
  })

  it('clears jobs, drafts, and unredacted reminders when downgraded from admin to read', async () => {
    const client = makeClient()
    const wrapper = mount(WorkspacePage, {
      props: { client, mode: 'admin', writesEnabled: true, active: true },
    })
    mounted.push(wrapper)
    await flushPromises()

    await wrapper.get('[data-testid="ws-sched-text"]').setValue('未保存敏感提醒草稿')
    expect(wrapper.get('[data-testid="ws-sched-list"]').text()).toContain('定时饮水提醒')

    ;(client.listScheduledJobs as ReturnType<typeof vi.fn>).mockResolvedValueOnce({
      revision: 1,
      jobs: [{
        jobId: 'job-1', scopeKey: 'workspace:owner', maskedScope: 'workspace:owner',
        kind: 'once_reminder', cronExpr: null, timezoneIana: 'Asia/Shanghai',
        text: '[受保护]', missedPolicy: 'skip_and_notify', enabled: true, nextFireUnix: 1900000000, lastFiredUnix: null, failureCount: 0,
      }],
    })
    await wrapper.setProps({ mode: 'read', writesEnabled: false })
    await flushPromises()

    expect(wrapper.get('[data-testid="ws-sched-list"]').text()).toContain('[受保护]')
    expect(wrapper.get('[data-testid="ws-sched-list"]').text()).not.toContain('定时饮水提醒')
    expect(wrapper.find('[data-testid="ws-sched-text"]').exists()).toBe(false)
  })
})
