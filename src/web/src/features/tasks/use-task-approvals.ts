/** 审批队列、决定登记与恢复资格由此模块统一维护；页面负责共同 ownership 和生命周期顺序。 */
import { computed, ref, watch, type ComputedRef, type DeepReadonly } from 'vue'
import { ZbaRequestError, type ZbaApprovalSummary, type ZbaClient } from '../../api/zba'
import { type SyncSubscriber } from '../../shared/sync/event-scheduler'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { beginDecision, forgetDecision, markDecision, pendingDecisions, type PendingDecision } from './pending-decisions'

/** 审批队列的一行；stale 阻止重放旧决定，unknown 不推断是否生效。 */
interface ApprovalRow extends ZbaApprovalSummary {
  stale: boolean
  staleReason: string | null
  unknown: boolean
}

interface ApprovalContext {
  readonly client: ZbaClient
  readonly ownership: number
  readonly active: boolean
  readonly mode: 'read' | 'admin'
  readonly writesEnabled: boolean
}

interface ApprovalsView {
  readonly rows: readonly ApprovalRow[]
  readonly loaded: boolean
  readonly pendingCount: number
  readonly busyIds: readonly string[]
  readonly readError: UiError | null
  readonly decisionError: UiError | null
  readonly decisionNotice: string | null
}

