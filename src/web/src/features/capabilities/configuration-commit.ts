import { computed, onBeforeUnmount, ref } from 'vue'
import {
  ZbaClient, ZbaRequestError,
  type ZbaConfigurationReceipt, type ZbaConfigurationSaveRequest
} from '../../api/zba'
import { createOperationKey, newNonce, nowSeconds } from '../configuration/draft'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

export type CommitPhase = 'idle' | 'saving' | 'checking' | 'unconfirmed' | 'conflict' | 'reload-required'

/** One configuration intent at a time. Only a verified receipt or authoritative NotFound releases its key. */
export function useConfigurationCommit(options: {
  client: () => ZbaClient
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
  const owned = (client: ZbaClient): boolean => alive && options.client() === client

  function auth(cause: unknown): boolean {
    if (!(cause instanceof ZbaRequestError) || cause.status !== 401) return false
    pendingKey.value = null
    options.onAuth(toUiError(cause))
    return true
  }
  function finish(value: ZbaConfigurationReceipt): void {
    receipt.value = value
    pendingKey.value = null
    phase.value = 'idle'
    notice.value = value.decision === 1 ? '没有配置变更。' : value.applyState === 3 ? '已保存，需重启后生效。不会自动重启。' : '已保存；实际运行状态以服务端回执为准。'
  }

  function verified(value: ZbaConfigurationReceipt | undefined, key: string): value is ZbaConfigurationReceipt {
    return value !== undefined && value.key === key && value.commitState !== 2
      && (value.decision === 1 || value.commitState === 1)
  }

  async function save(snapshotGeneration: string, request: Omit<ZbaConfigurationSaveRequest, 'key'>): Promise<boolean> {
    if (!options.canWrite() || phase.value !== 'idle') return false
    const client = options.client()
    const key = createOperationKey(snapshotGeneration, nowSeconds(), newNonce())
    pendingKey.value = key
    phase.value = 'saving'
    error.value = null
    notice.value = ''
    try {
      const result = await client.saveConfiguration({ ...request, key })
      if (!owned(client)) return false
      if (result.code === 12 || ((result.code === 0 || result.code === 13) && !verified(result.receipt, key))) {
        phase.value = 'unconfirmed'
        return false
      }
      if ((result.code === 0 || result.code === 13) && result.receipt) {
        finish(result.receipt)
        return true
      }
      pendingKey.value = null
      if (result.code === 3) {
        phase.value = 'conflict'
        notice.value = '版本已变化，草稿已保留。读取最新后核对差异，再明确保存。'
        return false
      }
      phase.value = 'idle'
      // Do not interpolate arbitrary backend text or submitted values into an error.
      error.value = { code: 'save_' + result.code, message: '保存被拒绝。请检查字段；仍被引用的提供商不能删除。' }
    } catch (cause) {
      if (!owned(client)) return false
      if (auth(cause)) return false
      if (cause instanceof ZbaRequestError && cause.status >= 400 && cause.status < 500) {
        phase.value = 'idle'
        pendingKey.value = null
        error.value = toUiError(cause)
      } else {
        phase.value = 'unconfirmed'
      }
    }
    return false
  }

  async function check(): Promise<boolean> {
    const key = pendingKey.value
    if (!key || phase.value !== 'unconfirmed' || !options.canQuery()) return false
    const client = options.client()
    phase.value = 'checking'
    error.value = null
    try {
      const result = await client.findConfiguration(key)
      if (!owned(client)) return false
      if (result.code === 0 && verified(result.receipt, key)) { finish(result.receipt); return true }
      if (result.code === 14) {
        pendingKey.value = null
        phase.value = 'idle'
        notice.value = '服务端确认没有这次提交；草稿保留，可再次保存。'
        return false
      }
      if (result.code === 5 || result.code === 6) {
        phase.value = 'reload-required'
        notice.value = '操作键已过期或代际已变化，不能据此判断提交失败。请读取当前配置并人工核对。'
        return false
      }
      phase.value = 'unconfirmed'
      error.value = { code: 'find_' + result.code, message: '仍无法确认提交结果，操作键已保留。' }
    } catch (cause) {
      if (!owned(client)) return false
      phase.value = 'unconfirmed'
      if (!auth(cause)) error.value = toUiError(cause)
    }
    return false
  }

  /** Caller must first re-read successfully and show the retained draft beside the new baseline. */
  function acknowledgeReload(): void {
    if (phase.value !== 'conflict' && phase.value !== 'reload-required') return
    pendingKey.value = null
    phase.value = 'idle'
    notice.value = '已读取最新配置，草稿仍保留；请核对后再保存。'
  }

  return { phase, pendingKey, error, notice, receipt, locked, save, check, acknowledgeReload }
}
