<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaApprovalSummary,
  type ZbaTaskFindCode,
  type ZbaTaskResult,
  type ZbaTaskState,
  type ZbaTaskSummary
} from '../../api/zba'
import {
  acquireScheduler,
  type EventScheduler,
  type SchedulerSnapshot
} from '../../shared/sync/event-scheduler'
import { registerAppResources } from '../../shared/sync/app-resources'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { newNonce, nowSeconds } from '../configuration/draft'
import {
  activePending,
  beginPending,
  discardPending,
  leavePendingUnknown,
  markPendingSent,
  pendingByKey,
  pendingCapacity,
  pendingEntries,
  PendingCapacityError,
  serviceLabel,
  settlePending,
  type PendingPhase
} from './pending-intent'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  /** 视图是否在前台；离开后不允许把在途写请求继续发出去。 */
  active?: boolean
}>(), { active: true })

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const stateLabels: Record<number, string> = {
  0: '排队中', 1: '执行中', 2: '待审批', 3: '已完成',
  4: '失败', 5: '已请求取消', 6: '已取消', 7: '未知'
}
const stateTones: Record<number, string> = {
  0: 'neutral', 1: 'warn', 2: 'warn', 3: 'ok', 4: 'bad', 5: 'warn', 6: 'neutral', 7: 'warn'
}
/** 终态：只有这三个不需要继续核对。 */
const terminalStates: number[] = [3, 4, 6]

/**
 * 核对结论的文案。
 *
 * `unknown` 写的是"查不到"，不是"没有执行"：台账是内存的，重启/淘汰都会让
 * 记录消失，所以查不到只能证明"现在查不到"。
 */
const findCodeLabels: Record<ZbaTaskFindCode, string> = {
  found: '已找到受理记录',
  expired: '受理回执已过期（服务端曾受理，任务记录已被清理）',
  accepted_unknown: '已受理但结果未知（另一实例受理）',
  unknown: '查不到受理记录（这不等于任务没有执行）'
}

/** 审批队列的一行；`stale` 表示这次决定没生效、只能只读保留。 */
interface ApprovalRow extends ZbaApprovalSummary {
  stale: boolean
  staleReason: string | null
}

const tasks = ref<ZbaTaskSummary[]>([])
const nextCursor = ref<string | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)
const pageError = ref<UiError | null>(null)
const lastLoadedAt = ref<string | null>(null)
const detail = ref<ZbaTaskResult | null>(null)
const detailError = ref<UiError | null>(null)
const detailBusy = ref(false)
const approvals = ref<ApprovalRow[]>([])
const approvalsLoaded = ref(false)
const approvalError = ref<UiError | null>(null)
const decisionError = ref<UiError | null>(null)
const approvalBusy = ref<string[]>([])
const decisionNotice = ref<string | null>(null)
const syncSnapshot = ref<SchedulerSnapshot>({
  state: 'idle', cursor: 0, resyncCount: 0, failures: 0, lastSyncedAt: null,
  failedResource: null
})

/**
 * 同步徽标的措辞要区分两种"没追平"：
 *
 * - events 本身读不到 → 不知道服务端走到哪儿了，说"同步未完成"；
 * - events 读到了、但资源补读失败 → 服务端已确认到更晚的位置，我们手上的副本
 *   一定比它旧，说"数据可能过期"。这两句话对用户的意义不同（R2）。
 */
const syncBadgeText = computed(() => {
  if (syncSnapshot.value.state === 'live') return '已追平'
  if (syncSnapshot.value.state === 'syncing') return '同步中'
  if (syncSnapshot.value.state === 'stale') {
    return syncSnapshot.value.failedResource === null ? '同步未完成' : '数据可能过期'
  }
  return '未同步'
})

const canWrite = computed(() => props.mode === 'admin' && props.writesEnabled)
const activeTasks = computed(() => tasks.value.filter((task) => !isTerminal(task.state)))
const pendingApprovals = computed(() => approvals.value.filter((row) => !row.stale))

/**
 * 页面所有权代数。卸载、离开视图、降级都会让在途写请求失效：
 * 每个 await 之后、真正发出请求之前都要重新核对。
 */
let ownership = 0
let unmounted = false

function ownedBy(epoch: number, client: ZbaClient): boolean {
  return !unmounted && epoch === ownership && props.client === client
    && props.active !== false && props.mode === 'admin' && props.writesEnabled
}

const formatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium', timeStyle: 'medium', hour12: false
})

function formatTime(value: string | null | undefined): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : formatter.format(date)
}

function stateLabel(state: ZbaTaskState): string {
  return stateLabels[state] ?? ('状态 ' + state)
}

function stateTone(state: ZbaTaskState): string {
  return stateTones[state] ?? 'warn'
}

function isTerminal(state: ZbaTaskState): boolean {
  return terminalStates.includes(state)
}

function reportAuthFailure(cause: unknown): boolean {
  if (cause instanceof ZbaRequestError && cause.status === 401) {
    emit('auth-expired', toUiError(cause))
    return true
  }
  return false
}

let loadSequence = 0

/**
 * 第一页重新读取的三种语义。
 *
 * - `initial`：挂载/会话重置，列表本来就是空的；
 * - `interactive`：用户按了「重新读取」——先把旧快照撤下（显示"正在读取任务…"），
 *   读取失败再把它放回去。避免用户一边看着旧行一边以为已经刷新过；
 * - `background`：事件驱动的补读，保持旧数据可见，成功后整份替换。
 */
type RefreshMode = 'initial' | 'interactive' | 'background'

async function load(cursor?: string, mode: RefreshMode = 'initial'): Promise<boolean> {
  // 卸载后的有序刷新或保留的 reload 引用不能再开始读取（LIFE-06）。
  if (unmounted) return true
  // 翻页要防重复，否则会拿到同一页两次；第一页读取允许被更新的一次取代，
  // 用 loadSequence 丢弃迟到响应（早退会让"重新读取"静默变成 no-op）。
  if (cursor && loading.value) return false
  const sequence = ++loadSequence
  // 后台/交互式读取都必须保住旧快照：读取失败要显示旧数据，不能伪装成空列表。
  const snapshot = mode === 'initial' ? null : tasks.value
  const snapshotCursor = nextCursor.value
  if (mode === 'interactive' && !cursor) {
    // 撤下旧快照：这期间列表显示为空 + loading，不会把旧行留在屏幕上。
    tasks.value = []
    nextCursor.value = null
  }
  loading.value = true
  error.value = null
  pageError.value = null
  try {
    const page = await props.client.listTasks({ limit: 50, cursor })
    // 被更新的一次读取取代：不是这次读取失败，交给那一次表达结果。
    // 卸载同样作废这次读取：unmount 已经把 refs 清空，迟到的响应不得再把孤儿行填回去
    // （LIFE-03——只有写请求有所有权闸是不够的，只读序列也要有）。
    if (unmounted || sequence !== loadSequence) return true
    tasks.value = cursor ? [...tasks.value, ...page.items] : page.items
    // 末页服务端整段省略 nextCursor，按"没有下一页"处理。
    nextCursor.value = page.nextCursor ?? null
    lastLoadedAt.value = new Date().toISOString()
    reconcileCancelMarks()
    return true
  } catch (cause) {
    if (unmounted || sequence !== loadSequence) return true
    // 认证失效也算这次读取没成功：不能签"已追平"（会话随后就锁了，游标会被丢弃）。
    if (reportAuthFailure(cause)) return false
    const ui = toUiError(cause)
    if (cursor) {
      pageError.value = ui
    } else {
      error.value = ui
      // 读取失败把旧快照放回去：显示旧数据 + 明确的失败提示，而不是伪装成空列表。
      tasks.value = snapshot ?? []
      nextCursor.value = snapshotCursor
    }
    // 把失败如实告诉调用方（调度器据此不签"已追平"）。
    return false
  } finally {
    if (sequence === loadSequence) loading.value = false
  }
}

