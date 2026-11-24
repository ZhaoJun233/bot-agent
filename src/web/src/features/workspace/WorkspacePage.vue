<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  type ZbaApprovalSummary,
  type ZbaClient,
  type ZbaEvent,
  type ZbaPersonalTodoView,
  type ZbaScheduledJobRunView,
  type ZbaScheduledJobView,
  type ZbaTaskSummary,
  type ZbaWorkspaceIdentityResponse,
  type ZbaWorkspaceMessageView,
} from '../../api/zba'
import { errorName } from '../../shared/friendly'
import { newNonce } from '../../shared/nonce'
import { acquireScheduler, type SyncSubscriber } from '../../shared/sync/event-scheduler'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

const props = defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active: boolean
}>()

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [err: UiError]
  busy: [value: boolean]
}>()

const loading = ref(false)
const sending = ref(false)
const mutating = ref(false)
const confirmClear = ref(false)
const confirmEnableIdentity = ref(false)
const serverIdentityEnabled = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<string | null>(null)
const mobileTab = ref<'chat' | 'lists' | 'ops'>('chat')

const messages = ref<ZbaWorkspaceMessageView[]>([])
const lastSequence = ref(0)
const draftText = ref('')
const draftMode = ref<'chat' | 'task'>('chat')
const attachmentName = ref<string | null>(null)
const attachmentBase64 = ref<string | null>(null)
const pendingSendKey = ref<string | null>(null)
const fileInputEl = ref<HTMLInputElement | null>(null)

const todos = ref<ZbaPersonalTodoView[]>([])
const todosRevision = ref(0)
const newTodoTitle = ref('')
const newTodoDue = ref('')

const jobs = ref<ZbaScheduledJobView[]>([])
const jobsRevision = ref(0)
const runs = ref<ZbaScheduledJobRunView[]>([])
const showRuns = ref(false)
const newSchedScope = ref('workspace:owner')
const newSchedKind = ref<'once_reminder' | 'cron_text'>('once_reminder')
const newSchedWhen = ref('')
const newSchedText = ref('')

const identity = ref<ZbaWorkspaceIdentityResponse>({
  enabled: false,
  shareTodos: false,
  shareKnowledge: false,
  sharePreferences: false,
  links: [],
  workspaceCanonicalPersonId: null,
})

const approvals = ref<ZbaApprovalSummary[]>([])
const tasks = ref<ZbaTaskSummary[]>([])

const canWrite = computed(() => props.mode === 'admin' && props.writesEnabled)
const defaultTimezone = Intl.DateTimeFormat().resolvedOptions().timeZone || 'Asia/Shanghai'

const linkedPersonIds = computed(() => {
  const counts = new Map<string, number>()
  for (const l of identity.value.links) {
    counts.set(l.canonicalPersonId, (counts.get(l.canonicalPersonId) ?? 0) + 1)
  }
  return [...counts.entries()].filter(([, c]) => c >= 2).map(([id]) => id)
})

const TASK_STATE_LABELS: Record<number, string> = {
  0: '排队中',
  1: '执行中',
  2: '待审批',
  3: '已完成',
  4: '失败',
  5: '已请求取消',
  6: '已取消',
  7: '未知',
}

function formatTaskState(state: number | string): string {
  if (typeof state === 'number') return TASK_STATE_LABELS[state] ?? `未知(${state})`
  return String(state)
}

let schedulerHandle: ReturnType<typeof acquireScheduler> | null = null
let unsubSync: (() => void) | null = null
let pendingReload = false
let unmounted = false
let loadSeq = 0
let savedIdentitySnapshot: ZbaWorkspaceIdentityResponse = {
  enabled: false,
  shareTodos: false,
  shareKnowledge: false,
  sharePreferences: false,
  links: [],
  workspaceCanonicalPersonId: null,
}

function isIdentityDirty(): boolean {
  return confirmEnableIdentity.value
    || identity.value.enabled !== savedIdentitySnapshot.enabled
    || identity.value.shareTodos !== savedIdentitySnapshot.shareTodos
    || identity.value.shareKnowledge !== savedIdentitySnapshot.shareKnowledge
    || identity.value.sharePreferences !== savedIdentitySnapshot.sharePreferences
    || (identity.value.workspaceCanonicalPersonId ?? null) !== (savedIdentitySnapshot.workspaceCanonicalPersonId ?? null)
}

const draftCount = computed(() => {
  let count = 0
  if (draftText.value.trim() || attachmentBase64.value) count++
  if (newTodoTitle.value.trim() || newTodoDue.value.trim()) count++
  if (newSchedText.value.trim() || newSchedWhen.value.trim()) count++
  if (isIdentityDirty()) count++
  return count
})

