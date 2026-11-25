import { computed, onBeforeUnmount, reactive, ref, watch, type Ref } from 'vue'
import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaActionCapability,
  type ZbaManagedAction,
  type ZbaManagedDescriptor,
  type ZbaManagedInspectResult,
  type ZbaManagedObjectKey,
  type ZbaManagedOperationResult
} from '../../api/zba'
import { capabilityTitle, kindName } from '../../shared/friendly'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { createOperationKey, newNonce, nowSeconds } from '../configuration/draft'
import { keyIdentity } from './domain-catalog'

export const actionLabels: Record<number, string> = {
  0: '查看', 1: '校验配置', 2: '配置', 3: '启动', 4: '停止',
  5: '测试连接', 6: '连接', 7: '断开', 8: '取消'
}
export const operationStateLabels: Record<number, string> = {
  0: '未开始', 1: '执行中', 2: '已完成', 3: '失败',
  4: '已请求取消', 5: '已取消', 6: '未知'
}
export const operationStateTones: Record<number, 'ok' | 'warn' | 'bad' | 'neutral'> = {
  0: 'neutral', 1: 'warn', 2: 'ok', 3: 'bad', 4: 'warn', 5: 'neutral', 6: 'warn'
}
export const managedResultMessages: Record<number, string> = {
  1: '当前令牌没有该操作权限。',
  2: '没有找到这个目标对象。',
  3: '登记身份有歧义，需要人工核对。',
  4: '该对象不支持此动作。',
  5: '描述符版本已变化，请重新读取后再试。',
  6: '字段校验失败。',
  7: '游标已过期，请重新加载列表。',
  8: '游标无效，请重新加载列表。',
  9: '同一操作键带了不同内容，已被拒绝。',
  10: '操作键已过期。',
  11: '操作键代际不匹配。',
  12: '操作容量已满，请稍后再试。',
  13: '管理存储暂不可用。',
  14: '结果未知，需要核对。',
  15: '服务器时钟回退。',
  16: '契约版本不受支持。'
}

const terminalStates = new Set<number>([2, 3, 5])

export type OperationPhase = 'preparing' | 'submitting' | 'unknown' | 'accepted' | 'rejected'

export interface PendingAction {
  descriptor: ZbaManagedDescriptor
  capability: ZbaActionCapability
  reuseKey: string | null
}

export interface OperationRecord {
  key: string
  target: ZbaManagedObjectKey
  identity: string
  label: string
  kindLabel: string
  action: ZbaManagedAction
  command: unknown
  descriptorRevision: string
  phase: OperationPhase
  result: ZbaManagedOperationResult | null
  cancelledState: number | null
  error: UiError | null
  busy: boolean
  checking: boolean
  priorUnknown: boolean
}

export function isDangerousAction(action: ZbaManagedAction): boolean {
  return action === 4 || action === 7
}

export function supportedActions(descriptor: ZbaManagedDescriptor): ZbaActionCapability[] {
  return descriptor.actions.filter(item => item.supported && item.action !== 0)
}

export function unsupportedActions(descriptor: ZbaManagedDescriptor): ZbaActionCapability[] {
  return descriptor.actions.filter(item => !item.supported && item.action !== 0 && item.reasonCode)
}

function buildManagedCommand(key: ZbaManagedObjectKey, action: ZbaManagedAction): unknown {
  if (key.kind === 'driver') {
    const driverAction = action === 3 ? 0 : action === 4 ? 1 : action === 5 ? 2 : null
    if (driverAction === null) return null
    return { kind: 'driver', key: { ...key.key }, action: driverAction }
  }
  if (key.kind === 'provider') {
    if (action !== 5) return null
    return { kind: 'provider', key: key.key, action: 0 }
  }
  return null
}