async function loadMore(): Promise<void> {
  const cursor = nextCursor.value
  if (cursor === null) return
  await load(cursor)
}

/**
 * 本次会话里决定失败过的审批。
 *
 * 决定失败（过期/已决/策略变更/回执错配）之后不允许在同一页面上反复提交争取成功；
 * 只有重新读取队列后它确实还在、且这次是新的意图才谈得上再决定——所以这里在整个
 * 页面生命周期里保持"未生效"，并把原因摆在行上。
 */
const staleIds = new Set<string>()

let approvalSequence = 0

async function loadApprovals(): Promise<boolean> {
  if (unmounted) return true
  const sequence = ++approvalSequence
  const client = props.client
  try {
    const fresh = await client.listApprovals()
    // 换令牌/换服务后旧读取晚到：整份丢弃，不能让旧身份的队列覆盖新快照。
    if (unmounted || sequence !== approvalSequence || client !== props.client) return true
    approvalsLoaded.value = true
    approvalError.value = null
    // 以服务端队列为准；但决定失败过的行保留为只读痕迹（它可能已经不在队列里了，
    // 也可能因为策略变更又出现了）。刷新失败时下面的 catch 不清列表，显示旧数据。
    const seen = new Set(fresh.map((item) => item.id))
    const carried = approvals.value.filter((row) => row.stale && !seen.has(row.id))
    approvals.value = [
      ...fresh.map((item) => ({
        ...item,
        stale: staleIds.has(item.id),
        staleReason: staleIds.has(item.id) ? (previousReason(item.id) ?? '这次决定没有生效。') : null
      })),
      ...carried.map((row) => ({ ...row, stale: true }))
    ]
    return true
  } catch (cause) {
    if (unmounted || sequence !== approvalSequence || client !== props.client) return true
    if (reportAuthFailure(cause)) return false
    // 刷新失败显示旧数据：不清列表，只标记读取失败。
    approvalError.value = toUiError(cause)
    return false
  }
}

const staleReasons = new Map<string, string>()

function previousReason(id: string): string | null {
  return staleReasons.get(id) ?? null
}

let detailSequence = 0

/**
 * 读取结果详情。
 *
 * 不再用 detailBusy 互斥（那会让后点的任务被前一次的读取挡住）：改用代数，
 * 后一次点击作废前一次的迟到响应。
 */
async function openDetail(task: ZbaTaskSummary): Promise<void> {
  const sequence = ++detailSequence
  const client = props.client
  detailBusy.value = true
  detailError.value = null
  detail.value = null
  try {
    const result = await client.getTaskResult(task.id)
    if (unmounted || sequence !== detailSequence || client !== props.client) return
    if (result === null) {
      detailError.value = { code: 'task_not_found', message: '任务不存在或已被清理。' }
      return
    }
    detail.value = result
  } catch (cause) {
    if (sequence !== detailSequence || client !== props.client) return
    if (reportAuthFailure(cause)) return
    detailError.value = toUiError(cause)
  } finally {
    if (sequence === detailSequence) detailBusy.value = false
  }
}

/**
 * 取消标记：取消是「请求」，不是「已取消」。
 *
 * 一旦我们确实请求过取消，这个标记对同一任务保持（服务端已进入终态也一样）：
 * 后台轮询重读列表时不能把「已请求（未确认）」擦掉。
 */
const cancelMarks = new Set<string>()

function reconcileCancelMarks(): void {
  const seen = new Set(tasks.value.map((task) => task.id))
  for (const id of [...cancelMarks]) {
    if (!seen.has(id)) cancelMarks.delete(id)
  }
  tasks.value = tasks.value.map((task) => task.cancellationRequested || cancelMarks.has(task.id)
    ? { ...task, cancellationRequested: true }
    : task)
}

/**
 * 请求取消。
 *
 * 界面只显示服务端返回的真实状态，绝不把 cancelRequested 写成「已取消」。
 * 更关键：**查询已确认的终态不被迟到响应覆盖**——回包只是我们这次请求的结果，
 * 任务是否结束以资源读取为准（S3-07）。
 */
async function cancelTask(task: ZbaTaskSummary): Promise<void> {
  // 单飞：上一次取消/详情请求还没回来时不再发第二个写请求。
  if (detailBusy.value) return
  if (!canWrite.value) {
    detailError.value = { code: 'read_only', message: '当前不是管理员模式，不能请求取消。' }
    return
  }
  const client = props.client
  const epoch = ownership
  if (!ownedBy(epoch, client)) {
    detailError.value = { code: 'aborted_client', message: '已经离开任务页或权限发生变化，这次取消没有发送。' }
    return
  }
  // 单飞：取消请求在途时不再发第二次（按钮同时按 detailBusy 禁用）。
  if (detailBusy.value) return
  detailBusy.value = true
  detailError.value = null
  try {
    const result = await client.cancelTask(task.id)
    if (!ownedBy(epoch, client)) return
    if (result.cancellationRequested) cancelMarks.add(task.id)
    tasks.value = tasks.value.map((item) => {
      if (item.id !== task.id) return item
      const next: ZbaTaskSummary = {
        ...item,
        cancellationRequested: item.cancellationRequested || result.cancellationRequested
      }
      // 终态是查询得到的结论，迟到的取消回包不能把它退回到"已请求取消"。
      if (!isTerminal(item.state)) next.state = result.state
      return next
    })
    if (result.outcome !== 'requested' && !result.cancellationRequested) {
      detailError.value = {
        code: 'cancel_' + result.outcome,
        message: '服务端没有登记这次取消（结果：' + result.outcome + '）；当前状态为 ' + stateLabel(result.state) + '。'
      }
    }
    scheduler.value?.kick()
  } catch (cause) {
    // 迟到的旧身份回包不能锁掉新会话：先核对所有权，再决定要不要报认证失效。
    if (!ownedBy(epoch, client)) return
    if (reportAuthFailure(cause)) return
    detailError.value = toUiError(cause)
  } finally {
    // busy 是界面标记，不属于任何写意图：无论所有权如何都要释放，否则会卡住。
    detailBusy.value = false
  }
}

/** 审批决定失败的安全语义：只解释 reasonCode，不猜工具为什么没执行。 */
function decideReasonText(reason: string | undefined): string {
  switch (reason) {
    case 'expired': return '这条审批已经过期，队列已重新读取。'
    case 'already_decided': return '这条审批已经被处理过，队列已重新读取。'
    case 'policy_changed': return '审批策略已变化，请按新策略重新确认。'
    case 'source_retired': return '发起这次审批的来源已下线。'
    case 'not_an_approver': return '当前身份不是审批人。'
    default: return reason ? '原因码：' + reason + '。' : '服务端未接受这次决定。'
  }
}

/**
 * 审批决定。
 *
 * 批准/拒绝都绑定本次请求与参数：服务端返回 ok=false（过期、已被他人处理）
 * 或 4xx 时界面必须如实说明，并且**补读独立队列**——不能靠反复提交争取成功
 * （S3-09）。
 */
