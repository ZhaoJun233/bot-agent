import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import type { ZbaClient, ZbaRuntimeLogFeedEntry } from '../../api/zba'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { RuntimeLogBuffer } from './runtime-log-buffer'

const RETRY_SECONDS = [1, 2, 4, 8, 15]

export function useRuntimeLogFeed(options: {
  client: () => ZbaClient
  active: () => boolean
  locked: () => boolean
  authExpired: (error: UiError) => void
}) {
  const rows = shallowRef<ZbaRuntimeLogFeedEntry[]>([])
  const error = ref<UiError | null>(null)
  const receiving = ref(true)
  const loading = ref(false)
  const hidden = ref(document.visibilityState === 'hidden')
  const forbidden = ref(false)
  const retrySeconds = ref(0)
  const lastUpdated = ref<number | null>(null)
  const notice = ref('')
  const evicted = ref(0)
  const available = computed(() => options.active() && !options.locked() && !hidden.value && !forbidden.value)
  let buffer = new RuntimeLogBuffer()
  let generation = 0
  let disposed = false
  let timer: ReturnType<typeof setTimeout> | undefined
  let controller: AbortController | undefined
  let failures = 0

  function stop(): void {
    generation++
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
    controller?.abort()
    controller = undefined
    loading.value = false
    retrySeconds.value = 0
  }
  function purge(): void {
    buffer = new RuntimeLogBuffer()
    rows.value = []
    error.value = null
    notice.value = ''
    evicted.value = 0
    lastUpdated.value = null
  }
  function schedule(delay: number): void {
    if (!disposed && available.value && receiving.value) {
      timer = setTimeout(() => { timer = undefined; void poll() }, delay)
    }
  }
  async function poll(): Promise<void> {
    if (disposed || !available.value || loading.value) return
    if (timer !== undefined) clearTimeout(timer)
    timer = undefined
    const requestGeneration = generation
    const client = options.client()
    const requestController = new AbortController()
    controller = requestController
    loading.value = true
    let delay = 1000
    const current = () => !disposed && requestGeneration === generation && client === options.client() && available.value
    try {
      const page = await client.listRuntimeLogFeed(buffer.cursor, requestController.signal)
      if (!current()) return
      const result = buffer.append(page)
      rows.value = buffer.rows
      evicted.value += result.evicted
      if (page.resetReason) {
        const reason = { expired: '服务端窗口已淘汰较早日志', restart: 'BotAgent 已重启，日志序列已重置', ahead: '游标已超出当前日志序列' }[page.resetReason]
        notice.value = reason + (page.omittedCount === undefined ? '；缺口数量未知。' : '；遗漏 ' + page.omittedCount + ' 条。')
      }
      if (result.skippedReset) notice.value = '已保留清空边界：跳过本次历史快照，只接收后续新日志。'
      error.value = null
      failures = 0
      retrySeconds.value = 0
      lastUpdated.value = Date.now()
      delay = page.hasMore ? 0 : 1000
    } catch (cause) {
      if (!current()) return
      const failure = toUiError(cause)
      error.value = failure
      if (failure.status === 401 || failure.status === 403 || failure.code === 'zba_not_configured') {
        purge()
        error.value = failure
        forbidden.value = true
        receiving.value = false
        options.authExpired(failure)
      } else {
        retrySeconds.value = RETRY_SECONDS[Math.min(failures++, RETRY_SECONDS.length - 1)]!
        delay = retrySeconds.value * 1000
      }
    } finally {
      if (requestGeneration === generation && client === options.client() && !disposed) {
        loading.value = false
        controller = undefined
        schedule(delay)
      }
    }
  }
  function clear(): void {
    buffer.clear()
    rows.value = []
    evicted.value = 0
    notice.value = '仅清空本页；游标与清空边界已保留，服务端日志未删除。'
  }
  function visibilityChanged(): void { hidden.value = document.visibilityState === 'hidden' }
  watch([available, receiving], () => {
    stop()
    if (available.value && receiving.value) void poll()
  }, { flush: 'sync' })
  watch(options.client, () => {
    stop()
    purge()
    failures = 0
    forbidden.value = false
    receiving.value = true
    if (available.value) void poll()
  }, { flush: 'sync' })
  watch(options.locked, locked => { if (locked) { stop(); purge() } }, { flush: 'sync' })
  onMounted(() => {
    document.addEventListener('visibilitychange', visibilityChanged)
    if (available.value) void poll()
  })
  onBeforeUnmount(() => { disposed = true; stop(); purge(); document.removeEventListener('visibilitychange', visibilityChanged) })
  return { rows, error, receiving, loading, hidden, forbidden, retrySeconds, lastUpdated, notice, evicted, available, poll, clear }
}