export function useTaskApprovals(options: {
  context(): ApprovalContext
  reportAuthFailure(cause: unknown): boolean
  kick(): void
}): {
  readonly view: ComputedRef<DeepReadonly<ApprovalsView>>
  readonly subscription: Readonly<SyncSubscriber>
  read(kind?: 'background' | 'manual'): Promise<boolean>
  decide(row: DeepReadonly<ApprovalRow>, approve: boolean): Promise<void>
  abandon(): void
  reset(): void
  dispose(): void
} {
  let disposed = false
  let boundClient = options.context().client
  const canWrite = computed(() => {
    const current = options.context()
    return current.mode === 'admin' && current.writesEnabled
  })
  const approvals = ref<ApprovalRow[]>([])
  const approvalsLoaded = ref(false)
  const approvalError = ref<UiError | null>(null)
  const decisionError = ref<UiError | null>(null)
  const decisionRecords = computed(() => pendingDecisions(options.context().client))
  const approvalBusy = computed(() => decisionRecords.value.filter(item => item.busy).map(item => item.id))
  const decisionNotice = ref<string | null>(null)

  function ownedBy(epoch: number, client: ZbaClient): boolean {
    const current = options.context()
    return !disposed && epoch === current.ownership && current.client === client
      && current.active !== false && current.mode === 'admin' && current.writesEnabled
  }

  /**
   * 本次会话里决定失败过的审批。
   *
   * 过期/已决等明确终结保留只读痕迹；未知和临时拒绝也禁止重放旧意图。
   * 后者只有人工重新读取成功、确认仍 Pending 后才能发起一次新的人工决定。
   */
  let approvalSequence = 0

  async function read(kind: 'background' | 'manual' = 'background'): Promise<boolean> {
    if (disposed) return true
    const sequence = ++approvalSequence
    const client = options.context().client
    // Only a read started after the original POST settled can authorize a new decision.
    const retryable = decisionRecords.value.filter(item => !item.busy && item.retryable)
    try {
      const fresh = await client.listApprovals()
      // 换令牌/换服务后旧读取晚到：整份丢弃，不能让旧身份的队列覆盖新快照。
      if (disposed || sequence !== approvalSequence || client !== options.context().client) return true
      approvalsLoaded.value = true
      approvalError.value = null
      // 以服务端队列为准；但决定失败过的行保留为只读痕迹（它可能已经不在队列里了，
      // 也可能因为策略变更又出现了）。刷新失败时下面的 catch 不清列表，显示旧数据。
      if (kind === 'manual') {
        for (const item of fresh) {
          const record = retryable.find(record => record.id === item.id)
          if (!record || !decisionRecords.value.includes(record) || record.busy
            || !(Date.parse(item.expiresAt) > Date.now())) continue
          forgetDecision(record)
          staleReasons.delete(item.id)
        }
      }
      const seen = new Set(fresh.map((item) => item.id))
      const carried = approvals.value.filter((row) => row.stale && !seen.has(row.id))
      approvals.value = [
        ...fresh.map((item) => ({
          ...item,
          ...decisionMark(item.id)
        })),
        ...carried.map((row) => ({
          ...row, stale: true,
          staleReason: row.unknown ? '当前已不在待审批队列；这不证明这次决定是否生效。' : row.staleReason
        }))
      ]
      return true
    } catch (cause) {
      if (disposed || sequence !== approvalSequence || client !== options.context().client) return true
      if (options.reportAuthFailure(cause)) return false
      // 刷新失败显示旧数据：不清列表，只标记读取失败。
      approvalError.value = toUiError(cause)
      return false
    }
  }

  const staleReasons = new Map<string, string>()

  function decisionMark(id: string) {
    const record = decisionRecords.value.find(item => item.id === id)
    return { stale: record?.stale ?? false, unknown: record?.unknown ?? false,
      staleReason: record?.stale ? (staleReasons.get(id) ?? (record.unknown
        ? '这次决定的结果未知；请人工重新读取队列后再决定。' : '这次决定没有生效。')) : null }
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
  async function decide(approval: DeepReadonly<ApprovalRow>, approve: boolean): Promise<void> {
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
    const client = options.context().client
    const epoch = options.context().ownership
    if (!ownedBy(epoch, client)) {
      decisionError.value = { code: 'aborted_client', message: '已经离开任务页或权限发生变化，这次审批没有发送。' }
      return
    }
    if (decisionRecords.value.some(item => item.id === approval.id)) return
    let record: PendingDecision
    try { record = beginDecision(client, approval.id, approval.expiresAt) } catch {
      decisionError.value = { code: 'pending_decision_capacity', message: '还有审批决定尚未核对，本次没有发送。请先核对待审批队列。' }
      return
    }
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
        markStale(record, decideReasonText(result.reasonCode), false, !isTerminalApprovalReason(result.reasonCode))
        return
      }
      // 成功体必须与本地意图一致：错配不能显示成"已批准"。
      const expected = approve ? 'approve' : 'reject'
      if (result.decision !== expected) {
        decisionError.value = {
          code: 'decide_mismatch',
          message: '服务端回执与本次决定不一致（发送 ' + expected + '，回执 ' + result.decision + '），请重新读取队列确认。'
        }
        markStale(record, '回执与本次决定不一致，结果未知；请人工重新读取队列确认。', true, true)
        return
      }
      approvals.value = approvals.value.filter((item) => item.id !== approval.id)
      forgetDecision(record)
      decisionNotice.value = '已提交决定：' + (approve ? '批准' : '拒绝') + '。审批通过只表示同意执行，不代表工具已经成功。'
      options.kick()
    } catch (cause) {
      // 同上：换了令牌/服务之后，旧决定请求的 401 不属于当前会话。
      if (!ownedBy(epoch, client)) return
      if (options.reportAuthFailure(cause)) return
      const ui = toUiError(cause)
      const reason = cause instanceof ZbaRequestError ? cause.reason : undefined
      // 网络中断、5xx、2xx 回执读不懂：决定可能已经生效，不能写成"未接受"。
      const outcomeUnknown = !(cause instanceof ZbaRequestError)
        || cause.status >= 500 || (cause.status >= 200 && cause.status < 300)
        || [408, 429].includes(cause.status)
      const reasonText = outcomeUnknown
        ? '这次决定的结果未知；请人工重新读取队列，确认仍待审批后再作新决定。'
        : decideReasonText(reason)
      decisionError.value = { ...ui, message: ui.message + reasonText }
      markStale(record, reasonText, outcomeUnknown, outcomeUnknown || !isTerminalApprovalReason(reason ?? (cause instanceof ZbaRequestError ? cause.code : undefined)))
    } finally {
      if (!ownedBy(epoch, client)) markDecision(record, true, true)
      // Settlement belongs to the request, including after its view has been disposed.
      record.busy = false
      if (!disposed && client === options.context().client) {
        // 自动补读只更新证据，不能替用户确认新的决定或重放 POST。
        await read()
      }
    }
  }

  function isTerminalApprovalReason(reason: string | undefined): boolean {
    return ['expired', 'approval_expired', 'already_decided', 'source_retired', 'unknown_request', 'approval_not_found'].includes(reason ?? '')
  }

  function markStale(record: PendingDecision, reason: string, unknown = false, retryable = false): void {
    // 在决定结束之前发出的 GET 不能充当该决定之后的 fresh Pending 证据。
    approvalSequence += 1
    markDecision(record, unknown, retryable)
    staleReasons.set(record.id, reason)
    approvals.value = approvals.value.map((row) => (row.id === record.id
      ? { ...row, stale: true, unknown, staleReason: reason }
      : row))
  }

  function abandon(): void {
    // 已发送的决定仍可能生效；旧回执不再属于当前页面时，保留待核对状态。
    for (const record of pendingDecisions(boundClient).filter(item => item.busy)) {
      markStale(record, '页面或写入状态已变化，这次决定的结果未知；请重新读取队列后再决定。', true, true)
    }
  }

  function reset(): void {
    if (disposed) return
    approvalSequence += 1
    approvals.value = []
    approvalsLoaded.value = false
    approvalError.value = null
    decisionError.value = null
    decisionNotice.value = null
    // 旧会话的"未生效"标记属于旧身份，不能带进新会话。
    staleReasons.clear()
    boundClient = options.context().client
  }

  const stopRegistryWatch = watch(decisionRecords, () => {
    approvals.value = approvals.value.map(row => ({ ...row, ...decisionMark(row.id) }))
  }, { deep: true })

  function dispose(): void {
    if (disposed) return
    disposed = true
    stopRegistryWatch()
    approvalSequence += 1
    approvals.value = []
  }

  const subscription: Readonly<SyncSubscriber> = {
    name: 'approvals',
    matches: (event) => event.type === 'approval.decided'
      || (event.type === 'task.updated' && event.state === 'waiting_approval'),
    reload: async () => {
      if (disposed) return
      if (!(await read())) throw new Error('approvals_read_failed')
    }
  }

  const view = computed<DeepReadonly<ApprovalsView>>(() => ({
    rows: approvals.value,
    loaded: approvalsLoaded.value,
    pendingCount: approvals.value.filter(row => !row.stale).length,
    busyIds: approvalBusy.value,
    readError: approvalError.value,
    decisionError: decisionError.value,
    decisionNotice: decisionNotice.value
  }))

  return { view, subscription, read, decide, abandon, reset, dispose }
}