async function decide(approval: ApprovalRow, approve: boolean): Promise<void> {
  if (!canWrite.value) {
    decisionError.value = { code: 'read_only', message: '当前不是管理员模式，不能审批。' }
    return
  }
  if (approval.stale) {
    decisionError.value = {
      code: 'approval_stale',
      message: '这条审批已经失效，请重新读取队列后再决定。'
    }
    return
  }
  const client = props.client
  const epoch = ownership
  if (!ownedBy(epoch, client)) {
    decisionError.value = { code: 'aborted_client', message: '已经离开任务页或权限发生变化，这次审批没有发送。' }
    return
  }
  if (approvalBusy.value.includes(approval.id)) return
  approvalBusy.value = [...approvalBusy.value, approval.id]
  decisionError.value = null
  decisionNotice.value = null
  try {
    const result = await client.decideApproval(approval.id, approve)
    if (!ownedBy(epoch, client)) return
    if (!result.ok) {
      decisionError.value = {
        code: 'decide_' + result.reasonCode,
        message: '审批没有生效（' + result.reasonCode + '）：' + (result.decision || '服务端未接受这次决定') + '。'
      }
      markStale(approval.id, decideReasonText(result.reasonCode))
      return
    }
    // 成功体必须与本地意图一致：错配不能显示成"已批准"。
    const expected = approve ? 'approve' : 'reject'
    if (result.decision !== expected) {
      decisionError.value = {
        code: 'decide_mismatch',
        message: '服务端回执与本次决定不一致（发送 ' + expected + '，回执 ' + result.decision + '），请重新读取队列确认。'
      }
      markStale(approval.id, '回执与本次决定不一致。')
      return
    }
    approvals.value = approvals.value.filter((item) => item.id !== approval.id)
    decisionNotice.value = '已提交决定：' + (approve ? '批准' : '拒绝') + '。审批通过只表示同意执行，不代表工具已经成功。'
    scheduler.value?.kick()
  } catch (cause) {
    // 同上：换了令牌/服务之后，旧决定请求的 401 不属于当前会话。
    if (!ownedBy(epoch, client)) return
    if (reportAuthFailure(cause)) return
    const ui = toUiError(cause)
    const reason = cause instanceof ZbaRequestError ? cause.reason : undefined
    // 网络中断、5xx、2xx 回执读不懂：决定可能已经生效，不能写成"未接受"。
    const outcomeUnknown = cause instanceof ZbaNetworkError
      || (cause instanceof ZbaRequestError && (cause.status >= 500 || (cause.status >= 200 && cause.status < 300)))
    const reasonText = outcomeUnknown
      ? '这次决定的结果未知，队列已重新读取，请以队列为准再操作。'
      : decideReasonText(reason)
    decisionError.value = { ...ui, message: ui.message + reasonText }
    markStale(approval.id, reasonText)
  } finally {
    approvalBusy.value = approvalBusy.value.filter((id) => id !== approval.id)
    // 失败/过期/已决定都补读独立队列；成功路径也已 kick 调度器。
    await loadApprovals()
  }
}

function markStale(id: string, reason: string): void {
  staleIds.add(id)
  staleReasons.set(id, reason)
  approvals.value = approvals.value.map((row) => (row.id === id
    ? { ...row, stale: true, staleReason: reason }
    : row))
}

/**
 * 提交任务：幂等键在提交前生成并冻结，结果未知时用它核对，
 * 而不是换新键重提（换键等于让服务端再执行一次）。
 */
type SubmissionPhase = 'preparing' | 'submitting' | 'unknown' | 'accepted' | 'rejected'

interface SubmissionRecord {
  /** 稳定本地 id，只用于 v-for key 与 testid 索引。 */
  id: string
  /**
   * 登记处句柄：这条关联在 pending-intent 里的身份。
   *
   * 归属登记处而不是本组件——锁定会把这一页整个卸载，而那次 fetch 还在飞行中；
   * 迟到结果必须写回同一条关联，新挂载的页面才看得见（R1）。
   */
  token: number | null
  key: string
  sourceKey: string
  phase: SubmissionPhase
  taskId: string | null
  findCode: ZbaTaskFindCode | null
  /** 核对回包不可信/核对本身失败时的说明（不冒充任何结论）。 */
  findProblem: string | null
  error: UiError | null
  busy: boolean
  /** 从上一会话恢复：不含任务内容，只允许只读核对。 */
  restored: boolean
  createdAt: string
}

const submissions = ref<SubmissionRecord[]>([])
const prompt = ref('')
const sessionId = ref('')
const sourceKey = ref('')
const timeoutSeconds = ref(120)
const submitBlockedNote = ref<string | null>(null)
const restartArmed = ref<string | null>(null)

const canFillForm = computed(() => canWrite.value)
// 会话 ID 由 ensureSession 按会话来源分配并回填，不是提交的必填项。
const formFilled = computed(() => prompt.value.trim().length > 0
  && sourceKey.value.trim().length > 0)

/**
 * 本服务 + 本身份下当前占用提交入口的关联（在途或结果未知）。
 *
 * 这是**登记处**的读法，不是翻本页的 submissions：请求可能由上一个已经卸载的
 * 组件实例发出（R1 的时序就是"新页面先挂载、旧请求后失败"）。登记处是响应式的，
 * 所以无论谁先出现，这里都能立刻看到并禁用主提交入口。
 */
const livePending = computed(() => activePending(
  serviceLabel(props.client),
  props.client.identityLabel
))

/** 未决提交：还没能证明它到底有没有被受理。 */
const unresolved = computed(() => (livePending.value === null ? null : {
  key: livePending.value.key
}))

/**
 * 未决容量是否已满。
 *
 * 满额时不能再登记：每一条未决关联都可能已被服务端受理，静默挤掉任何一条都会
 * 重新打开"同一意图换键重提"的入口（CAPACITY-M1）。这时**显式阻止**提交入口。
 */
const capacity = computed(() => pendingCapacity(serviceLabel(props.client), props.client.identityLabel))
const capacityFull = computed(() => capacity.value.active >= capacity.value.limit)
const capacityNote = computed(() => '还有 ' + capacity.value.active + ' 条提交的结果没核对清楚（上限 '
  + capacity.value.limit + ' 条），新的提交已经停用。请先核对或放弃其中一条；'
  + '直接换键重提会让服务端再执行一次。')

const canSubmit = computed(() => canWrite.value && formFilled.value
  && livePending.value === null
  && !capacityFull.value
  && !submissions.value.some((item) => item.busy))

/**
 * 登记处的阶段 → 本页那一行的阶段。
 *
 * 四种都要映射（M2）：终态（已受理/明确拒绝）同样要传播到恢复出来的那一行，
 * 否则它会永远停在"正在等待服务端受理…"，核对与收起入口都不出现。
 */
function phaseFromPending(pending: PendingPhase): SubmissionPhase {
  switch (pending) {
    case 'in_flight': return 'submitting'
    case 'resolved': return 'accepted'
    case 'rejected': return 'rejected'
    default: return 'unknown'
  }
}

function newIdempotencyKey(): string {
  return 'task1-' + newNonce() + '-' + nowSeconds()
}

function newRecordId(): string {
  return newNonce()
}

/**
 * 服务端明确拒绝 vs 结果未知（S3-04）。
 *
 * 只有服务端**在创建任务之前**回绝的 4xx 才能说"没有产生任务"。
 * 下面几类都不能：
 * - 5xx（代理/网关/服务端内部）：请求可能已经到达并受理，只是回执丢了；
 * - 网络中断/超时：同上；
 * - 2xx 却读不懂的成功体：受理回执丢了，任务可能已经建好；
 * - `idempotency_unverifiable`（409）：持久回执证明这个键**曾经被受理**，
 *   只是已经无法核对——换键重提就是第二次执行。
 */
const UNVERIFIABLE_CODES = new Set(['idempotency_unverifiable', 'invalid_json'])
const AMBIGUOUS_STATUSES = new Set([408, 409, 429])

function classifySubmitFailure(cause: unknown): SubmissionPhase {
  if (cause instanceof ZbaRequestError) {
    if (cause.status >= 500) return 'unknown'
    if (cause.status >= 200 && cause.status < 300) return 'unknown'
    if (UNVERIFIABLE_CODES.has(cause.code)) return 'unknown'
    // 408 超时、409 冲突、429 限流都不能证明服务端没有建任务（可能是网关/重放层回的）。
    if (AMBIGUOUS_STATUSES.has(cause.status)) return 'unknown'
    if (cause.status >= 400 && cause.status < 500) return 'rejected'
    return 'unknown'
  }
  // ZbaNetworkError（超时、连接重置）与任何其它异常：请求可能已到达服务端。
  return 'unknown'
}

