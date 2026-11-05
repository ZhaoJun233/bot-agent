<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type { ZbaClient, ZbaRuntimeLogFeedEntry } from '../../api/zba'
import type { UiError } from '../../shared/ui/ui-error'
import AppIcon from '../../shared/ui/AppIcon.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'
import { useRuntimeLogFeed } from './use-runtime-log-feed'
import { captureLogAnchor, restoreLogAnchor, selectedLogText, viewportCopyText } from './runtime-log-viewport'

const props = defineProps<{ client: ZbaClient; active?: boolean; locked?: boolean }>()
const emit = defineEmits<{ 'auth-expired': [error: UiError] }>()
const { rows, error, receiving, loading, hidden, forbidden, retrySeconds, lastUpdated, notice, evicted, available, poll, clear } = useRuntimeLogFeed({
  client: () => props.client, active: () => props.active !== false, locked: () => props.locked === true,
  authExpired: failure => emit('auth-expired', failure)
})
const query = ref('')
const level = ref('all')
const source = ref('all')
const terminal = ref<HTMLElement>()
const following = ref(true)
const unreadIds = ref(new Set<string>())
const anchorNotice = ref('')
const copyNotice = ref('')
const sources = computed(() => [...new Set(rows.value.map(row => row.source))].sort())
const filtered = computed(() => {
  const text = query.value.trim().toLocaleLowerCase()
  return rows.value.filter(row => (level.value === 'all' || row.level === level.value)
    && (source.value === 'all' || row.source === source.value)
    && (!text || (row.source + ' ' + row.message).toLocaleLowerCase().includes(text)))
})
const unread = computed(() => filtered.value.filter(row => unreadIds.value.has(row.id)).length)
const labels = { info: '信息', warn: '警告', error: '错误' }
const codes = { info: 'INFO', warn: 'WARN', error: 'ERROR' }
const clock = new Intl.DateTimeFormat('zh-CN', { month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false })
function time(value: number): string { return clock.format(new Date(value)) }
function copyLine(row: ZbaRuntimeLogFeedEntry): string { return time(row.time) + ' [' + row.level.toUpperCase() + '] [' + row.source + '] ' + row.message }
function clearFilters(): void { query.value = ''; level.value = 'all'; source.value = 'all' }
function pauseFollow(): void { following.value = false }
function selectionChanged(): void { if (terminal.value && selectedLogText(terminal.value)) pauseFollow() }
function scrolled(): void {
  const element = terminal.value
  if (element && element.scrollHeight - element.scrollTop - element.clientHeight > 0) pauseFollow()
}
function jumpToLatest(): void {
  following.value = true
  unreadIds.value = new Set()
  anchorNotice.value = ''
  if (terminal.value) terminal.value.scrollTop = terminal.value.scrollHeight
}
function clearView(): void { clear(); unreadIds.value = new Set(); anchorNotice.value = ''; copyNotice.value = '' }
async function copyVisible(): Promise<void> {
  if (!available.value || !terminal.value) return
  const text = viewportCopyText(terminal.value)
  if (!text) { copyNotice.value = '当前视口没有可复制日志。'; return }
  const client = props.client
  try {
    await navigator.clipboard.writeText(text)
    if (available.value && client === props.client) copyNotice.value = '已复制选中文字或当前视口内的安全日志。'
  } catch { if (available.value) copyNotice.value = '无法访问剪贴板，请选中文字后手动复制。' }
}
watch(rows, (current, previous) => {
  const oldIds = new Set(previous.map(row => row.id))
  unreadIds.value = following.value ? new Set() : new Set(current.filter(row => unreadIds.value.has(row.id) || !oldIds.has(row.id)).map(row => row.id))
})
watch(filtered, async () => {
  const element = terminal.value
  if (!element) return
  const anchor = following.value ? undefined : captureLogAnchor(element)
  await nextTick()
  if (element !== terminal.value) return
  if (following.value) element.scrollTop = element.scrollHeight
  else if (anchor && !restoreLogAnchor(element, anchor)) anchorNotice.value = '阅读锚点已被筛选或 600 条窗口淘汰，请重新定位；没有自动跳至最新。'
})
watch(() => props.client, () => { clearFilters(); copyNotice.value = ''; anchorNotice.value = ''; following.value = true })
onMounted(() => document.addEventListener('selectionchange', selectionChanged))
onBeforeUnmount(() => document.removeEventListener('selectionchange', selectionChanged))
defineExpose({ reload: poll })
</script>