export function useCapabilityOperations(options: {
  client: () => ZbaClient
  canWrite: () => boolean
  active: () => boolean
  context: Ref<{ generation: string; contractRevision: string } | null>
  onDescriptorUpdated: (descriptor: ZbaManagedDescriptor) => void
  onAuthExpired: (error: UiError) => void
}) {
  const operations = ref<OperationRecord[]>([])
  const activeKey = ref<string | null>(null)
  const pendingAction = ref<PendingAction | null>(null)
  const actionTrigger = ref<HTMLElement | null>(null)
  const actionBusy = ref(false)
  const actionError = ref<UiError | null>(null)
  const abortedNotice = ref<string | null>(null)
  const dismissNotice = ref<string | null>(null)

  let ownership = 0
  let unmounted = false

  const activeRecord = computed<OperationRecord | null>(
    () => operations.value.find(record => record.key === activeKey.value) ?? null
  )
  const waitingRecords = computed<OperationRecord[]>(
    () => operations.value.filter(record => record.key !== activeKey.value && unsettled(record))
  )

  function ownedBy(epoch: number, client: ZbaClient): boolean {
    return !unmounted && epoch === ownership && options.client() === client
      && options.active() && options.canWrite()
  }

  function isTerminal(state: number): boolean {
    return terminalStates.has(state)
  }

  function unsettled(record: OperationRecord): boolean {
    if (record.phase === 'preparing' || record.phase === 'submitting' || record.phase === 'unknown') return true
    if (record.result?.receipt) return !isTerminal(record.result.receipt.state)
    return false
  }

  function canQuery(record: OperationRecord): boolean {
    if (record.phase === 'preparing' || record.phase === 'submitting') return false
    if (record.phase === 'unknown') return true
    if (record.result?.receipt) return !isTerminal(record.result.receipt.state)
    return false
  }

  function canRetry(record: OperationRecord): boolean {
    return record.phase === 'unknown' && !record.busy && options.canWrite()
  }

  function reportAuthFailure(cause: unknown): boolean {
    if (cause instanceof ZbaRequestError && cause.status === 401) {
      options.onAuthExpired(toUiError(cause))
      return true
    }
    return false
  }

  function requestAction(descriptor: ZbaManagedDescriptor, capability: ZbaActionCapability, event?: Event): void {
    if (!options.canWrite()) return
    actionError.value = null
    abortedNotice.value = null
    const trigger = event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
    actionTrigger.value = trigger
    const identity = keyIdentity(descriptor.key)
    const reusable = operations.value.find(
      item => item.identity === identity && item.action === capability.action && item.phase === 'unknown'
    )
    pendingAction.value = { descriptor, capability, reuseKey: reusable?.key ?? null }
  }

  function cancelPendingAction(): void {
    pendingAction.value = null
    actionError.value = null
  }

  async function refreshDescriptor(target: ZbaManagedObjectKey): Promise<void> {
    try {
      const fresh = await options.client().inspectCapability(target)
      if (fresh.descriptor) options.onDescriptorUpdated(fresh.descriptor)
    } catch (cause) {
      reportAuthFailure(cause)
    }
  }

  function applyResult(record: OperationRecord, result: ZbaManagedOperationResult): void {
    const unknownBefore = record.priorUnknown || record.phase === 'unknown'
    record.result = result
    record.error = null
    if (result.code === 0 && !result.receipt) {
      record.phase = 'unknown'
      record.priorUnknown = true
      return
    }
    if (result.code === 0 && result.receipt) {
      record.phase = 'accepted'
      record.priorUnknown = !isTerminal(result.receipt.state)
      if (isTerminal(result.receipt.state)) void refreshDescriptor(result.receipt.target)
      return
    }
    if (result.code === 13 || result.code === 14) {
      record.phase = 'unknown'
      record.priorUnknown = true
      record.error = {
        code: 'managed_' + result.code,
        message: managedResultMessages[result.code] ?? `动作结果未知（code ${result.code}）。`
      }
      return
    }
    if (unknownBefore) {
      record.phase = 'unknown'
      record.priorUnknown = true
      record.error = {
        code: 'managed_' + result.code,
        message: (managedResultMessages[result.code] ?? `执行失败（code ${result.code}）。`)
          + ' 由于此前结果未知，本次拒绝不能证明原操作未执行；操作键仍保留。'
      }
      return
    }
    record.phase = 'rejected'
    record.error = {
      code: 'managed_' + result.code,
      message: managedResultMessages[result.code] ?? `执行失败（code ${result.code}）。`
    }
  }

  function applyCause(record: OperationRecord, cause: unknown): void {
    if (reportAuthFailure(cause)) return
    const ui = toUiError(cause)
    record.error = ui
    const ambiguous = cause instanceof ZbaNetworkError
      || (cause instanceof ZbaRequestError && (cause.status >= 500 || cause.status === 408 || cause.status === 409 || cause.status === 429))
    if (ambiguous || record.priorUnknown) {
      record.phase = 'unknown'
      record.priorUnknown = true
    } else {
      record.phase = 'rejected'
    }
  }

  async function confirmAction(): Promise<void> {
    const pending = pendingAction.value
    if (!pending || actionBusy.value) return
    const ctx = options.context.value
    if (!ctx) {
      actionError.value = { code: 'missing_generation', message: '尚未读取到配置代际，无法提交动作。请先刷新页面。' }
      return
    }
    const existing = pending.reuseKey ? operations.value.find(item => item.key === pending.reuseKey) : null
    if (existing) {
      pendingAction.value = null
      actionBusy.value = true
      try {
        await resendUnknown(existing)
      } finally {
        actionBusy.value = false
      }
      return
    }
    const command = buildManagedCommand(pending.descriptor.key, pending.capability.action)
    if (!command) {
      actionError.value = { code: 'unsupported_action', message: '当前对象不支持该动作。' }
      return
    }
    const client = options.client()
    const epoch = ownership
    const key = createOperationKey(ctx.generation, nowSeconds(), newNonce()).replace(/^cfg1:/, 'mg1:')
    const record = reactive<OperationRecord>({
      key,
      target: pending.descriptor.key,
      identity: keyIdentity(pending.descriptor.key),
      label: capabilityTitle(pending.descriptor.displayName, pending.descriptor.key),
      kindLabel: kindName(pending.descriptor.key.kind),
      action: pending.capability.action,
      command,
      descriptorRevision: pending.descriptor.descriptorRevision,
      phase: 'preparing',
      result: null,
      cancelledState: null,
      error: null,
      busy: true,
      checking: false,
      priorUnknown: false
    })
    pendingAction.value = null
    actionBusy.value = true
    operations.value = [record, ...operations.value]
    activeKey.value = key

    try {
      let fresh: ZbaManagedInspectResult
      try {
        fresh = await client.inspectCapability(record.target)
      } catch (cause) {
        if (!reportAuthFailure(cause)) {
          record.phase = 'rejected'
          record.error = toUiError(cause)
        }
        return
      }
      if (!ownedBy(epoch, client)) {
        record.phase = 'rejected'
        record.error = { code: 'aborted_client', message: '离开页面或权限变化，动作在发出前已取消。' }
        abortedNotice.value = '动作在提交前已取消，服务端未收到请求。'
        return
      }
      if (!fresh.descriptor) {
        record.phase = 'rejected'
        record.error = { code: 'inspect_' + fresh.code, message: managedResultMessages[fresh.code] ?? '确认描述符失败。' }
        return
      }
      options.onDescriptorUpdated(fresh.descriptor)
      const cap = fresh.descriptor.actions.find(item => item.action === record.action && item.supported)
      if (!cap) {
        record.phase = 'rejected'
        record.error = { code: 'descriptor_changed', message: '该动作当前不可用，请刷新后再试。' }
        return
      }
      record.descriptorRevision = fresh.descriptor.descriptorRevision
      record.phase = 'submitting'
      const result = await client.executeCapability({
        contractRevision: ctx.contractRevision,
        expectedDescriptorRevision: record.descriptorRevision,
        key: record.key,
        command: record.command
      })
      if (!ownedBy(epoch, client) && result.code === 0) {
        record.result = result
        record.phase = 'unknown'
        return
      }
      applyResult(record, result)
    } catch (cause) {
      applyCause(record, cause)
    } finally {
      record.busy = false
      actionBusy.value = false
    }
  }

  async function resendUnknown(record: OperationRecord): Promise<void> {
    if (!canRetry(record)) return
    const ctx = options.context.value
    if (!ctx) {
      record.error = { code: 'missing_generation', message: '尚未读取到配置代际，无法重试提交。' }
      return
    }
    const client = options.client()
    const epoch = ownership
    record.priorUnknown = true
    record.busy = true
    record.checking = true
    record.error = null
    activeKey.value = record.key
    try {
      let fresh: ZbaManagedInspectResult
      try {
        fresh = await client.inspectCapability(record.target)
      } catch (cause) {
        if (!reportAuthFailure(cause)) {
          record.phase = 'unknown'
          record.error = toUiError(cause)
        }
        return
      }
      if (!ownedBy(epoch, client)) {
        record.phase = 'unknown'
        record.error = { code: 'aborted_client', message: '离开页面或权限变化，本次没有重发；上一次提交结果仍未知，操作键已保留。' }
        abortedNotice.value = '重发在提交前已取消，服务端未收到新请求。'
        return
      }
      if (!fresh.descriptor) {
        record.phase = 'unknown'
        record.error = { code: 'inspect_' + fresh.code, message: managedResultMessages[fresh.code] ?? '提交前核对失败；上一次提交结果仍未知。' }
        return
      }
      options.onDescriptorUpdated(fresh.descriptor)
      const cap = fresh.descriptor.actions.find(item => item.action === record.action && item.supported)
      if (!cap) {
        record.phase = 'unknown'
        record.error = { code: 'descriptor_changed', message: '该动作已不再受支持；上一次提交结果仍未知。' }
        return
      }
      record.phase = 'submitting'
      const result = await client.executeCapability({
        contractRevision: ctx.contractRevision,
        expectedDescriptorRevision: record.descriptorRevision || fresh.descriptor.descriptorRevision,
        key: record.key,
        command: record.command
      })
      if (!ownedBy(epoch, client) && result.code === 0) {
        record.result = result
        record.phase = 'unknown'
        return
      }
      applyResult(record, result)
    } catch (cause) {
      if (record.phase === 'submitting') applyCause(record, cause)
      else if (!reportAuthFailure(cause)) record.error = toUiError(cause)
    } finally {
      record.checking = false
      record.busy = false
    }
  }

  async function retryRecord(record: OperationRecord): Promise<void> {
    if (!canRetry(record)) return
    const ctx = options.context.value
    if (!ctx) {
      record.error = { code: 'missing_generation', message: '尚未读取到配置代际，无法重试提交。' }
      return
    }
    const client = options.client()
    const epoch = ownership
    record.priorUnknown = true
    record.busy = true
    record.phase = 'submitting'
    record.error = null
    activeKey.value = record.key
    try {
      const result = await client.executeCapability({
        contractRevision: ctx.contractRevision,
        expectedDescriptorRevision: record.descriptorRevision,
        key: record.key,
        command: record.command
      })
      if (!ownedBy(epoch, client) && result.code === 0) {
        record.result = result
        record.phase = 'unknown'
        return
      }
      applyResult(record, result)
    } catch (cause) {
      applyCause(record, cause)
    } finally {
      record.busy = false
    }
  }

  async function queryRecord(record: OperationRecord): Promise<void> {
    if (record.busy) return
    record.busy = true
    record.error = null
    activeKey.value = record.key
    const trusted = record.result?.receipt ? record.result : null
    try {
      const result = await options.client().queryCapability(record.key)
      if (!operations.value.some(item => item.key === record.key)) return
      if (result.receipt) {
        record.result = result
        record.phase = 'accepted'
        record.priorUnknown = !isTerminal(result.receipt.state)
        if (isTerminal(result.receipt.state)) void refreshDescriptor(result.receipt.target)
        return
      }
      if (result.code === 0) {
        if (trusted === null) record.result = result
        record.phase = 'unknown'
        record.priorUnknown = true
        return
      }
      record.error = {
        code: 'query_' + result.code,
        message: managedResultMessages[result.code] ?? `核对失败（code ${result.code}）。`
      }
      record.phase = 'unknown'
      record.priorUnknown = true
    } catch (cause) {
      if (!reportAuthFailure(cause)) record.error = toUiError(cause)
      record.phase = 'unknown'
      record.priorUnknown = true
    } finally {
      record.busy = false
    }
  }

  async function cancelRecord(record: OperationRecord): Promise<void> {
    if (record.busy || !options.canWrite()) return
    record.busy = true
    record.error = null
    activeKey.value = record.key
    try {
      const result = await options.client().cancelCapability(record.key)
      if (!operations.value.some(item => item.key === record.key)) return
      record.cancelledState = result.state
      if (result.code !== 0) {
        record.error = {
          code: 'cancel_' + result.code,
          message: managedResultMessages[result.code] ?? `取消失败（code ${result.code}）。`
        }
      }
    } catch (cause) {
      if (!reportAuthFailure(cause)) record.error = toUiError(cause)
    } finally {
      record.busy = false
    }
  }

  function closeResult(): void {
    const record = activeRecord.value
    if (!record) return
    if (unsettled(record)) {
      dismissNotice.value = '操作尚未到达终态，操作键继续保留；请先核对或使用同键重试。'
      return
    }
    dismissNotice.value = null
    operations.value = operations.value.filter(item => item.key !== record.key)
    activeKey.value = operations.value[0]?.key ?? null
  }

  function focusRecord(record: OperationRecord): void {
    dismissNotice.value = null
    activeKey.value = record.key
  }

  function abandonInFlight(): void {
    ownership += 1
    pendingAction.value = null
    actionError.value = null
  }

  function reset(): void {
    ownership += 1
    pendingAction.value = null
    actionBusy.value = false
    actionError.value = null
    abortedNotice.value = null
    dismissNotice.value = null
    operations.value = []
    activeKey.value = null
  }

  watch(options.client, () => reset())
  watch(options.active, value => { if (!value) abandonInFlight() })
  watch(options.canWrite, value => { if (!value) abandonInFlight() })

  onBeforeUnmount(() => {
    unmounted = true
    reset()
  })

  return {
    operations,
    activeRecord,
    waitingRecords,
    pendingAction,
    actionTrigger,
    actionBusy,
    actionError,
    abortedNotice,
    dismissNotice,
    unsettled,
    canQuery,
    canRetry,
    requestAction,
    cancelPendingAction,
    confirmAction,
    retryRecord,
    queryRecord,
    cancelRecord,
    closeResult,
    focusRecord,
    reset
  }
}