async function submit(): Promise<void> {
  if (!canSubmit.value) {
    if (unresolved.value) {
      submitBlockedNote.value = '还有一次提交的结果没核对清楚（幂等键 ' + unresolved.value.key
        + '）。请先核对；换键重提会让服务端再执行一次。'
    } else if (capacityFull.value) {
      submitBlockedNote.value = capacityNote.value
    }
    return
  }
  const client = props.client
  const epoch = ownership
  const key = newIdempotencyKey()
  // 快照本次意图：异步流程里 prompt 可能被继续编辑。
  const promptText = prompt.value
  let requestSent = false
  // 记录必须用 reactive 包一层：异步流程持有的是这个代理，改字段才会驱动界面。
  // 发送前登记：从这一刻起这个幂等键就占用提交入口，即使本页随后被卸载。
  //
  // 登记可能因为**未决容量满**而失败（CAPACITY-M1）：未决记录永不淘汰，所以这里
  // 宁可拒绝这次提交并如实说明，也不能挤掉别的、仍可能已被服务端受理的关联。
  let pending: ReturnType<typeof beginPending>
  try {
    pending = beginPending({
      key,
      sourceKey: sourceKey.value.trim(),
      service: serviceLabel(client),
      identity: client.identityLabel,
      bornOf: epoch
    })
  } catch (cause) {
    if (cause instanceof PendingCapacityError) {
      submitBlockedNote.value = '还有 ' + cause.active + ' 条提交的结果没核对清楚（上限 '
        + cause.limit + ' 条），本次没有发出。请先核对或放弃其中一条；换键重提会让服务端再执行一次。'
      return
    }
    throw cause
  }
  const record = reactive<SubmissionRecord>({
    id: newRecordId(),
    token: pending.token,
    key,
    sourceKey: sourceKey.value.trim(),
    phase: 'preparing',
    taskId: null,
    findCode: null,
    findProblem: null,
    error: null,
    busy: true,
    restored: false,
    createdAt: new Date().toISOString()
  })
  submissions.value = [record, ...submissions.value].slice(0, 8)
  submitBlockedNote.value = null
  restartArmed.value = null
  try {
    if (!ownedBy(epoch, client)) {
      record.phase = 'rejected'
      record.error = { code: 'aborted_client', message: '已经离开任务页或权限发生变化，这次提交没有发送。' }
      return
    }
    record.phase = 'submitting'
    // 后端要求会话先存在：不存在时提交直接 404 session_not_found。
    // ensure 只保证「当前有一个 server 会话」，不产生任务，也不消耗幂等键。
    const ensured = await client.ensureSession(record.sourceKey)
    // 从这里开始，请求才真正可能创建任务；此前的失败都能确定"没有任务"。
    requestSent = true
    if (!ownedBy(epoch, client)) {
      record.phase = 'rejected'
      record.error = { code: 'aborted_client', message: '已经离开任务页或权限发生变化，这次提交没有发送。' }
      return
    }
    sessionId.value = ensured.session.id
    // 任务 POST 真的要离开浏览器了：登记处据此把措辞从"准备中"改成"已发出"。
    markPendingSent(pending.token)
    const accepted = await client.submitTask({
      sourceKey: record.sourceKey,
      sessionId: ensured.session.id,
      prompt: promptText,
      target: 'server',
      timeoutSeconds: timeoutSeconds.value
    }, key)
    record.taskId = accepted.id
    record.phase = 'accepted'
    // 受理成功才清空输入：失败时保留原意图，用户要能用同一个键核对。
    // 登记处的结算统一在 finally 里按 record.phase 做（所有退出路径都要结算）。
    prompt.value = ''
    await load(undefined, 'background')
    scheduler.value?.kick()
  } catch (cause) {
    // 没走到提交端点就失败：可以确定这次没有产生任务。
    record.phase = requestSent ? classifySubmitFailure(cause) : 'rejected'
    record.error = toUiError(cause)
    if (!ownedBy(epoch, client)) return
    reportAuthFailure(cause)
  } finally {
    // 结算登记处：每条退出路径都要做，包括"还没发出就中止"的那些——否则一条
    // in_flight 记录会永远占着提交入口。这里**不看所有权代数**：组件可以消失，
    // 关联不能因此失去记录（R1 的失败正来自"新页面先挂载、旧请求后失败"）。
    if (record.phase === 'accepted') {
      settlePending(pending.token, 'resolved', record.taskId)
    } else if (record.phase === 'unknown') {
      leavePendingUnknown(pending.token, { taskId: record.taskId })
    } else {
      settlePending(pending.token, 'rejected')
    }
    record.busy = false
  }
}

/** 用提交时的幂等键核对：不换键、不重提。 */
async function findSubmission(record: SubmissionRecord): Promise<void> {
  if (record.busy) return
  if (!canWrite.value) {
    record.findProblem = '核对是 admin 路由：请先进入管理员模式再核对。'
    return
  }
  record.busy = true
  record.error = null
  record.findProblem = null
  // 发起时固定会话代际：换身份/换服务后迟到的核对结果不能写进新会话。
  const owner = ownership
  // 已受理是终局结论：之后的 expired/unknown 只说明回执过了保留期，不能把它降级回未知。
  const wasAccepted = record.phase === 'accepted'
  try {
    const result = await props.client.findTask(record.key)
    if (owner !== ownership) return
    record.findCode = result.code
    record.taskId = result.task?.id ?? result.taskId ?? record.taskId
    if (result.code === 'found') {
      // 拿到稳定 id：这条关联已经解决，不再占用未决名额。
      record.phase = 'accepted'
      const live = pendingByKey(record.key)
      if (live !== null) settlePending(live.token, 'resolved', record.taskId)
      await load(undefined, 'background')
      scheduler.value?.kick()
    } else if (wasAccepted) {
      // 已受理的行只更新核对码，不重新占用未决名额。
    } else {
      // expired：持久回执证明**曾经受理**（记录已被内存台账淘汰）；
      // accepted_unknown / unknown：都无法证明没有执行。
      // 三者都不能降级成"没有产生任务"，继续保持未知并保留原键。
      record.phase = 'unknown'
      // 恢复出来的行也要能写回登记处：按 key 取当前记录，不依赖行内的 token。
      const live = pendingByKey(record.key)
      if (live !== null) {
        leavePendingUnknown(live.token, { taskId: record.taskId, findCode: result.code })
      }
    }
  } catch (cause) {
    if (owner !== ownership) return
    if (reportAuthFailure(cause)) return
    const ui = toUiError(cause)
    record.error = ui
    // 回包不可信（含畸形 found）时仍要给出核对结论的位置，但不能冒充任何码。
    record.findProblem = ui.code === 'invalid_json'
      ? '核对回包无法验证（' + ui.code + '），结果仍然是未知；不能用它证明任务没有执行。'
      : '核对没有完成（' + ui.code + '），结果仍然是未知。'
  } finally {
    record.busy = false
  }
}

/**
 * 显式重新执行：这是**新的用户意图**，会生成新的幂等键，服务端可能再执行一次。
 * 必须两步确认，且明确写出副作用风险（S3-01）。
 */
function armRestart(record: SubmissionRecord): void {
  restartArmed.value = restartArmed.value === record.id ? null : record.id
}

function confirmRestart(record: SubmissionRecord): void {
  restartArmed.value = null
  // 用户明确接受"可能再执行一次"：这条关联不再占用入口，也不需要再传播结论。
  // 行可能是别的服务实例登记/恢复出来的，按当前登记处里的同键记录丢弃。
  discardPending(record.key)
  submissions.value = submissions.value.filter((item) => item.id !== record.id)
  submitBlockedNote.value = '已放弃原关联。再次提交会生成新的幂等键，服务端可能再执行一次同样的副作用。'
}