<template>
  <section class="runtime-logs-page" data-testid="runtime-logs-page">
    <PageHeader title-id="runtime-logs-title" title="运行日志" description="按时间从旧到新，每秒接收安全摘要。隐藏、离开或锁定面板时停止请求。" eyebrow="REALTIME / SAFE LOGS">
      <template #actions><button class="btn ghost" type="button" :disabled="loading || !available" @click="poll"><AppIcon name="refresh" />立即同步</button></template>
    </PageHeader>
    <aside class="logs-safety" role="note"><AppIcon name="shield" /><div><strong>只读安全视图</strong><span>未知内容以占位符显示；不提供原始日志。缓冲与页面最多 600 条，长行可在行内横向滚动。</span></div></aside>
    <div class="logs-toolbar">
      <label class="logs-search"><AppIcon name="logs" /><input v-model="query" placeholder="搜索安全摘要" aria-label="搜索日志" /></label>
      <label>级别<select v-model="level" aria-label="日志级别"><option value="all">全部级别</option><option value="info">信息</option><option value="warn">警告</option><option value="error">错误</option></select></label>
      <label>来源<select v-model="source" aria-label="日志来源"><option value="all">全部来源</option><option v-for="name in sources" :key="name" :value="name">{{ name }}</option></select></label>
      <label class="logs-receive"><input v-model="receiving" type="checkbox" :disabled="!available" />每秒接收</label>
    </div>
    <div class="logs-controls">
      <button class="btn ghost" type="button" :disabled="!available" @click="following ? pauseFollow() : jumpToLatest()">{{ following ? '暂停跟随' : '返回最新' }}<span v-if="!following && unread">（{{ unread }} 条未读）</span></button>
      <span>{{ following ? '跟随最新' : '阅读中 · 接收不受影响' }}</span>
      <button class="btn ghost" type="button" :disabled="!available" @click="clearView">清空本页</button>
      <button class="btn ghost" type="button" :disabled="!available || !filtered.length" @click="copyVisible">复制当前视口</button>
    </div>
    <ErrorNotice v-if="error" :error="error" :stale="rows.length > 0" />
    <p v-if="retrySeconds" class="logs-notice">连接暂时中断，保留安全缓存；{{ retrySeconds }} 秒后重试。</p>
    <p v-if="hidden || active === false || locked" class="logs-notice">页面不可用，已暂停请求。</p>
    <p v-if="notice" class="logs-notice" role="status">{{ notice }}</p>
    <p v-if="evicted" class="logs-notice">本页已淘汰 {{ evicted }} 条较早日志；未读数仅计算当前筛选下仍在缓存中的条目。</p>
    <p v-if="anchorNotice" class="logs-notice" role="status">{{ anchorNotice }}</p>
    <p v-if="copyNotice" class="logs-notice" role="status">{{ copyNotice }}</p>
    <div class="logs-panel">
      <div class="logs-panel-head">
        <span class="term-dots" aria-hidden="true"><i /><i /><i /></span>
        <span class="term-title">botagent@runtime: ~ — tail -f runtime.log</span>
        <span class="term-meta">{{ filtered.length }}/{{ rows.length }} 行 · {{ forbidden ? '无权限 · 已停止' : lastUpdated ? '同步于 ' + time(lastUpdated) : '等待首次同步' }}</span>
      </div>
      <div ref="terminal" class="logs-terminal" role="region" aria-label="运行日志终端，旧日志在上新日志在下" tabindex="0" data-testid="logs-terminal" @scroll="scrolled" @wheel="selectionChanged" @pointerup="selectionChanged" @keyup="selectionChanged">
        <div v-if="loading && !rows.length" class="logs-empty">正在连接日志流…</div>
        <div v-else-if="!rows.length" class="logs-empty"><strong>{{ forbidden ? '授权失效，缓存已清除' : '当前视图暂无日志' }}</strong><span>{{ forbidden ? '请重新解锁面板后查看。' : '只显示安全摘要，新日志将继续接收。' }}</span></div>
        <div v-else-if="!filtered.length" class="logs-empty"><strong>没有匹配的日志</strong><button class="btn ghost" @click="clearFilters">清除筛选</button></div>
        <div v-for="row in filtered" :key="row.id" class="log-row" :class="'level-' + row.level" :data-log-id="row.id" :data-copy-text="copyLine(row)" data-testid="log-row">
          <time :datetime="new Date(row.time).toISOString()">{{ time(row.time) }}</time><span class="log-level" :aria-label="labels[row.level]">{{ codes[row.level] }}</span><span class="log-source">{{ row.source }}</span>
          <code class="log-message" tabindex="0" :aria-label="'日志摘要：' + row.message">{{ row.message }}</code><span v-if="row.redacted" class="log-redacted" aria-label="安全摘要">已脱敏</span>
        </div>
        <div v-if="rows.length" class="term-prompt" aria-hidden="true"><span class="term-user">botagent@runtime</span>:<span class="term-path">~</span>$ tail -f runtime.log <span class="term-cursor" :class="{ live: receiving && available && !forbidden }" /></div>
      </div>
    </div>
    <p class="logs-footnote">上滚或选中文字只暂停跟随，不暂停接收。复制仅包含选中文字或视口内可见的安全行；清空不删除服务端记录。</p>
  </section>
</template>