function handleError(err: unknown): void {
  const ui = toUiError(err)
  const resolved = errorName(ui.code)
  error.value = resolved ? { ...ui, message: resolved } : ui
  if (ui.status === 401 || (ui.status === 403 && (canWrite.value || ui.code === 'forbidden'))) {
    emit('auth-expired', ui)
  }
}

function clearSensitiveState(): void {
  messages.value = []
  lastSequence.value = 0
  todos.value = []
  todosRevision.value = 0
  jobs.value = []
  jobsRevision.value = 0
  runs.value = []
  showRuns.value = false
  draftText.value = ''
  newTodoTitle.value = ''
  newTodoDue.value = ''
  newSchedScope.value = 'workspace:owner'
  newSchedKind.value = 'once_reminder'
  newSchedWhen.value = ''
  newSchedText.value = ''
  attachmentName.value = null
  attachmentBase64.value = null
  pendingSendKey.value = null
  confirmClear.value = false
  confirmEnableIdentity.value = false
  identity.value = {
    ...savedIdentitySnapshot,
    links: identity.value.links,
  }
  if (fileInputEl.value) fileInputEl.value.value = ''
}

function parseFutureUnix(raw: string): { ok: true; unix: number } | { ok: false; code: string; message: string } {
  const trimmed = raw.trim()
  const normalized = /^\d{4}-\d{2}-\d{2}\s+\d{2}:\d{2}(:\d{2})?$/.test(trimmed)
    ? trimmed.replace(/\s+/, 'T')
    : trimmed
  const ms = Date.parse(normalized)
  if (Number.isNaN(ms)) {
    return {
      ok: false,
      code: 'scheduler_time_invalid',
      message: '时间格式无效，请使用 YYYY-MM-DD HH:mm 或 ISO-8601 格式。',
    }
  }
  const unix = Math.floor(ms / 1000)
  if (unix <= Math.floor(Date.now() / 1000)) {
    return {
      ok: false,
      code: 'scheduler_fire_time_in_past',
      message: '触发或截止时间必须晚于当前时间。',
    }
  }
  return { ok: true, unix }
}

function isFiveFieldCron(raw: string): boolean {
  const parts = raw.trim().split(/\s+/).filter(Boolean)
  return parts.length === 5
}

async function loadAll(throwOnError = false): Promise<void> {
  pendingReload = true
  if (loading.value) return
  loading.value = true
  emit('busy', true)
  try {
    while (pendingReload) {
      pendingReload = false
      error.value = null
      const currentSeq = ++loadSeq
      const client = props.client
      const writableAtStart = canWrite.value
      try {
        const [schedSnap, idSnap, taskPage, approvalList, todoSnap, ws] = await Promise.all([
          client.listScheduledJobs(),
          client.readWorkspaceIdentity(),
          client.listTasks({ limit: 10 }),
          client.listApprovals(),
          writableAtStart ? client.listPersonalTodos() : Promise.resolve(null),
          writableAtStart ? client.readWorkspaceMessages() : Promise.resolve(null),
        ])
        if (unmounted || client !== props.client || currentSeq !== loadSeq) continue
        jobs.value = schedSnap.jobs
        jobsRevision.value = schedSnap.revision
        const keepIdentityDraft = canWrite.value && isIdentityDirty()
        savedIdentitySnapshot = idSnap
        serverIdentityEnabled.value = idSnap.enabled
        identity.value = keepIdentityDraft
          ? { ...identity.value, links: [...idSnap.links] }
          : { ...idSnap, links: [...idSnap.links] }
        tasks.value = taskPage.items
        approvals.value = approvalList
        if (canWrite.value && writableAtStart && todoSnap) {
          todos.value = todoSnap.items
          todosRevision.value = todoSnap.revision
        } else {
          todos.value = []
          todosRevision.value = 0
        }
        if (canWrite.value && writableAtStart && ws) {
          messages.value = ws.messages
          lastSequence.value = ws.lastSequence
        } else {
          messages.value = []
          lastSequence.value = 0
        }
      } catch (err) {
        if (unmounted || client !== props.client) continue
        handleError(err)
        if (throwOnError) throw err
      }
    }
  } finally {
    loading.value = false
    emit('busy', false)
  }
}