function dismiss(record: SubmissionRecord): void {
  if (record.phase === 'unknown' || record.phase === 'submitting') return
  // 收起 = 用户看过结论了：登记处那条终态记录也一并丢弃，避免行被重新恢复出来。
  discardPending(record.key)
  submissions.value = submissions.value.filter((item) => item.id !== record.id)
}

/**
 * 把登记处里本服务 + 本身份的未决关联映射成本页的提交记录。
 *
 * 两件事一起解决：
 * - 重新挂载后恢复最小关联（S3-05）：只有键与任务 id，没有 prompt/请求体；
 * - 新组件先挂载、旧请求后失败（R1）：登记处是响应式的，这个函数由 watch 驱动，
 *   所以迟到的失败会把已经在页面上的那一行降级成"结果未知"，而不是无人知晓。
 */
function syncSubmissionsWithRegistry(): void {
  const service = serviceLabel(props.client)
  const identity = props.client.identityLabel
  for (const pending of pendingEntries()) {
    if (pending.service !== service || pending.identity !== identity) continue
    const existing = submissions.value.find((item) => item.key === pending.key)
    if (existing) {
      // 已被本组件持有的行：跟随登记处更新阶段与已知任务 id。
      //
      // 终态也要跟随（M2）：旧组件实例已经结算，本页的恢复行必须收到"已受理/明确
      // 拒绝"才算结束。但正在提交中的那一行有自己的 in-flight 生命周期，不能被
      // 一条更旧的终态记录覆盖。
      if (!existing.busy && existing.token === pending.token) {
        existing.phase = phaseFromPending(pending.phase)
        existing.taskId = pending.taskId ?? existing.taskId
        existing.findCode = pending.findCode ?? existing.findCode
        existing.createdAt = pending.savedAt
      }
      continue
    }
    // 终态记录也能建行：锁定发生在结算之后、重连挂载时，结论仍要显示出来。
    const record = reactive<SubmissionRecord>({
      id: newRecordId(),
      token: pending.token,
      key: pending.key,
      sourceKey: pending.sourceKey,
      phase: phaseFromPending(pending.phase),
      taskId: pending.taskId,
      findCode: pending.findCode,
      findProblem: null,
      error: null,
      busy: false,
      restored: true,
      createdAt: pending.savedAt
    })
    submissions.value = [record, ...submissions.value]
  }
  // 合并后一次排序与裁剪：未决/在途行优先，终态只能占剩余展示位。
  // 登记处容量与展示容量彼此独立，不能让旧终态遮住仍需核对的幂等键。
  const byKey = new Map<string, SubmissionRecord>()
  for (const record of submissions.value) byKey.set(record.key, record)
  const merged = [...byKey.values()]
  const isActiveRecord = (record: SubmissionRecord) => record.busy || record.phase === 'unknown' || record.phase === 'submitting' || record.phase === 'preparing'
  merged.sort((left, right) => {
    const activeOrder = Number(isActiveRecord(right)) - Number(isActiveRecord(left))
    if (activeOrder !== 0) return activeOrder
    return Date.parse(right.createdAt) - Date.parse(left.createdAt)
  })
  submissions.value = merged.slice(0, 8)
  // 登记处已经不再占用的行留给本地状态显示结论（已受理/已拒绝），不动它。
}

function resetForSession(): void {
  ownership += 1
  // 旧 client 的读取结果一律作废：推进三条代数并释放 loading。
  loadSequence += 1
  approvalSequence += 1
  detailSequence += 1
  loading.value = false
  detailBusy.value = false
  tasks.value = []
  nextCursor.value = null
  approvals.value = []
  approvalsLoaded.value = false
  detail.value = null
  detailError.value = null
  approvalError.value = null
  decisionError.value = null
  decisionNotice.value = null
  submissions.value = []
  submitBlockedNote.value = null
  restartArmed.value = null
  cancelMarks.clear()
  // 旧会话的"未生效"标记属于旧身份，不能带进新会话。
  staleIds.clear()
  staleReasons.clear()
  error.value = null
  pageError.value = null
  lastLoadedAt.value = null
  // 换身份/换服务后重新从登记处取一次：旧身份的记录取不到，也就不会外发。
  syncSubmissionsWithRegistry()
  void load()
  void loadApprovals()
}

// ── 共享事件调度（S3-06） ─────────────────────────────────────────────
const scheduler = ref<EventScheduler | null>(null)
let detachScheduler: (() => void) | null = null

function attachScheduler(client: ZbaClient): void {
  detachScheduler?.()
  const { scheduler: shared, release } = acquireScheduler(client)
  scheduler.value = shared
  const offTasks = shared.subscribe({
    name: 'tasks',
    matches: (event) => event.type === 'task.updated',
    // 事件驱动的补读是后台行为：旧数据留在屏幕上，成功后整份替换。
    // 读取失败必须**抛给调度器**：自己吞掉会让它误签"已追平"并推进游标（R2）。
    reload: async () => {
      // 本页已经卸载时，即使调度器把这条订阅执行到了这里也不该再读（LIFE-02）。
      if (unmounted) return
      if (!(await load(undefined, 'background'))) throw new Error('tasks_read_failed')
    }
  })
  const offApprovals = shared.subscribe({
    name: 'approvals',
    matches: (event) => event.type === 'approval.decided'
      || (event.type === 'task.updated' && event.state === 'waiting_approval'),
    reload: async () => {
      if (unmounted) return
      if (!(await loadApprovals())) throw new Error('approvals_read_failed')
    }
  })
  // 计划 §9.3 第 240 行：resync 要覆盖 status、配置、任务、审批与受影响能力。
  // 这里接上另外三类只读探测的订阅；它们和任务/审批共用同一个 events 轮询。
  const offResources = registerAppResources(shared, client)
  const offSnapshot = shared.onSnapshot((snapshot) => { syncSnapshot.value = snapshot })
  detachScheduler = () => {
    offTasks()
    offApprovals()
    offResources()
    offSnapshot()
    release()
    scheduler.value = null
  }
}

let boundClient = props.client
watch(() => props.client, (value) => {
  if (value === boundClient) return
  boundClient = value
  attachScheduler(value)
  resetForSession()
})

/** 离开视图、退出管理员、写能力失效都让在途写请求永久失效。 */
function abandonInFlight(): void {
  ownership += 1
}

watch(() => props.active, (value) => { if (value === false) abandonInFlight() })
watch(() => props.mode, (value) => { if (value !== 'admin') abandonInFlight() })
watch(() => props.writesEnabled, (value) => { if (!value) abandonInFlight() })

/** 用户显式触发的读取：撤下旧快照再读，失败把旧快照放回去。 */
async function reloadAll(): Promise<void> {
  await load(undefined, 'interactive')
  await loadApprovals()
}

/**
 * 登记处是模块级的，本页只是它的一个视图：任何时刻的变化都要反映到界面上。
 *
 * R1 的时序正是「新组件先挂载 → 旧请求后失败」，只有订阅登记处才能看到那一步。
 */
watch(pendingEntries, () => { syncSubmissionsWithRegistry() })

onMounted(() => {
  attachScheduler(props.client)
  syncSubmissionsWithRegistry()
  void load()
  void loadApprovals()
})

onBeforeUnmount(() => {
  unmounted = true
  ownership += 1
  // 只读序列同样要作废：在途的 listTasks/approvals/result 迟到时不得回填已清空的 refs
  // （LIFE-03）。推进代数后，它们的 finally 也不会再动下一代 loading。
  loadSequence += 1
  approvalSequence += 1
  detailSequence += 1
  loading.value = false
  detailBusy.value = false
  detachScheduler?.()
  detachScheduler = null
  // 未决提交的最小关联信息留在 pending-intent 里（内存），
  // prompt/结果/请求体一律不保留。
  tasks.value = []
  approvals.value = []
  detail.value = null
})