<style scoped>
.runtime-logs-page{display:flex;flex-direction:column;gap:14px;min-width:0;width:100%}
.logs-safety{display:flex;align-items:flex-start;gap:10px;padding:13px 16px;border:1px solid color-mix(in srgb,var(--ok-text) 22%,transparent);border-radius:var(--radius-lg);background:var(--ok-bg);color:var(--ok-text)}
.logs-safety>div{display:flex;flex-direction:column;gap:4px}.logs-safety strong{font-size:13px}.logs-safety span{font-size:12px;line-height:1.6;color:var(--text-muted)}
.logs-toolbar,.logs-controls{display:flex;align-items:center;flex-wrap:wrap;gap:10px;min-width:0}.logs-toolbar>label{display:flex;align-items:center;gap:8px;font-size:12px;color:var(--text-muted);min-width:0}
.logs-search{position:relative;flex:1 1 210px}.logs-search>.app-icon{position:absolute;left:11px;width:16px}.logs-search input{width:100%;min-width:0;padding:10px 12px 10px 34px}.logs-toolbar input:not([type=checkbox]),.logs-toolbar select{min-height:40px;border:1px solid var(--border-strong);border-radius:var(--radius);background:var(--surface);color:var(--text)}.logs-toolbar select{max-width:100%;padding:8px 10px}.logs-receive input{accent-color:var(--accent)}
.logs-controls>span{font-size:12px;color:var(--text-muted);margin-right:auto}.logs-controls .btn{font-size:12px}.logs-notice,.logs-footnote{margin:0;color:var(--text-muted);font-size:12px;line-height:1.6;overflow-wrap:anywhere}
.logs-panel{min-width:0;overflow:hidden;border:1px solid var(--term-border);border-radius:var(--radius-lg);background:var(--term-bg);color:var(--term-text);box-shadow:var(--shadow-1)}
.logs-panel-head{display:flex;align-items:center;gap:12px;flex-wrap:wrap;padding:8px 14px;border-bottom:1px solid var(--term-border);background:var(--term-head);font-family:var(--font-mono);font-size:12px;color:var(--term-muted)}
.term-dots{display:inline-flex;gap:7px}.term-dots i{width:12px;height:12px;border-radius:50%;background:#ff5f56}.term-dots i:nth-child(2){background:#ffbd2e}.term-dots i:nth-child(3){background:#27c93f}
.term-title{flex:1 1 auto;min-width:0;text-align:center;color:var(--term-text);overflow-wrap:anywhere}.term-meta{color:var(--term-muted);font-variant-numeric:tabular-nums}
.logs-terminal{height:clamp(260px,52vh,680px);overflow-y:auto;overflow-x:hidden;min-width:0;padding:8px 0;overscroll-behavior:contain;overflow-anchor:none;outline-offset:-3px;scrollbar-color:var(--term-scroll) var(--term-bg);scrollbar-width:thin}
.log-row{display:grid;grid-template-columns:auto 5ch minmax(6ch,max-content) minmax(0,1fr) auto;align-items:baseline;gap:0 1ch;padding:1px 14px;font-family:var(--font-mono);font-size:13px;line-height:1.5;min-width:0}.log-row:hover{background:var(--term-hover)}
.log-row time{color:var(--term-time);white-space:nowrap;font-variant-numeric:tabular-nums}.log-row time::before{content:'['}.log-row time::after{content:']'}
.log-level{font-weight:700;color:var(--term-info)}.level-warn .log-level{color:var(--term-warn)}.level-error .log-level{color:var(--term-error)}.level-error .log-message{color:var(--term-error-text)}
.log-source{color:var(--term-source);overflow-wrap:anywhere}.log-source::after{content:':'}
.log-message{display:block;min-width:0;max-width:100%;overflow-x:auto;white-space:pre;font:inherit;color:var(--term-text);background:transparent;padding:0;scrollbar-width:thin}
.log-redacted{font-size:11px;color:var(--term-muted)}.log-redacted::before{content:'#'}
.term-prompt{padding:2px 14px;font-family:var(--font-mono);font-size:13px;line-height:1.5;color:var(--term-text)}.term-user{color:var(--term-user);font-weight:700}.term-path{color:var(--term-path);font-weight:700}
.term-cursor{display:inline-block;width:.6em;height:1.1em;vertical-align:text-bottom;background:var(--term-muted)}.term-cursor.live{background:var(--term-text);animation:term-blink 1s steps(1) infinite}
@keyframes term-blink{50%{opacity:0}}@media(prefers-reduced-motion:reduce){.term-cursor.live{animation:none}}
.logs-empty{display:flex;min-height:230px;align-items:center;justify-content:center;flex-direction:column;gap:10px;padding:24px;text-align:center;color:var(--term-muted);font-family:var(--font-mono);font-size:13px}.logs-empty strong{color:var(--term-text);font-size:15px}
@media(max-width:700px){.logs-toolbar{align-items:stretch}.logs-search{flex-basis:100%}.logs-toolbar select,.logs-toolbar input:not([type=checkbox]),.logs-controls .btn,.logs-receive{min-height:44px}.logs-controls>span{flex-basis:100%}.log-row{grid-template-columns:auto auto minmax(0,1fr) auto;gap:0 1ch;padding:3px 10px;font-size:12px}.log-message{grid-column:1/-1}.logs-panel-head{padding:8px 10px}.term-title{text-align:left}.term-prompt{padding:2px 10px;font-size:12px}}
</style>
