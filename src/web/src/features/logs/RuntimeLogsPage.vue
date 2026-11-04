<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ZbaClient, ZbaRuntimeLogPage } from '../../api/zba'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import AppIcon from '../../shared/ui/AppIcon.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'

const props = defineProps<{ client: ZbaClient; active?: boolean }>()
const emit = defineEmits<{ 'auth-expired': [error: UiError] }>()
const page = ref<ZbaRuntimeLogPage | null>(null)
const error = ref<UiError | null>(null)
const loading = ref(false)
const autoRefresh = ref(false)
const hidden = ref(document.visibilityState === 'hidden')
const query = ref('')
const level = ref('all')
const source = ref('all')
const limit = ref(200)
const lastUpdated = ref<number | null>(null)
let sequence = 0
let disposed = false
let timer: ReturnType<typeof setTimeout> | undefined
const available = computed(() => props.active !== false && !hidden.value)
const items = computed(() => page.value?.items ?? [])
const sources = computed(() => [...new Set(items.value.map(item => item.source))].sort())
const filtered = computed(() => {
  const text = query.value.trim().toLocaleLowerCase()
  return items.value.filter(item => (level.value === 'all' || item.level === level.value)
    && (source.value === 'all' || item.source === source.value)
    && (!text || (item.source + ' ' + item.message).toLocaleLowerCase().includes(text)))
})
const warnings = computed(() => items.value.filter(item => item.level === 'warn').length)
const errors = computed(() => items.value.filter(item => item.level === 'error').length)
const labels = { info: '信息', warn: '警告', error: '错误' }
const clock = new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
function time(value: number): string { return clock.format(new Date(value)) }
function stopTimer(): void { if (timer !== undefined) clearTimeout(timer); timer = undefined }
function schedule(): void {
  stopTimer()
  if (!disposed && autoRefresh.value && available.value && !loading.value) {
    timer = setTimeout(() => { void reload() }, 5000)
  }
}
function invalidate(): void { sequence++; loading.value = false; stopTimer() }

async function reload(): Promise<void> {
  if (disposed || !available.value) return
  stopTimer()
  const request = ++sequence
  const client = props.client
  loading.value = true
  error.value = null
  try {
    const result = await client.listRuntimeLogs(limit.value)
    if (disposed || request !== sequence || client !== props.client) return
    page.value = result
    lastUpdated.value = Date.now()
  } catch (cause) {
    if (disposed || request !== sequence || client !== props.client) return
    const failure = toUiError(cause)
    error.value = failure
    autoRefresh.value = false
    if (failure.status === 401 || failure.status === 403 || failure.code === 'zba_not_configured') {
      page.value = null
      lastUpdated.value = null
      if (failure.status === 401 || failure.code === 'zba_not_configured') emit('auth-expired', failure)
    }
  } finally {
    if (!disposed && request === sequence && client === props.client) {
      loading.value = false
      schedule()
    }
  }
}
function clearFilters(): void { query.value = ''; level.value = 'all'; source.value = 'all' }
function visibilityChanged(): void { hidden.value = document.visibilityState === 'hidden' }
watch(autoRefresh, schedule)
watch(limit, () => { void reload() })
watch(() => props.active, active => {
  if (active === false) invalidate()
  else void reload()
}, { flush: 'sync' })
watch(hidden, value => {
  if (value) invalidate()
  else if (autoRefresh.value) void reload()
}, { flush: 'sync' })
watch(() => props.client, () => {
  invalidate()
  page.value = null
  lastUpdated.value = null
  error.value = null
  autoRefresh.value = false
  clearFilters()
  void reload()
}, { flush: 'sync' })
onMounted(() => {
  document.addEventListener('visibilitychange', visibilityChanged)
  void reload()
})
onBeforeUnmount(() => {
  disposed = true
  invalidate()
  document.removeEventListener('visibilitychange', visibilityChanged)
})
defineExpose({ reload })
</script>