defineExpose({
  reload: reloadAll,
  describe: () => ({ tasks: tasks.value.length, approvals: approvals.value.length })
})
</script>

<template>
  <section class="tasks" data-testid="tasks">
    <header class="page-heading">
      <div>
        <p class="eyebrow">任务</p>
        <h1 id="tasks-title">Agent 任务与审批</h1>
        <p class="subtitle">
          进行中 {{ activeTasks.length }} · 待审批 {{ pendingApprovals.length }}
          <template v-if="lastLoadedAt"> · 读取于 {{ formatTime(lastLoadedAt) }}</template>
        </p>
      </div>
      <div class="heading-actions">
        <span class="mode-badge" :data-mode="props.mode" data-testid="task-mode-badge">
          {{ props.mode === 'admin' ? '管理员模式' : '标准模式' }}
        </span>
        <span
          class="status-badge"
          :data-tone="syncSnapshot.state === 'live' ? 'ok' : syncSnapshot.state === 'idle' ? 'neutral' : 'warn'"
          data-testid="task-sync-state"
        >
          {{ syncBadgeText }}
        </span>
        <button type="button" class="tonal" data-testid="task-reload" :disabled="loading" @click="reloadAll">
          {{ loading ? '读取中…' : '重新读取' }}
        </button>
      </div>
    </header>

    <p v-if="syncSnapshot.state === 'stale'" class="inline-notice warn" data-testid="task-sync-stale">
      事件同步没有追平（连续失败 {{ syncSnapshot.failures }} 次）；
      <template v-if="syncSnapshot.failedResource">
        资源「{{ syncSnapshot.failedResource }}」重读失败，下面显示的是上一次成功读取的数据。
      </template>
      <template v-else>下面显示的是上一次成功读取的数据。</template>
    </p>

    <p v-if="props.mode !== 'admin'" class="inline-notice" data-testid="task-readonly">
      标准模式可以查看任务与待审批；提交任务、取消、审批与按幂等键核对都需要先进入管理员模式。
      <button
        type="button"
        class="tonal inline-action"
        data-testid="task-enable-admin"
        :disabled="!props.writesEnabled"
        @click="emit('enable-admin')"
      >
        进入管理员模式
      </button>
    </p>
    <p v-else-if="!props.writesEnabled" class="inline-notice bad" data-testid="task-write-disabled">
      契约不兼容，写入口已禁用；本页只展示当前任务。
    </p>

    <ErrorNotice :error="error" title="读取任务失败" />
    <ErrorNotice :error="pageError" title="加载下一页失败" />

    <div class="task-workspace">
      <section class="panel task-composer" data-testid="task-submit-panel" aria-labelledby="task-composer-title">
        <div class="panel-heading">
          <p class="section-eyebrow">任务编排</p>
          <h2 id="task-composer-title">提交任务</h2>
          <p class="config-guide">把要交给后台 Agent 的工作写在这里，提交后在执行记录中跟进。与群里的 // 指令效果相同。</p>
        </div>
        <div class="task-form">
          <label>
            <span>会话来源</span>
            <input v-model="sourceKey" data-testid="task-source-key" placeholder="panel:synthetic" :disabled="!canFillForm" />
          </label>
          <label>
            <span>会话 ID</span>
            <input v-model="sessionId" data-testid="task-session-id" placeholder="synthetic-session" aria-describedby="task-session-hint" :disabled="!canFillForm" />
            <small id="task-session-hint" class="field-hint">服务端按来源分配，提交后回填。</small>
          </label>
          <label class="task-form-wide">
            <span>任务内容</span>
            <textarea v-model="prompt" rows="4" data-testid="task-prompt" placeholder="描述目标、必要的上下文和预期结果…" :disabled="!canFillForm" />
          </label>
          <label class="task-timeout-field">
            <span>超时（秒）</span>
            <input v-model.number="timeoutSeconds" type="number" min="10" max="900" data-testid="task-timeout" aria-describedby="task-timeout-hint" :disabled="!canFillForm" />
            <small id="task-timeout-hint" class="field-hint">10–900 秒</small>
          </label>
        </div>
        <div class="task-compose-footer">
          <p class="task-submit-hint">幂等键在提交前生成并冻结。结果未知时请先核对，换键重提会再次执行。</p>
          <div class="dialog-actions">
            <button type="button" class="primary" data-testid="task-submit" :disabled="!canSubmit" @click="submit">
              {{ unresolved ? '先核对上一次提交' : submissions.some((item) => item.busy) ? '提交中…' : '提交任务' }}
            </button>
          </div>
        </div>
        <p v-if="capacityFull" class="inline-notice warn" data-testid="task-submit-capacity">
          {{ capacityNote }}
        </p>
        <p v-if="submitBlockedNote" class="inline-notice warn" data-testid="task-submit-blocked" role="status">
          {{ submitBlockedNote }}
        </p>

        <div
          v-for="(submission, index) in submissions"
          :key="submission.id"
          class="task-submission"
          data-testid="task-submission"
        >
          <p
            class="inline-notice"
            :class="{ warn: submission.phase === 'unknown' }"
            data-testid="task-submission-state"
          >
            <template v-if="submission.phase === 'preparing'">正在准备提交…</template>
            <template v-else-if="submission.phase === 'submitting'">已提交，正在等待服务端受理…</template>
            <template v-else-if="submission.phase === 'unknown'">
              没有拿到可信的受理回执：结果未知，幂等键已保留。请先按原键核对；换键重提会让服务端再执行一次。
            </template>
            <template v-else-if="submission.phase === 'accepted'">已受理{{ submission.taskId ? '（任务 ' + submission.taskId + '）' : '' }}。</template>
            <template v-else>服务端明确拒绝，这次没有产生任务。</template>
          </p>
          <p v-if="submission.restored" class="inline-notice" data-testid="task-submission-restored">
            这是上一个会话留下的未决提交：只保留了幂等键与任务 id，任务内容与请求体没有保留。
          </p>
          <p v-if="submission.findCode || submission.findProblem" class="inline-notice" data-testid="task-find-code">
            <template v-if="submission.findCode">核对结果：{{ findCodeLabels[submission.findCode] }}</template>
            <template v-else>核对结果：{{ submission.findProblem }}</template>
          </p>
          <ErrorNotice :error="submission.error" title="提交没有完成" />
          <div class="dialog-actions">
            <button
              v-if="submission.phase === 'unknown' || submission.phase === 'accepted'"
              type="button"
              class="tonal"
              data-testid="task-find"
              :disabled="submission.busy || !canWrite"
              @click="findSubmission(submission)"
            >
              {{ submission.busy ? '核对中…' : '核对受理结果' }}
            </button>
            <button
              v-if="submission.phase === 'unknown'"
              type="button"
              class="tonal"
              :data-testid="'task-restart-' + index"
              :disabled="!canWrite"
              @click="armRestart(submission)"
            >
              作为新任务重新提交…
            </button>
            <button
              v-if="submission.phase !== 'unknown' && submission.phase !== 'submitting'"
              type="button"
              class="link-button"
              data-testid="task-submission-dismiss"
              @click="dismiss(submission)"
            >
              收起
            </button>
          </div>
          <div
            v-if="restartArmed === submission.id"
            class="inline-notice bad"
            :data-testid="'task-restart-warning-' + index"
          >
            <p>
              重新执行是「新的一次提交」：会生成新的幂等键，服务端可能再执行一次相同的工具副作用。
              原幂等键 <code>{{ submission.key }}</code> 的核对记录会被放弃。
            </p>
            <button
              type="button"
              class="primary"
              :data-testid="'task-restart-confirm-' + index"
              :disabled="!canWrite"
              @click="confirmRestart(submission)"
            >
              我明白，按新任务重新提交
            </button>
          </div>
          <p class="cap-op-key">幂等键 <code data-testid="task-idempotency-key">{{ submission.key }}</code></p>
        </div>
      </section>

      <section class="panel task-approvals" data-testid="task-approvals" aria-labelledby="task-approvals-title">
        <div class="panel-heading">
          <p class="section-eyebrow">人工确认</p>
          <div class="task-section-title">
            <h2 id="task-approvals-title">待审批</h2>
            <span v-if="approvalsLoaded && !approvalError" class="status-badge" :data-tone="pendingApprovals.length ? 'warn' : 'neutral'">{{ pendingApprovals.length }} 项待处理</span>
          </div>
          <p class="config-guide">改文件、执行命令等风险操作会在此等待确认。批准后才继续，拒绝或过期不会执行。</p>
          <p class="field-hint">摘要已脱敏；每次决定都绑定审批 ID。</p>
        </div>
        <ErrorNotice :error="approvalError" title="读取待审批失败" />
        <ErrorNotice :error="decisionError" title="审批没有完成" />
        <p v-if="decisionNotice" class="inline-notice" data-testid="task-decision-notice" role="status">{{ decisionNotice }}</p>
        <p
          v-if="approvals.length === 0 && approvalsLoaded && !approvalError"
          class="empty-state"
          data-testid="task-approvals-empty"
        >
          当前没有待审批请求。
        </p>
        <p v-else-if="approvals.length === 0 && !approvalError" class="empty-state" data-testid="task-approvals-unknown">
          还没读到待审批队列。
        </p>
        <ul v-else class="task-approval-list">
          <li
            v-for="(approval, index) in approvals"
            :key="approval.id"
            :data-testid="'task-approval-' + index"
            :class="{ 'task-approval-stale': approval.stale }"
          >
            <div class="task-approval-head">
              <strong>{{ approval.toolId }}</strong>
              <span v-if="approval.stale" class="status-badge" data-tone="bad" :data-testid="'task-approval-stale-' + index">
                未生效
              </span>
              <span v-else class="status-badge" data-tone="warn">{{ formatTime(approval.expiresAt) }} 过期</span>
            </div>
            <p class="task-approval-summary">{{ approval.summary }}</p>
            <p class="task-approval-meta">
              会话 {{ approval.session }} · 策略版本 {{ approval.policyVersion }}
              <template v-if="approval.stale"> · {{ approval.staleReason }}</template>
            </p>
            <div class="dialog-actions">
              <button
                type="button"
                class="primary"


              :data-testid="'task-approve-' + index"
              :disabled="!canWrite || approval.stale || approvalBusy.includes(approval.id)"
              @click="decide(approval, true)"
            >
              批准
            </button>
            <button
              type="button"
              class="tonal"
              :data-testid="'task-reject-' + index"
              :disabled="!canWrite || approval.stale || approvalBusy.includes(approval.id)"
              @click="decide(approval, false)"
            >
              拒绝
            </button>
          </div>
        </li>
      </ul>
    </section>

    </div>

    <section class="panel task-history" aria-labelledby="task-history-title">
      <div class="task-history-heading">
        <div>
          <p class="section-eyebrow">执行记录</p>
          <h2 id="task-history-title">任务历史</h2>
        </div>
        <p class="task-history-meta">当前已载入 {{ tasks.length }} 项 · 进行中 {{ activeTasks.length }}</p>
      </div>
    <p v-if="loading && tasks.length === 0" class="empty-state" data-testid="task-loading">正在读取任务…</p>
    <p v-else-if="!error && tasks.length === 0" class="empty-state" data-testid="task-empty">还没有任务。</p>
    <div v-else class="task-list">
      <article
        v-for="task in tasks"
        :key="task.id"
        class="task-item"
        :data-testid="'task-item-' + task.id"
      >
        <header class="task-item-head">
          <div>
            <p class="cap-kind">{{ task.target }}</p>
            <h3 class="task-id">{{ task.id }}</h3>
            <p class="cap-key">会话 {{ task.sessionId }}</p>
          </div>
          <span class="status-badge" :data-tone="stateTone(task.state)" data-testid="task-state">
            {{ stateLabel(task.state) }}
          </span>
        </header>
        <dl class="cap-meta">
          <div><dt>创建</dt><dd>{{ formatTime(task.createdAt) }}</dd></div>
          <div><dt>更新</dt><dd>{{ formatTime(task.updatedAt) }}</dd></div>
          <div v-if="task.toolCalls !== undefined"><dt>工具调用</dt><dd>{{ task.toolCalls }}</dd></div>
          <div v-if="task.errorCode"><dt>错误</dt><dd class="cap-error-code">{{ task.errorCode }}</dd></div>
          <div v-if="task.cancellationRequested"><dt>取消</dt><dd>{{ isTerminal(task.state) ? '已请求（任务已结束，以状态为准）' : '已请求（未确认）' }}</dd></div>
        </dl>
        <div class="dialog-actions">
          <button type="button" class="tonal" :data-testid="'task-result-' + task.id" @click="openDetail(task)">
            查看结果
          </button>
          <button
            v-if="!isTerminal(task.state)"
            type="button"
            class="tonal"
            :data-testid="'task-cancel-' + task.id"
            :disabled="!canWrite || detailBusy"
            @click="cancelTask(task)"
          >
            请求取消
          </button>
        </div>
      </article>
    </div>

    <div v-if="nextCursor" class="cap-more">
      <button type="button" class="tonal" data-testid="task-load-more" :disabled="loading" @click="loadMore">
        {{ loading ? '加载中…' : '加载下一页' }}
      </button>
    </div>

    </section>

    <section v-if="detail || detailError" class="panel task-detail" data-testid="task-detail" aria-labelledby="task-detail-title">
      <div class="panel-heading">
        <p class="section-eyebrow">执行输出</p>
        <div class="task-section-title">
          <h2 id="task-detail-title">任务结果</h2>
          <span v-if="detail" class="status-badge" :data-tone="stateTone(detail.state)">{{ stateLabel(detail.state) }}</span>
        </div>
        <p v-if="detail" class="task-result-id">任务 {{ detail.id }}</p>
      </div>
      <ErrorNotice :error="detailError" title="读取结果失败" />
      <template v-if="detail">
        <p v-if="!detail.available" class="inline-notice warn" data-testid="task-result-unavailable">
          结果不可用{{ detail.reasonCode ? '（' + detail.reasonCode + '）' : '' }}；任务状态仍以列表为准。
        </p>
        <template v-else>
          <p class="task-summary" data-testid="task-result-summary">{{ detail.summary }}</p>
          <p v-if="detail.summaryTruncated" class="inline-notice" data-testid="task-result-truncated">
            摘要已截断，共 {{ detail.summaryChars }} 字。
          </p>
        </template>
        <dl class="cap-meta">
          <div><dt>更新</dt><dd>{{ formatTime(detail.updatedAt) }}</dd></div>
          <div v-if="detail.toolCalls !== undefined"><dt>工具调用</dt><dd>{{ detail.toolCalls }}</dd></div>
          <div v-if="detail.errorCode"><dt>错误</dt><dd class="cap-error-code">{{ detail.errorCode }}</dd></div>
        </dl>
      </template>
      <button type="button" class="link-button" data-testid="task-detail-close" @click="detail = null; detailError = null">
        收起
      </button>
    </section>
  </section>