async function onFileSelected(event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const client = props.client
  const file = input.files?.[0]
  if (!file) {
    attachmentName.value = null
    attachmentBase64.value = null
    return
  }
  if (file.size === 0 || file.size > 10 * 1024 * 1024) {
    attachmentName.value = null
    attachmentBase64.value = null
    input.value = ''
    error.value = { code: 'attachment_too_large', message: errorName('attachment_too_large') ?? '附件体积超过 10 MiB 上限或内容为空' }
    return
  }
  const bytes = new Uint8Array(await file.arrayBuffer())
  if (unmounted || client !== props.client || !canWrite.value) return
  let bin = ''
  const chunk = 0x8000
  for (let i = 0; i < bytes.length; i += chunk) {
    bin += String.fromCharCode(...bytes.subarray(i, i + chunk))
  }
  attachmentName.value = file.name
  attachmentBase64.value = btoa(bin)
}

async function sendMessage(): Promise<void> {
  if (!canWrite.value || sending.value) return
  if (!draftText.value.trim() && !attachmentBase64.value) return
  sending.value = true
  error.value = null
  notice.value = null
  const client = props.client
  const idempotencyKey = pendingSendKey.value ?? `ws-${newNonce()}`
  pendingSendKey.value = idempotencyKey
  try {
    const res = await client.sendWorkspaceMessage({
      idempotencyKey,
      text: draftText.value.trim(),
      mode: draftMode.value,
      attachmentName: attachmentName.value,
      attachmentBase64: attachmentBase64.value,
    })
    if (unmounted || client !== props.client || !canWrite.value) return
    messages.value = res.snapshot.messages
    lastSequence.value = res.snapshot.lastSequence
    if (!res.ok) {
      error.value = {
        code: res.reasonCode || 'workspace_send_failed',
        message: errorName(res.reasonCode) ?? '发送失败，请保留草稿后重试。',
      }
      return
    }
    draftText.value = ''
    attachmentName.value = null
    attachmentBase64.value = null
    pendingSendKey.value = null
    if (fileInputEl.value) fileInputEl.value.value = ''
    if (res.taskId) {
      notice.value = `已提交后台任务 ${res.taskId}`
      const page = await client.listTasks({ limit: 10 })
      if (unmounted || client !== props.client) return
      tasks.value = page.items
    }
  } catch (err) {
    if (unmounted || client !== props.client) return
    handleError(err)
  } finally {
    sending.value = false
  }
}

function requestClearMessages(): void {
  if (!canWrite.value || mutating.value) return
  confirmClear.value = true
}

