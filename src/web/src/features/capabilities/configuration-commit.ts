import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaConfigurationReceipt, type ZbaConfigurationSaveRequest } from '../../api/zba'
import { createOperationKey, newNonce, nowSeconds } from '../configuration/draft'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { beginOperation, getPendingOperation, markOperation, pendingOperations } from '../../shared/sync/pending-operations'

export type CommitPhase = 'idle' | 'saving' | 'checking' | 'unconfirmed' | 'conflict' | 'reload-required'
/** The registry owns the receipt key; a component owns only its current presentation. */
export function useConfigurationCommit(options: {
  client: () => ZbaClient
  target?: () => string
  canWrite: () => boolean
  canQuery: () => boolean
  onAuth: (error: UiError) => void
}) {
  const phase = ref<CommitPhase>('idle')
  const pendingKey = ref<string | null>(null)
  const error = ref<UiError | null>(null)
  const notice = ref('')
  const receipt = ref<ZbaConfigurationReceipt | null>(null)
  const locked = computed(() => phase.value !== 'idle')
  let alive = true
  onBeforeUnmount(() => { alive = false })
  const target = (): string => options.target?.() ?? 'configuration'
  const owned = (client: ZbaClient, originalTarget: string): boolean => alive && options.client() === client && target() === originalTarget
  watch(() => getPendingOperation(options.client(), 'configuration', target()), (operation) => {
    if (operation) {
      pendingKey.value = operation.key
      if (phase.value !== 'checking' && phase.value !== 'reload-required') phase.value = operation.state === 'in-flight' ? 'saving' : 'unconfirmed'
      notice.value = '已有提交正在等待或尚未确认，原操作键已恢复。请核对，不要重复保存。'
    } else if (pendingKey.value && pendingOperations(options.client(), 'configuration', target()).some(r => r.key === pendingKey.value && r.state === 'settled')) {
      pendingKey.value = null
      phase.value = 'reload-required'
      notice.value = '原提交已有确定结论；请重新读取当前配置后再编辑。'
    }
  }, { immediate: true, flush: 'sync' })
  function terminal(client: ZbaClient, key: string, owner: string): boolean {
    return pendingOperations(client, 'configuration', owner).some(r => r.key === key && r.state === 'settled')
  }
  function auth(cause: unknown): boolean {
    if (!(cause instanceof ZbaRequestError) || cause.status !== 401) return false
    options.onAuth(toUiError(cause)); return true
  }
  function verified(value: ZbaConfigurationReceipt | undefined, key: string): value is ZbaConfigurationReceipt {
    return value !== undefined && value.key === key && value.commitState !== 2 && (value.decision === 1 || value.commitState === 1)
  }
  function finish(value: ZbaConfigurationReceipt): void {
    receipt.value = value; pendingKey.value = null; phase.value = 'idle'
    notice.value = value.decision === 1 ? '没有配置变更。' : value.applyState === 3 ? '已保存，需重启后生效。不会自动重启。' : '已保存；实际运行状态以服务端回执为准。'
  }
  async function save(generation: string, request: Omit<ZbaConfigurationSaveRequest, 'key'>): Promise<boolean> {
    if (!options.canWrite() || phase.value !== 'idle') return false
    const client = options.client(), originalTarget = target()
    const key = createOperationKey(generation, nowSeconds(), newNonce())
    try { beginOperation(client, { key, kind: 'configuration', target: originalTarget }) } catch {
      error.value = { code: 'pending_operation', message: '已有未确认提交或核对登记容量已满，不能另行保存。' }; return false
    }
    pendingKey.value = key; phase.value = 'saving'; error.value = null; notice.value = ''
    try {
      const result = await client.saveConfiguration({ ...request, key })
      const confirmed = (result.code === 0 || result.code === 13) && verified(result.receipt, key)
      const unknown = result.code === 12 || ((result.code === 0 || result.code === 13) && !confirmed)
      if (terminal(client, key, originalTarget)) return false
      markOperation(client, key, unknown ? 'unknown' : 'settled')
      if (!owned(client, originalTarget) || (unknown && terminal(client, key, originalTarget))) return false
      if (unknown) { phase.value = 'unconfirmed'; return false }
      if (confirmed) { finish(result.receipt!); return true }
      pendingKey.value = null
      if (result.code === 3) { phase.value = 'conflict'; notice.value = '版本已变化，草稿已保留。读取最新后核对差异，再明确保存。'; return false }
      phase.value = 'idle'; error.value = { code: 'save_' + result.code, message: '保存被拒绝。请检查字段；仍被引用的提供商不能删除。' }
    } catch (cause) {
      const rejected = cause instanceof ZbaRequestError && cause.status >= 400 && cause.status < 500
      if (terminal(client, key, originalTarget)) return false
      markOperation(client, key, rejected ? 'settled' : 'unknown')
      if (!owned(client, originalTarget) || (!rejected && terminal(client, key, originalTarget))) return false
      if (auth(cause)) { pendingKey.value = null; phase.value = 'idle'; return false }
      if (rejected) { phase.value = 'idle'; pendingKey.value = null; error.value = toUiError(cause) }
      else phase.value = 'unconfirmed'
    }
    return false
  }
  async function check(): Promise<boolean> {
    const key = pendingKey.value
    if (!key || phase.value !== 'unconfirmed' || !options.canQuery()) return false
    const client = options.client(), originalTarget = target()
    phase.value = 'checking'; error.value = null
    try {
      const result = await client.findConfiguration(key)
      const confirmed = result.code === 0 && verified(result.receipt, key)
      if (terminal(client, key, originalTarget)) return false
      if (confirmed || result.code === 14) markOperation(client, key, 'settled')
      if (!owned(client, originalTarget) || (!confirmed && result.code !== 14 && terminal(client, key, originalTarget))) return false
      if (confirmed) { finish(result.receipt!); return true }
      if (result.code === 14) { pendingKey.value = null; phase.value = 'idle'; notice.value = '服务端确认没有这次提交；草稿保留，可再次保存。'; return false }
      if (result.code === 5 || result.code === 6) { phase.value = 'reload-required'; notice.value = '操作键已过期或代际已变化，不能据此判断提交失败。请读取当前配置并人工核对。'; return false }
      phase.value = 'unconfirmed'; error.value = { code: 'find_' + result.code, message: '仍无法确认提交结果，操作键已保留。' }
    } catch (cause) {
      if (!owned(client, originalTarget) || terminal(client, key, originalTarget)) return false
      phase.value = 'unconfirmed'; if (!auth(cause)) error.value = toUiError(cause)
    }
    return false
  }
  /** Only after a successful current read and explicit human review; never an automatic retry. */
  function acknowledgeReload(): void {
    if (phase.value !== 'conflict' && phase.value !== 'reload-required') return
    if (pendingKey.value) markOperation(options.client(), pendingKey.value, 'settled')
    pendingKey.value = null; phase.value = 'idle'; notice.value = '已读取最新配置，草稿仍保留；请核对后再保存。'
  }
  return { phase, pendingKey, error, notice, receipt, locked, save, check, acknowledgeReload }
}