</template>

<style scoped>
.tasks { display: flex; flex-direction: column; gap: 20px; min-width: 0; }
.tasks .panel { min-width: 0; margin: 0; padding: 24px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface); box-shadow: none; }
.heading-actions { flex-wrap: wrap; }
.task-workspace { display: grid; grid-template-columns: minmax(0, 1fr); gap: 20px; align-items: start; }
.tasks .task-composer { border-top: 2px solid var(--accent-deep); }
.tasks .panel-heading { display: grid; gap: 8px; margin: 0 0 20px; }
.tasks .panel-heading h2, .task-history-heading h2 { margin: 0; color: var(--text); font-size: 18px; font-weight: 650; letter-spacing: -.025em; line-height: 1.4; }
.tasks .panel-heading p { margin: 0; color: var(--text-muted); font-size: 13px; line-height: 1.65; }
.tasks .section-eyebrow { margin: 0 0 4px; color: var(--text-soft); font-size: 11px; font-weight: 650; line-height: 1.4; letter-spacing: .08em; }
.task-section-title { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 8px 12px; }
.task-form { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 18px 16px; margin: 0; }
.task-form label { display: flex; flex-direction: column; gap: 7px; min-width: 0; font-size: 13px; }
.task-form label > span { color: var(--text); font-weight: 550; }
.task-form input, .task-form textarea { box-sizing: border-box; width: 100%; min-width: 0; min-height: 36px; padding: 7px 10px; border: 1px solid var(--border-strong); border-radius: 8px; background: var(--surface); color: var(--text); font: inherit; font-size: 14px; line-height: 1.5; }
.task-form textarea { min-height: 126px; resize: vertical; }
.task-form input::placeholder, .task-form textarea::placeholder { color: var(--text-soft); }
.task-form input:disabled, .task-form textarea:disabled { background: var(--surface-muted); color: var(--text-muted); cursor: not-allowed; }
.task-form-wide { grid-column: 1 / -1; }
.task-timeout-field { max-width: 180px; }
.tasks .field-hint { color: var(--text-muted); font-size: 12px; font-weight: 400; line-height: 1.6; }
.task-compose-footer { display: flex; align-items: center; justify-content: space-between; gap: 20px; margin-top: 20px; padding-top: 18px; border-top: 1px solid var(--border); }
.task-submit-hint { flex: 1; max-width: 52ch; margin: 0; color: var(--text-muted); font-size: 12px; line-height: 1.7; }
.tasks .dialog-actions { border-top: 0; padding-top: 0; display: flex; flex-wrap: wrap; justify-content: flex-start; align-items: center; gap: 8px; margin: 0; }
.task-compose-footer .dialog-actions { flex-shrink: 0; }
.tasks button { min-height: 36px; border-radius: 8px; }
.tasks button:focus-visible, .task-form input:focus-visible, .task-form textarea:focus-visible { outline: 2px solid var(--accent-deep); outline-offset: 3px; }
.tasks .inline-notice { max-width: 100%; line-height: 1.7; overflow-wrap: anywhere; }
.tasks .inline-notice button { white-space: normal; }
.task-submission { display: grid; gap: 12px; margin-top: 20px; padding-top: 20px; border-top: 1px solid var(--border); }
.task-submission > .inline-notice { margin: 0; }
.task-submission .cap-op-key { margin: 0; color: var(--text-soft); font-size: 12px; overflow-wrap: anywhere; }
.task-submission code { color: var(--text-muted); font-size: 12px; overflow-wrap: anywhere; }
.task-approval-list { display: grid; gap: 12px; margin: 0; padding: 0; list-style: none; }
.task-approval-list li { display: grid; gap: 12px; min-width: 0; padding: 16px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-muted); }
.task-approval-head { display: flex; align-items: flex-start; justify-content: space-between; flex-wrap: wrap; gap: 8px 12px; }
.task-approval-head strong { min-width: 0; color: var(--text); font-family: var(--font-mono, ui-monospace, monospace); font-size: 13px; font-weight: 600; overflow-wrap: anywhere; }
.task-approval-head .status-badge { white-space: normal; }
.task-approval-summary { margin: 0; color: var(--text); font-size: 14px; line-height: 1.75; overflow-wrap: anywhere; }
.task-approval-meta { margin: 0; color: var(--text-muted); font-size: 12px; line-height: 1.7; overflow-wrap: anywhere; }
.task-approval-list li.task-approval-stale { opacity: 1; border-style: dashed; }
.task-approval-stale .task-approval-head strong { color: var(--text-muted); }
.task-approval-list .dialog-actions { padding-top: 4px; }
.tasks .empty-state { margin: 0; padding: 28px 16px; border: 1px dashed var(--border-strong); border-radius: 8px; background: var(--surface-muted); color: var(--text-muted); text-align: center; font-size: 13px; line-height: 1.7; }
.task-history-heading { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 12px; margin-bottom: 20px; }
.task-history-meta { margin: 0; color: var(--text-muted); font-size: 12px; font-variant-numeric: tabular-nums; }
.task-list { display: grid; gap: 0; margin: 0; }
.task-item { display: grid; grid-template-columns: minmax(0, 1fr) auto; gap: 14px 24px; min-width: 0; padding: 20px 0; border: 0; border-top: 1px solid var(--border); border-radius: 0; background: transparent; box-shadow: none; }
.task-item:first-child { padding-top: 0; border-top: 0; }
.task-item:last-child { padding-bottom: 0; }
.task-item-head { display: flex; grid-column: 1 / -1; align-items: flex-start; justify-content: space-between; gap: 12px; min-width: 0; }
.task-item-head > div { min-width: 0; }
.task-item .cap-kind { margin: 0 0 5px; color: var(--text-soft); font-size: 11px; font-weight: 550; }
.task-id { margin: 0; color: var(--text); font-family: var(--font-mono, ui-monospace, monospace); font-size: 13px; font-weight: 600; line-height: 1.6; overflow-wrap: anywhere; }
.task-item .cap-key { margin: 5px 0 0; color: var(--text-muted); font-size: 12px; line-height: 1.6; overflow-wrap: anywhere; }
.tasks .cap-meta { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 12px 24px; min-width: 0; margin: 0; }
.tasks .cap-meta > div { display: grid; gap: 4px; min-width: 0; }
.tasks .cap-meta dt { color: var(--text-soft); font-size: 11px; }
.tasks .cap-meta dd { margin: 0; color: var(--text-muted); font-size: 12px; line-height: 1.6; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.tasks .cap-meta .cap-error-code { color: var(--bad-text); font-family: var(--font-mono, ui-monospace, monospace); }
.task-item > .dialog-actions { align-self: end; justify-content: flex-end; }
.task-history .cap-more { margin: 20px 0 0; padding-top: 20px; border-top: 1px solid var(--border); text-align: center; }
.tasks .task-detail { border-inline-start: 3px solid var(--accent-deep); }
.task-detail .task-result-id { font-family: var(--font-mono, ui-monospace, monospace); font-size: 12px; overflow-wrap: anywhere; }
.task-summary { margin: 0 0 20px; padding: 20px; border: 1px solid var(--border); border-radius: 8px; background: var(--surface-muted); color: var(--text); font-size: 14px; line-height: 1.85; white-space: pre-wrap; overflow-wrap: anywhere; }
.task-detail > .link-button { margin-top: 16px; }
@media (min-width: 1200px) { .task-workspace { grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr); } }
@media (max-width: 767px) {
  .tasks { gap: 16px; }
  .task-workspace { gap: 16px; }
  .tasks .panel { padding: 18px; }
  .task-compose-footer { align-items: flex-start; flex-direction: column; gap: 14px; }
  .task-compose-footer .dialog-actions { width: 100%; }
  .task-item { grid-template-columns: minmax(0, 1fr); gap: 16px; }
  .task-item > .dialog-actions { justify-content: flex-start; }
  .tasks button, .task-form input { min-height: 44px; }
  .task-form textarea { min-height: 140px; }
  .task-approval-head { flex-direction: column; }
}
@media (max-width: 479px) {
  .task-form { grid-template-columns: minmax(0, 1fr); gap: 16px; }
  .task-timeout-field { max-width: none; }
  .task-compose-footer .primary { width: 100%; }
  .task-summary { padding: 14px; }
}
</style>