<template>
  <section class="runtime-logs" aria-labelledby="runtime-logs-title" data-testid="runtime-logs-page">
    <PageHeader title-id="runtime-logs-title" title="运行日志" eyebrow="运行诊断" description="最近的进程事件 · 最新在前 · 只读安全摘要">
      <template #default>
        <button type="button" class="tonal" :disabled="loading" @click="reload">
          <AppIcon name="refresh" />{{ loading ? '正在读取…' : '刷新日志' }}
        </button>
      </template>
    </PageHeader>

    <p class="inline-notice privacy-note"><AppIcon name="shield" />
      聊天正文、成员信息和凭据不会返回浏览器。未知内容仅显示隐藏占位，不提供原文查看。
    </p>
    <div class="log-summary" aria-label="已读取日志统计">
      <div><span>已读取</span><strong>{{ items.length }}<small> / {{ page?.capacity ?? 600 }}</small></strong></div>
      <div><span>警告</span><strong class="warning-count">{{ warnings }}</strong></div>
      <div><span>错误</span><strong class="error-count">{{ errors }}</strong></div>
      <div class="refresh-state"><span>{{ autoRefresh ? (available ? '自动刷新 · 每 5 秒' : '自动刷新已暂停') : '手动刷新' }}</span>
        <small>{{ lastUpdated === null ? '尚未读取' : '更新于 ' + time(lastUpdated) }}</small>
      </div>
    </div>
    <ErrorNotice v-if="error" :error="error" />
    <p v-if="error && page" class="inline-notice warn">读取失败，下面保留上次成功的快照；自动刷新已停止。</p>

    <div class="panel log-panel">
      <div class="log-filters">
        <label class="search-filter">关键词<input v-model="query" type="search" placeholder="搜索安全摘要或来源" /></label>
        <label>级别<select v-model="level"><option value="all">全部级别</option><option value="info">信息</option><option value="warn">警告</option><option value="error">错误</option></select></label>
        <label>来源<select v-model="source"><option value="all">全部来源</option><option v-for="name in sources" :key="name" :value="name">{{ name }}</option></select></label>
        <label>读取条数<select v-model.number="limit"><option :value="100">最近 100 条</option><option :value="200">最近 200 条</option><option :value="600">最近 600 条</option></select></label>
      </div>
      <div class="log-controls">
        <p role="status">显示 {{ filtered.length }} / {{ items.length }} 条<span> · 仅筛选当前快照</span></p>
        <label class="auto-refresh"><input v-model="autoRefresh" type="checkbox" />每 5 秒刷新</label>
        <button v-if="query || level !== 'all' || source !== 'all'" type="button" @click="clearFilters">清除筛选</button>
      </div>
      <p v-if="loading && !page" class="empty-state">正在读取运行日志…</p>
      <p v-else-if="!page && error" class="empty-state">尚未获取日志。请检查连接或权限后点击刷新日志。</p>
      <p v-else-if="!items.length" class="empty-state">暂无运行日志。只显示内存窗口中的最近记录，不提供历史文件翻页。</p>
      <p v-else-if="!filtered.length" class="empty-state">没有匹配的日志。试试其他关键词或清除筛选。</p>
      <ol v-else class="log-list" aria-label="运行日志记录" :aria-busy="loading">
        <li v-for="(item, index) in filtered" :key="index" class="log-row" :data-level="item.level">
          <time :datetime="new Date(item.time).toISOString()">{{ time(item.time) }}</time>
          <span class="level-tag" :class="item.level">{{ labels[item.level] }}</span>
          <span class="log-source">{{ item.source }}</span>
          <div class="log-message">{{ item.message }}<span v-if="item.redacted" class="redacted-tag">已脱敏</span></div>
        </li>
      </ol>
      <p class="log-footnote">内存窗口最多 600 条，不是持久化日志文件。切换页面或隐藏浏览器时暂停自动刷新。</p>
    </div>
  </section>
</template>

<style scoped>
.runtime-logs { display: grid; gap: 20px; min-width: 0; }
.privacy-note { display: flex; align-items: center; gap: 10px; }
.privacy-note svg { flex: none; }
.log-summary { display: grid; grid-template-columns: 1fr 1fr 1fr 2fr; border: 1px solid var(--border); border-radius: var(--radius-lg); background: var(--surface); }
.log-summary > div { display: grid; gap: 5px; padding: 18px 22px; border-right: 1px solid var(--border); }
.log-summary > div:last-child { border-right: 0; }
.log-summary span, .log-summary small { color: var(--text-muted); font-size: 12px; font-weight: 400; }
.log-summary strong { font: 26px var(--font-mono); }
.warning-count { color: var(--warn-text); }
.error-count { color: var(--bad-text); }
.refresh-state { align-content: center; }
.log-filters { display: grid; grid-template-columns: minmax(160px, 2fr) repeat(3, minmax(100px, 1fr)); gap: 14px; }
.log-filters label { display: grid; gap: 7px; font-size: 12px; color: var(--text-muted); }
.log-filters input, .log-filters select { width: 100%; }
.log-controls { display: flex; align-items: center; flex-wrap: wrap; gap: 12px; margin: 16px 0; font-size: 12px; }
.log-controls p { margin: 0 auto 0 0; }
.log-controls p span, .log-footnote { color: var(--text-muted); }
.auto-refresh { display: inline-flex; align-items: center; gap: 7px; cursor: pointer; }
.log-list { list-style: none; margin: 0; padding: 0; border-top: 1px solid var(--border); }
.log-row { display: grid; grid-template-columns: 140px 48px 90px minmax(0, 1fr); gap: 12px; align-items: start; border-bottom: 1px solid var(--border); padding: 14px 0; font-size: 12px; }
.log-row time { color: var(--text-muted); font: 11px/1.8 var(--font-mono); }
.log-source { font-family: var(--font-mono); overflow-wrap: anywhere; }
.log-message { overflow-wrap: anywhere; line-height: 1.8; }
.level-tag { display: inline-block; border-radius: 4px; padding: 2px 6px; font-size: 11px; text-align: center; }
.level-tag.info { color: var(--text-muted); background: var(--surface-muted); }
.level-tag.warn { color: var(--warn-text); background: var(--warn-bg); }
.level-tag.error { color: var(--bad-text); background: var(--bad-bg); }
.redacted-tag { display: inline-block; margin-left: 8px; color: var(--text-soft); font-size: 10px; }
.log-footnote { margin: 18px 0 0; font-size: 11px; }
@media (max-width: 760px) {
  .log-summary { grid-template-columns: repeat(3, 1fr); }
  .log-summary > div { padding: 14px; }
  .log-summary > div:nth-child(3) { border-right: 0; }
  .log-summary > .refresh-state { grid-column: 1 / -1; border-top: 1px solid var(--border); }
  .log-filters { grid-template-columns: repeat(2, minmax(0, 1fr)); }
  .log-row { grid-template-columns: minmax(0, 1fr) 48px 80px; gap: 8px; }
  .log-message { grid-column: 1 / -1; }
  .log-panel { padding: 16px; }
}
</style>