async function clearMessages(): Promise<void> {
  if (!canWrite.value || mutating.value) return
  mutating.value = true
  confirmClear.value = false
  error.value = null
  const client = props.client
  try {
    const res = await client.clearWorkspaceMessages()
    if (unmounted || client !== props.client || !canWrite.value) return
    messages.value = res.snapshot.messages
    lastSequence.value = res.snapshot.lastSequence
    notice.value = '已清空个人工作台对话记录。'
  } catch (err) {
    if (unmounted || client !== props.client) return
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function addTodo(): Promise<void> {
  if (!canWrite.value || mutating.value || !newTodoTitle.value.trim()) return
  error.value = null
  let dueUnix: number | null = null
  if (newTodoDue.value.trim()) {
    const parsed = parseFutureUnix(newTodoDue.value)
    if (!parsed.ok) {
      error.value = { code: parsed.code, message: parsed.message }
      return
    }
    dueUnix = parsed.unix
  }
  mutating.value = true
  const client = props.client
  try {
    const res = await client.savePersonalTodo({
      expectedRevision: todosRevision.value,
      idempotencyKey: `todo-${newNonce()}`,
      title: newTodoTitle.value.trim(),
      priority: 2,
      status: 'open',
      dueUnix,
      timezoneIana: defaultTimezone,
    })
    if (unmounted || client !== props.client || !canWrite.value) return
    todos.value = res.snapshot.items
    todosRevision.value = res.snapshot.revision
    newTodoTitle.value = ''
    newTodoDue.value = ''
    if (dueUnix) {
      const sched = await client.listScheduledJobs()
      if (unmounted || client !== props.client || !canWrite.value) return
      jobs.value = sched.jobs
      jobsRevision.value = sched.revision
    }
  } catch (err) {
    if (unmounted || client !== props.client) return
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function completeTodo(item: ZbaPersonalTodoView): Promise<void> {
  if (!canWrite.value || mutating.value) return
  mutating.value = true
  error.value = null
  const client = props.client
  try {
    const res = await client.savePersonalTodo({
      expectedRevision: todosRevision.value,
      idempotencyKey: `todo-done-${newNonce()}`,
      todoId: item.todoId,
      title: item.title,
      notes: item.notes ?? null,
      priority: item.priority,
      status: 'done',
      dueUnix: item.dueUnix ?? null,
    })
    if (unmounted || client !== props.client || !canWrite.value) return
    todos.value = res.snapshot.items
    todosRevision.value = res.snapshot.revision
    if (item.linkedJobId) {
      const sched = await client.listScheduledJobs()
      if (unmounted || client !== props.client || !canWrite.value) return
      jobs.value = sched.jobs
      jobsRevision.value = sched.revision
    }
  } catch (err) {
    if (unmounted || client !== props.client) return
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function removeTodo(todoId: string): Promise<void> {
  if (!canWrite.value || mutating.value) return
  mutating.value = true
  error.value = null
  const client = props.client
  try {
    const res = await client.deletePersonalTodo({
      expectedRevision: todosRevision.value,
      idempotencyKey: `todo-del-${newNonce()}`,
      todoId,
    })
    if (unmounted || client !== props.client || !canWrite.value) return
    todos.value = res.snapshot.items
    todosRevision.value = res.snapshot.revision
    const sched = await client.listScheduledJobs()
    if (unmounted || client !== props.client || !canWrite.value) return
    jobs.value = sched.jobs
    jobsRevision.value = sched.revision
  } catch (err) {
    if (unmounted || client !== props.client) return
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function addSchedule(): Promise<void> {
  if (!canWrite.value || mutating.value || !newSchedText.value.trim()) return
  error.value = null
  const isCron = newSchedKind.value === 'cron_text'
  let nextFireUnix = 0
  if (isCron) {
    if (!isFiveFieldCron(newSchedWhen.value)) {
      error.value = { code: 'cron_expr_invalid', message: '周期任务需要合法的 5 段 Cron 表达式（例如 */15 * * * *）。' }
      return
    }
  } else {
    const parsed = parseFutureUnix(newSchedWhen.value)
    if (!parsed.ok) {
      error.value = { code: parsed.code, message: parsed.message }
      return
    }
    nextFireUnix = parsed.unix
  }
  mutating.value = true
  try {
    const res = await props.client.saveScheduledJob({
      expectedRevision: jobsRevision.value,
      idempotencyKey: `sched-${newNonce()}`,
      scopeKey: newSchedScope.value.trim() || 'workspace:owner',
      kind: newSchedKind.value,
      cronExpr: isCron ? newSchedWhen.value.trim() : null,
      timezoneIana: defaultTimezone,
      text: newSchedText.value.trim(),
      missedPolicy: 'skip_and_notify',
      enabled: true,
      nextFireUnix,
    })
    jobs.value = res.snapshot.jobs
    jobsRevision.value = res.snapshot.revision
    newSchedWhen.value = ''
    newSchedText.value = ''
  } catch (err) {
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function toggleJob(job: ZbaScheduledJobView): Promise<void> {
  if (!canWrite.value || mutating.value) return
  mutating.value = true
  error.value = null
  try {
    const res = await props.client.saveScheduledJob({
      expectedRevision: jobsRevision.value,
      idempotencyKey: `sched-tog-${newNonce()}`,
      jobId: job.jobId,
      scopeKey: job.scopeKey,
      kind: job.kind,
      cronExpr: job.cronExpr ?? null,
      timezoneIana: job.timezoneIana,
      text: job.text,
      missedPolicy: job.missedPolicy,
      enabled: !job.enabled,
      nextFireUnix: job.nextFireUnix,
    })
    jobs.value = res.snapshot.jobs
    jobsRevision.value = res.snapshot.revision
  } catch (err) {
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function removeJob(jobId: string): Promise<void> {
  if (!canWrite.value || mutating.value) return
  mutating.value = true
  error.value = null
  try {
    const res = await props.client.deleteScheduledJob({
      expectedRevision: jobsRevision.value,
      idempotencyKey: `sched-del-${newNonce()}`,
      jobId,
    })
    jobs.value = res.snapshot.jobs
    jobsRevision.value = res.snapshot.revision
  } catch (err) {
    handleError(err)
  } finally {
    mutating.value = false
  }
}

async function toggleRuns(): Promise<void> {
  showRuns.value = !showRuns.value
  if (!showRuns.value) return
  try {
    const res = await props.client.listScheduledRuns({ limit: 20 })
    runs.value = res.runs
  } catch (err) {
    handleError(err)
  }
}

async function saveIdentity(confirmedEnable = false): Promise<void> {
  if (!canWrite.value || mutating.value) return
  if (identity.value.enabled && !serverIdentityEnabled.value && !confirmedEnable) {
    confirmEnableIdentity.value = true
    return
  }
  const nextEnabled = confirmedEnable ? true : identity.value.enabled
  identity.value.enabled = nextEnabled
  confirmEnableIdentity.value = false
  mutating.value = true
  error.value = null
  try {
    const client = props.client
    const saved = await client.toggleWorkspaceIdentity({
      enabled: nextEnabled,
      shareTodos: nextEnabled ? identity.value.shareTodos : false,
      shareKnowledge: nextEnabled ? identity.value.shareKnowledge : false,
      sharePreferences: nextEnabled ? identity.value.sharePreferences : false,
      workspaceCanonicalPersonId: nextEnabled ? (identity.value.workspaceCanonicalPersonId ?? null) : null,
    })
    if (unmounted || client !== props.client || !canWrite.value) return
    savedIdentitySnapshot = saved
    identity.value = { ...saved, links: [...saved.links] }
    serverIdentityEnabled.value = saved.enabled
    notice.value = '已保存跨平台本人身份关联设置。'
  } catch (err) {
    if (!unmounted) handleError(err)
  } finally {
    mutating.value = false
  }
}

async function decide(id: string, approve: boolean): Promise<void> {
  if (!canWrite.value || mutating.value) return
  mutating.value = true
  error.value = null
  const client = props.client
  try {
    await client.decideApproval(id, approve)
    if (unmounted || client !== props.client || !canWrite.value) return
    approvals.value = await client.listApprovals()
  } catch (err) {
    if (unmounted || client !== props.client) return
    handleError(err)
  } finally {
    mutating.value = false
  }
}

function mountSync(): void {
  unsubSync?.()
  unsubSync = null
  schedulerHandle?.release()
  schedulerHandle = null
  const client = props.client
  schedulerHandle = acquireScheduler(client)
  let pendingReloadWorkspace = false
  let pendingReloadOps = false
  const subscriber: SyncSubscriber = {
    name: 'workspace',
    matches: (event: ZbaEvent) => {
      if (!props.active) return false
      if (event.type === 'workspace.message.appended') {
        pendingReloadWorkspace = true
        return true
      }
      if (event.type.startsWith('task.') || event.type.startsWith('approval.')) {
        pendingReloadOps = true
        return true
      }
      return false
    },
    reload: async () => {
      if (!props.active || unmounted || client !== props.client) return
      const reloadWorkspace = pendingReloadWorkspace
      const reloadOps = pendingReloadOps
      pendingReloadWorkspace = false
      pendingReloadOps = false
      if (!reloadWorkspace && !reloadOps) {
        await loadAll(true)
        return
      }
      if (reloadWorkspace && canWrite.value) {
        const ws = await client.readWorkspaceMessages()
        if (unmounted || client !== props.client || !canWrite.value) return
        messages.value = ws.messages
        lastSequence.value = ws.lastSequence
      }
      if (reloadOps) {
        const [taskPage, approvalList] = await Promise.all([
          client.listTasks({ limit: 10 }),
          client.listApprovals(),
        ])
        if (unmounted || client !== props.client) return
        tasks.value = taskPage.items
        approvals.value = approvalList
      }
    },
  }
  unsubSync = schedulerHandle.scheduler.subscribe(subscriber)
}

watch([draftText, draftMode, attachmentBase64], () => {
  pendingSendKey.value = null
})

watch(() => props.active, (active) => {
  if (active) void loadAll()
})

watch(canWrite, (writable) => {
  if (!writable) {
    ++loadSeq
    clearSensitiveState()
    if (props.active) void loadAll()
  } else if (props.active) {
    void loadAll()
  }
})

watch(() => props.client, () => {
  mountSync()
  if (props.active) void loadAll()
})

onMounted(() => {
  mountSync()
  void loadAll()
})

onBeforeUnmount(() => {
  unmounted = true
  ++loadSeq
  unsubSync?.()
  unsubSync = null
  schedulerHandle?.release()
  schedulerHandle = null
  clearSensitiveState()
})

async function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', _trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock' || reason === 'downgrade') {
    clearSensitiveState()
  }
  return true
}

function discardSecretsOnLeave(): void {
  clearSensitiveState()
}

defineExpose({ reload: loadAll, draftCount, requestLeave, discardSecretsOnLeave })
</script>

<template>
  <section class="workspace-page" data-testid="workspace-page">
    <div v-if="error" class="inline-notice bad" role="alert" data-testid="ws-error">
      {{ error.message }}
    </div>
    <div v-if="notice" class="inline-notice ok" role="status" data-testid="ws-notice">
      {{ notice }}
    </div>

    <div class="ws-mobile-tabs" role="tablist" aria-label="工作台视图切换">
      <button type="button" :class="['tab-btn', { active: mobileTab === 'chat' }]" data-testid="ws-tab-chat" @click="mobileTab = 'chat'">对话</button>
      <button type="button" :class="['tab-btn', { active: mobileTab === 'lists' }]" data-testid="ws-tab-lists" @click="mobileTab = 'lists'">待办与提醒</button>
      <button type="button" :class="['tab-btn', { active: mobileTab === 'ops' }]" data-testid="ws-tab-ops" @click="mobileTab = 'ops'">任务与审批</button>
    </div>

    <div class="ws-grid">
      <!-- 左栏：个人待办、定时提醒与跨平台身份关联 -->
      <aside class="ws-col ws-col-left" :class="{ 'is-mobile-active': mobileTab === 'lists' }">
        <section class="panel">
          <div class="panel-heading">
            <h3>个人待办</h3>
            <span class="badge">{{ todos.length }}</span>
          </div>
          <p class="muted ws-hint">严格限于本人工作台与可靠私聊作用域，群聊不可见。</p>
          <div v-if="canWrite" class="ws-form">
            <input v-model="newTodoTitle" type="text" placeholder="新增待办标题…" data-testid="ws-todo-title" />
            <input v-model="newTodoDue" type="text" placeholder="可选截止 ISO/本地时间 (2026-12-01 09:30)" data-testid="ws-todo-due" />
            <button type="button" class="btn primary" :disabled="mutating" data-testid="ws-todo-add" @click="addTodo">添加待办</button>
          </div>
          <ul class="ws-list" data-testid="ws-todo-list">
            <li v-for="item in todos" :key="item.todoId" class="ws-list-item">
              <div class="ws-item-main">
                <strong>[P{{ item.priority }}] {{ item.title }}</strong>
                <span class="muted"> · {{ item.status }}<template v-if="item.linkedJobId"> · 提醒 {{ item.linkedJobId }}</template></span>
              </div>
              <div v-if="canWrite" class="ws-item-actions">
                <button v-if="item.status === 'open'" type="button" class="tonal" :disabled="mutating" :data-testid="`ws-todo-complete-${item.todoId}`" @click="completeTodo(item)">完成</button>
                <button type="button" class="danger" :disabled="mutating" :data-testid="`ws-todo-delete-${item.todoId}`" @click="removeTodo(item.todoId)">删</button>
              </div>
            </li>
            <li v-if="todos.length === 0" class="muted">{{ canWrite ? '暂无个人待办' : '切换管理员以查看个人待办' }}</li>
          </ul>
        </section>

        <section class="panel">
          <div class="panel-heading">
            <h3>定时与提醒</h3>
            <button type="button" class="tonal" data-testid="ws-sched-runs" @click="toggleRuns">
              {{ showRuns ? '收起记录' : '执行记录' }}
            </button>
          </div>
          <div v-if="canWrite" class="ws-form">
            <input v-model="newSchedScope" type="text" placeholder="作用域 (workspace:owner / group:10001)" data-testid="ws-sched-scope" />
            <select v-model="newSchedKind" data-testid="ws-sched-kind">
              <option value="once_reminder">一次性提醒</option>
              <option value="cron_text">周期 Cron 推送</option>
            </select>
            <input v-model="newSchedWhen" type="text" :placeholder="newSchedKind === 'cron_text' ? '5段 Cron (*/15 * * * *)' : '未来 ISO/本地时间 (2026-12-01T09:30)'" data-testid="ws-sched-when" />
            <input v-model="newSchedText" type="text" placeholder="提醒正文…" data-testid="ws-sched-text" />
            <button type="button" class="btn primary" :disabled="mutating" data-testid="ws-sched-add" @click="addSchedule">创建定时</button>
          </div>
          <ul class="ws-list" data-testid="ws-sched-list">
            <li v-for="j in jobs" :key="j.jobId" class="ws-list-item">
              <div class="ws-item-main">
                <strong>{{ j.text }}</strong>
                <div class="muted">{{ j.maskedScope }} · {{ j.kind }} · {{ j.enabled ? '启用' : '暂停' }}</div>
              </div>
              <div v-if="canWrite" class="ws-item-actions">
                <button type="button" class="tonal" :disabled="mutating" :data-testid="`ws-sched-toggle-${j.jobId}`" @click="toggleJob(j)">{{ j.enabled ? '暂停' : '启用' }}</button>
                <button type="button" class="danger" :disabled="mutating" :data-testid="`ws-sched-delete-${j.jobId}`" @click="removeJob(j.jobId)">删</button>
              </div>
            </li>
            <li v-if="jobs.length === 0" class="muted">暂无定时任务</li>
          </ul>
          <ul v-if="showRuns" class="ws-list ws-runs" data-testid="ws-sched-runs-list">
            <li v-for="r in runs" :key="r.runKey" class="ws-list-item muted">
              {{ r.jobId }} · {{ r.deliveryStatus }} ({{ r.reasonCode || 'ok' }})
            </li>
          </ul>
        </section>

        <section class="panel">
          <div class="panel-heading">
            <h3>跨平台本人身份（ADR-0006）</h3>
          </div>
          <p class="muted ws-hint">默认关闭；仅共享明确勾选的个人数据，永不共享聊天记录与群权限。</p>
          <div class="ws-checks">
            <label><input v-model="identity.enabled" type="checkbox" :disabled="!canWrite || mutating" data-testid="ws-identity-enabled" /> 启用跨端身份绑定</label>
            <label><input v-model="identity.shareTodos" type="checkbox" :disabled="!canWrite || !identity.enabled || mutating" /> 共享个人待办</label>
            <label><input v-model="identity.shareKnowledge" type="checkbox" :disabled="!canWrite || !identity.enabled || mutating" /> 共享个人知识库</label>
            <label><input v-model="identity.sharePreferences" type="checkbox" :disabled="!canWrite || !identity.enabled || mutating" /> 共享个人偏好</label>
            <select v-if="canWrite && identity.enabled && linkedPersonIds.length > 0" v-model="identity.workspaceCanonicalPersonId" data-testid="ws-identity-bind-person">
              <option :value="null">工作台独立作用域（不关联跨端分组）</option>
              <option v-for="pid in linkedPersonIds" :key="pid" :value="pid">绑定跨端身份 {{ pid }}</option>
            </select>
            <button v-if="canWrite && !confirmEnableIdentity" type="button" class="tonal" :disabled="mutating" data-testid="ws-identity-save" @click="saveIdentity(false)">保存绑定开关</button>
            <div v-if="canWrite && confirmEnableIdentity" class="ws-item-actions">
              <button type="button" class="btn primary" :disabled="mutating" data-testid="ws-identity-confirm-enable" @click="saveIdentity(true)">确认开启跨端绑定</button>
              <button type="button" class="tonal" :disabled="mutating" @click="confirmEnableIdentity = false">取消</button>
            </div>
          </div>
          <p class="muted ws-hint">已绑定端点：{{ identity.links.length }} 个</p>
        </section>
      </aside>

      <!-- 中栏：个人工作台专属对话与文档分析 -->
      <section class="ws-col ws-col-center panel" :class="{ 'is-mobile-active': mobileTab === 'chat' }">
        <div class="panel-heading">
          <div class="ws-item-main">
            <h3>个人工作台对话</h3>
            <span class="muted">作用域：workspace:owner（与真实群聊/私聊物理隔离）</span>
          </div>
          <button v-if="canWrite && !confirmClear" type="button" class="tonal" :disabled="mutating" data-testid="ws-clear" @click="requestClearMessages">清空对话</button>
          <div v-if="canWrite && confirmClear" class="ws-item-actions">
            <button type="button" class="danger" :disabled="mutating" data-testid="ws-clear-confirm" @click="clearMessages">确认清空</button>
            <button type="button" class="tonal" :disabled="mutating" @click="confirmClear = false">取消</button>
          </div>
        </div>

        <div v-if="!canWrite" class="ws-readonly-banner" data-testid="ws-readonly-banner">
          <p>读取与发送 `workspace:owner` 对话需要管理员模式。</p>
          <button type="button" class="btn primary" data-testid="ws-unlock-admin" @click="emit('enable-admin')">切换管理员</button>
        </div>

        <div v-else class="ws-messages" data-testid="ws-messages">
          <div v-for="m in messages" :key="m.sequence" class="ws-msg" :class="`ws-msg--${m.role}`">
            <div class="ws-msg-meta">
              <strong>{{ m.role === 'assistant' ? 'BotAgent' : '本人' }}</strong>
              <span> · {{ m.mode }}</span>
              <span v-if="m.attachmentName"> · 附件: {{ m.attachmentName }}</span>
              <span v-if="m.taskId"> · 任务 {{ m.taskId }}</span>
            </div>
            <p class="ws-msg-body">{{ m.text }}</p>
          </div>
          <p v-if="messages.length === 0" class="muted">在本人专属工作台开始对话或上传文档（≤ 10 MiB）。</p>
        </div>

        <form v-if="canWrite" class="ws-composer" @submit.prevent="sendMessage">
          <div class="ws-composer-bar">
            <select v-model="draftMode" data-testid="ws-mode">
              <option value="chat">快速对话</option>
              <option value="task">后台 Agent 任务</option>
            </select>
            <input ref="fileInputEl" class="ws-file-input" type="file" data-testid="ws-file" @change="onFileSelected" />
            <span v-if="attachmentName" class="muted">{{ attachmentName }}</span>
          </div>
          <textarea v-model="draftText" rows="3" placeholder="输入问题、待办整理或对上传文档提问…" data-testid="ws-input" />
          <div class="ws-composer-actions">
            <button type="submit" class="btn primary" :disabled="sending || mutating" data-testid="ws-send">
              {{ sending ? '发送中…' : '发送' }}
            </button>
          </div>
        </form>
      </section>

      <!-- 右栏：实时 Agent 任务与待审批 -->
      <aside class="ws-col ws-col-right" :class="{ 'is-mobile-active': mobileTab === 'ops' }">
        <section class="panel">
          <div class="panel-heading">
            <h3>待审批请求</h3>
            <span class="badge">{{ approvals.length }}</span>
          </div>
          <ul class="ws-list" data-testid="ws-approvals">
            <li v-for="a in approvals" :key="a.id" class="ws-list-item">
              <div class="ws-item-main">
                <strong>{{ a.toolId }}</strong>
                <div class="muted">{{ a.summary }}</div>
              </div>
              <div v-if="canWrite" class="ws-item-actions">
                <button type="button" class="btn primary" :disabled="mutating" :data-testid="`ws-approval-approve-${a.id}`" @click="decide(a.id, true)">同意</button>
                <button type="button" class="danger" :disabled="mutating" :data-testid="`ws-approval-reject-${a.id}`" @click="decide(a.id, false)">拒绝</button>
              </div>
            </li>
            <li v-if="approvals.length === 0" class="muted">暂无待审批请求</li>
          </ul>
        </section>

        <section class="panel">
          <div class="panel-heading">
            <h3>近期后台任务</h3>
            <span class="badge">{{ tasks.length }}</span>
          </div>
          <ul class="ws-list" data-testid="ws-tasks">
            <li v-for="t in tasks" :key="t.id" class="ws-list-item">
              <div class="ws-item-main">
                <strong>{{ t.id }}</strong>
                <div class="muted">状态：{{ formatTaskState(t.state) }}</div>
              </div>
            </li>
            <li v-if="tasks.length === 0" class="muted">暂无近期任务</li>
          </ul>
        </section>
      </aside>
    </div>
  </section>
</template>

<style scoped>
.workspace-page {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.ws-mobile-tabs {
  display: none;
  flex-wrap: wrap;
  gap: 6px;
}

.tab-btn {
  padding: 6px 10px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
  cursor: pointer;
}

.tab-btn.active {
  background: var(--primary);
  color: var(--on-primary);
  border-color: var(--primary);
}

.ws-grid {
  display: grid;
  grid-template-columns: minmax(260px, 1fr) minmax(340px, 1.45fr) minmax(240px, 0.9fr);
  gap: 12px;
  align-items: start;
}

.ws-col {
  display: flex;
  flex-direction: column;
  gap: 12px;
  min-width: 0;
}

.ws-form,
.ws-checks {
  display: flex;
  flex-direction: column;
  gap: 6px;
  margin-bottom: 10px;
}

.ws-form input,
.ws-form select,
.ws-composer textarea,
.ws-composer select {
  width: 100%;
  box-sizing: border-box;
  padding: 6px 8px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: var(--surface);
  color: var(--text);
}

.ws-file-input {
  max-width: 100%;
}

.ws-list {
  list-style: none;
  margin: 0;
  padding: 0;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.ws-list-item {
  display: flex;
  justify-content: space-between;
  align-items: flex-start;
  flex-wrap: wrap;
  gap: 8px;
  padding: 8px;
  border-radius: 8px;
  border: 1px solid var(--border);
  min-width: 0;
}

.ws-item-main {
  min-width: 0;
  overflow-wrap: anywhere;
}

.ws-item-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 4px;
  flex-shrink: 0;
}

.ws-messages {
  max-height: 460px;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
  gap: 8px;
  margin: 8px 0;
}

.ws-msg {
  padding: 8px 10px;
  border-radius: 8px;
  border: 1px solid var(--border);
}

.ws-msg--assistant {
  background: var(--surface-muted);
}

.ws-msg-body {
  margin: 4px 0 0;
  white-space: pre-wrap;
  word-break: break-word;
}

.ws-composer-bar {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  margin-bottom: 6px;
}

.ws-composer-actions {
  margin-top: 6px;
  display: flex;
  justify-content: flex-end;
}

.ws-readonly-banner {
  padding: 12px;
  border-radius: 8px;
  border: 1px dashed var(--border);
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
}

.ws-hint {
  font-size: 12px;
  margin: 4px 0 8px;
}

@media (max-width: 1160px) {
  .ws-mobile-tabs {
    display: flex;
  }
  .ws-grid {
    grid-template-columns: 1fr;
  }
  .ws-col {
    display: none;
  }
  .ws-col.is-mobile-active {
    display: flex;
  }
}
</style>
