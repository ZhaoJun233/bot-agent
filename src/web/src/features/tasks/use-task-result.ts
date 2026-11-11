import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ZbaClient, ZbaTaskResult } from '../../api/zba'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

/** 结果读取拥有自己的展示与失效规则，不参与任务列表或写操作的状态管理。 */
export function useTaskResult(options: {
  client: () => ZbaClient
  reportAuthFailure: (cause: unknown) => boolean
}) {
  const detail = ref<ZbaTaskResult | null>(null)
  const detailError = ref<UiError | null>(null)
  const detailBusy = ref(false)
  let detailSequence = 0
  let unmounted = false
  let boundClient = options.client()

  /** 后一次点击作废前一次的迟到响应，不用 busy 阻止读取另一个任务。 */
  async function open(taskId: string): Promise<void> {
    const sequence = ++detailSequence
    const client = options.client()
    detailBusy.value = true
    detailError.value = null
    detail.value = null
    try {
      const result = await client.getTaskResult(taskId)
      if (unmounted || sequence !== detailSequence || client !== options.client()) return
      if (result === null) {
        detailError.value = { code: 'task_not_found', message: '任务不存在或已被清理。' }
        return
      }
      detail.value = result
    } catch (cause) {
      if (sequence !== detailSequence || client !== options.client()) return
      if (options.reportAuthFailure(cause)) return
      detailError.value = toUiError(cause)
    } finally {
      if (sequence === detailSequence) detailBusy.value = false
    }
  }

  function close(): void {
    detail.value = null
    detailError.value = null
  }

  watch(options.client, client => {
    if (client === boundClient) return
    boundClient = client
    detailSequence += 1
    detailBusy.value = false
    detail.value = null
    detailError.value = null
  })

  onBeforeUnmount(() => {
    unmounted = true
    detailSequence += 1
    detailBusy.value = false
    detail.value = null
  })

  return {
    detail: computed(() => detail.value),
    error: computed(() => detailError.value),
    busy: computed(() => detailBusy.value),
    open,
    close
  }
}
