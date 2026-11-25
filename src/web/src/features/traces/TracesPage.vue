<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ZbaRequestError, type ZbaClient, type ZbaTracePage, type ZbaTraceSummary } from '../../api/zba'
import { relativeTime } from '../../shared/friendly'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import AppIcon from '../../shared/ui/AppIcon.vue'
import BreakableId from '../../shared/ui/BreakableId.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import UiTabs, { type UiTabOption } from '../../shared/ui/UiTabs.vue'

const props = defineProps<{ client: ZbaClient; active?: boolean }>()
const emit = defineEmits<{ 'auth-expired': [error: UiError] }>()

const headerRef = ref<InstanceType<typeof PageHeader> | null>(null)
const pageHeading = computed(() => headerRef.value?.heading ?? null)

const page = ref<ZbaTracePage | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)
const limit = ref<20 | 50 | 100>(20)
const outcomeFilter = ref<'all' | 'replied' | 'failed' | 'other'>('all')
const lastLoadedAt = ref<string | null>(null)
const selectedTrace = ref<ZbaTraceSummary | null>(null)
const selectedTrigger = ref<HTMLElement | null>(null)

let unmounted = false
let loadSequence = 0

const cache = computed(() => page.value?.cache ?? null)
const items = computed(() => page.value?.items ?? [])

const filterOptions = computed<UiTabOption<'all' | 'replied' | 'failed' | 'other'>[]>(() => {
  const replied = items.value.filter(t => ['replied', 'ok', 'success'].includes(t.outcome.toLowerCase())).length
  const failed = items.value.filter(t => ['failed', 'error'].includes(t.outcome.toLowerCase())).length
  const other = items.value.length - replied - failed
  return [
    { value: 'all', label: '全部', count: items.value.length },
    { value: 'replied', label: '已回复', count: replied },
    { value: 'failed', label: '失败', count: failed },
    { value: 'other', label: '其他', count: other }
  ]
})

const visibleItems = computed(() => {
  if (outcomeFilter.value === 'all') return items.value
  return items.value.filter(trace => {
    const value = trace.outcome.toLowerCase()
    const isReplied = value === 'replied' || value === 'ok' || value === 'success'
    const isFailed = value === 'failed' || value === 'error'
    if (outcomeFilter.value === 'replied') return isReplied
    if (outcomeFilter.value === 'failed') return isFailed
    return !isReplied && !isFailed
  })
})

const exactTime = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'medium', hour12: false })

function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? '未知' : value.toLocaleString('zh-CN')
}

function formatMs(value: number | null | undefined): string {
  return value === null || value === undefined ? '未知' : `${value.toLocaleString('zh-CN')} ms`
}

function timeTitle(value: string): string {
  const at = new Date(value)
  return Number.isNaN(at.getTime()) ? value : exactTime.format(at)
}

/**
 * 缓存用量是三态：字段缺失 / null / cacheUsageUnknown 都表示供应商没报，
 * 必须显示「未知」——不能和「0 命中」混在一起。
 */
function cacheUnknown(trace: ZbaTraceSummary): boolean {
  return trace.cacheUsageUnknown || trace.cachedPromptTokens === null || trace.cachedPromptTokens === undefined
}

function cacheHitRate(trace: ZbaTraceSummary): string {
  if (cacheUnknown(trace) || trace.promptTokens === null || trace.promptTokens === undefined || trace.promptTokens <= 0) {
    return '未知'
  }
  const rate = Math.min(100, Math.max(0, Math.round(((trace.cachedPromptTokens ?? 0) / trace.promptTokens) * 100)))
  return `${rate}%`
}

const outcomeNames: Record<string, string> = {
  replied: '已回复',
  failed: '失败',
  cancelled: '已取消',
  skipped: '跳过',
  expired: '已超时',
  unknown: '未知'
}

function outcomeName(outcome: string): string {
  return outcomeNames[outcome.toLowerCase()] ?? (outcome || '未知')
}

function outcomeTone(outcome: string): 'ok' | 'warn' | 'bad' {
  const value = outcome.toLowerCase()
  if (value === 'replied' || value === 'ok' || value === 'success') return 'ok'
  if (value === 'failed' || value === 'error') return 'bad'
  return 'warn'
}

function nodeTone(status: string): 'ok' | 'warn' | 'bad' | 'neutral' {
  const value = status.toLowerCase()
  if (value === 'ok' || value === 'success' || value === 'done' || value === 'completed') return 'ok'
  if (value === 'failed' || value === 'error') return 'bad'
  if (value === 'skipped' || value === 'cancelled') return 'neutral'
  return 'warn'
}

function openTraceDetail(trace: ZbaTraceSummary, event?: Event): void {
  selectedTrigger.value = event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
  selectedTrace.value = trace
}

function closeTraceDetail(): void {
  selectedTrace.value = null
  selectedTrigger.value = null
}

async function reload(): Promise<void> {
  const sequence = ++loadSequence
  loading.value = true
  error.value = null
  try {
    const result = await props.client.listTraces(limit.value)
    if (unmounted || sequence !== loadSequence) return
    page.value = result
    lastLoadedAt.value = new Date().toISOString()
  } catch (cause) {
    if (unmounted || sequence !== loadSequence) return
    const ui = toUiError(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) {
      emit('auth-expired', ui)
      return
    }
    error.value = ui
  } finally {
    if (!unmounted && sequence === loadSequence) loading.value = false
  }
}

function changeLimit(event: Event): void {
  const parsed = Number((event.target as HTMLSelectElement).value)
  if (parsed === 20 || parsed === 50 || parsed === 100) {
    limit.value = parsed
    void reload()
  }
}

onMounted(() => { void reload() })
onBeforeUnmount(() => {
  unmounted = true
  loadSequence += 1
})

defineExpose({ reload })
</script>

<template>
  <section class="traces" data-testid="traces" aria-labelledby="traces-title">
    <PageHeader
      ref="headerRef"
      title="轨迹与缓存"
      title-id="traces-title"
      eyebrow="TURN TRACES"
      description="观测最近轮次的耗时、token 用量、缓存命中率与节点执行分布。"
    >
      <template #metadata>
        <span v-if="lastLoadedAt">更新于 {{ relativeTime(lastLoadedAt) }}</span>
        <span v-else>进程级只读指标</span>
      </template>
      <div class="traces-actions">
        <label class="traces-limit">
          <span class="sr-only">拉取条数</span>
          <select :value="limit" data-testid="traces-limit" :disabled="loading" @change="changeLimit">
            <option :value="20">最近 20 轮</option>
            <option :value="50">最近 50 轮</option>
            <option :value="100">最近 100 轮</option>
          </select>
        </label>
        <button type="button" class="tonal" data-testid="traces-reload" :disabled="loading" @click="reload">
          <AppIcon name="refresh" :size="15" />
          {{ loading ? '正在读取…' : '刷新' }}
        </button>
      </div>
    </PageHeader>

    <ErrorNotice :error="error" title="轨迹读取失败" />
    <p v-if="error && page" class="inline-notice warn" role="status">
      本次刷新失败，以下仍为上次成功读取的缓存结果。
    </p>

    <p v-if="loading && !page" class="empty-state" data-testid="traces-loading">正在读取最近的轨迹…</p>

    <p v-else-if="!page" class="empty-state" data-testid="traces-unavailable">
      还没有读取到轨迹数据，请点「刷新」重试。
    </p>

    <template v-else>
      <section class="panel traces-panel" aria-labelledby="traces-summary-title">
        <div class="panel-heading">
          <div>
            <h2 id="traces-summary-title">提示缓存用量</h2>
            <p>按当前进程统计；「未知轮数」单独统计供应商未上报用量的轮次，不与 0 命中合并。</p>
          </div>
        </div>
        <div v-if="cache" class="traces-kpis">
          <article class="traces-kpi" data-testid="traces-done-turns">
            <span>完成轮数</span>
            <strong>{{ formatCount(cache.doneTurns) }}</strong>
            <p>已有结果的轮次</p>
          </article>
          <article class="traces-kpi" data-testid="traces-active-turns">
            <span>进行中</span>
            <strong>{{ formatCount(cache.activeTurns) }}</strong>
            <p>正在生成的轮次</p>
          </article>
          <article class="traces-kpi" data-testid="traces-capacity">
            <span>缓存容量</span>
            <strong>{{ formatCount(cache.capacity) }}</strong>
            <p>进程内保留上限</p>
          </article>
          <article class="traces-kpi" data-testid="traces-cached-tokens">
            <span>命中 token 合计</span>
            <strong>{{ formatCount(cache.cachedPromptTokens) }}</strong>
            <p>模型供应商上报命中</p>
          </article>
          <article class="traces-kpi" data-testid="traces-cache-write-tokens">
            <span>写入 token 合计</span>
            <strong>{{ formatCount(cache.cacheWriteTokens) }}</strong>
            <p>模型供应商上报写入</p>
          </article>
          <article
            class="traces-kpi"
            :class="{ 'traces-kpi--unknown': (cache.cacheUnknownTurns ?? 0) > 0 }"
            data-testid="traces-unknown-turns"
          >
            <span>未知轮数</span>
            <strong>{{ formatCount(cache.cacheUnknownTurns) }}</strong>
            <p>供应商没报缓存用量的轮数</p>
          </article>
        </div>
      </section>

      <section class="panel traces-panel" aria-labelledby="traces-list-title">
        <div class="panel-heading">
          <div>
            <h2 id="traces-list-title">最近轮次</h2>
            <p>点击「节点明细」可查看该轮每个执行节点的耗时、工具与状态。</p>
          </div>
          <p v-if="items.length > 0" class="muted" data-testid="traces-count">共 {{ visibleItems.length }} / {{ items.length }} 轮</p>
        </div>

        <UiTabs
          v-if="items.length > 0"
          :model-value="outcomeFilter"
          :options="filterOptions"
          variant="pills"
          label="按结果筛选轮次"
          @update:model-value="outcomeFilter = $event as 'all' | 'replied' | 'failed' | 'other'"
        />

        <p v-if="items.length === 0" class="empty-state" data-testid="traces-empty">
          还没有轨迹记录。机器人完成一轮对话后，这里会显示耗时与 token 形状。
        </p>
        <p v-else-if="visibleItems.length === 0" class="empty-state">
          当前筛选下没有匹配的轮次。
        </p>

        <div v-else class="traces-table-wrap" role="region" aria-label="轨迹与用量明细，可横向滚动" tabindex="0">
          <table class="traces-table">
            <thead>
              <tr>
                <th scope="col">时间</th>
                <th scope="col">结果</th>
                <th scope="col">总耗时</th>
                <th scope="col">输入 Token</th>
                <th scope="col">输出 Token</th>
                <th scope="col">缓存命中</th>
                <th scope="col">缓存写入</th>
                <th scope="col">回退</th>
                <th scope="col">节点</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="trace in visibleItems" :key="trace.runId" data-testid="trace-row">
                <td><time :datetime="trace.startedAt" :title="timeTitle(trace.startedAt)">{{ relativeTime(trace.startedAt) }}</time></td>
                <td><StatusBadge :tone="outcomeTone(trace.outcome)" :label="outcomeName(trace.outcome)" dot /></td>
                <td class="mono">{{ formatMs(trace.totalMs) }}</td>
                <td class="mono">{{ formatCount(trace.promptTokens) }}</td>
                <td class="mono">{{ formatCount(trace.completionTokens) }}</td>
                <td class="mono">
                  <span v-if="cacheUnknown(trace)" class="trace-unknown" data-testid="trace-cache-unknown">未知</span>
                  <template v-else>
                    <span data-testid="trace-cache-hit">{{ formatCount(trace.cachedPromptTokens) }}</span>
                    <span class="hit-rate"> ({{ cacheHitRate(trace) }})</span>
                  </template>
                </td>
                <td class="mono">
                  <span v-if="trace.cacheWriteTokens === null || trace.cacheWriteTokens === undefined" class="trace-unknown">未知</span>
                  <span v-else data-testid="trace-cache-write">{{ formatCount(trace.cacheWriteTokens) }}</span>
                </td>
                <td class="mono">{{ formatCount(trace.fallbackHops) }}</td>
                <td>
                  <button
                    type="button"
                    class="link-button"
                    data-testid="trace-nodes-open"
                    @click="openTraceDetail(trace, $event)"
                  >
                    {{ formatCount(trace.nodes.length) }} 节点明细
                  </button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>

        <p class="traces-foot" data-testid="traces-footnote">
          只读隐私视图：仅展示形状指标（耗时 / token / 节点元数据），不包含会话 key、消息正文或工具载荷。
        </p>
      </section>
    </template>

    <ManagementDialog
      v-if="selectedTrace"
      title="轮次节点明细"
      title-id="trace-detail-title"
      test-id="trace-detail-drawer"
      :trigger="selectedTrigger"
      :fallback="pageHeading"
      drawer
      @close="closeTraceDetail"
    >
      <template #eyebrow><span class="eyebrow">TRACE BREAKDOWN</span></template>
      <div class="trace-drawer-body">
        <dl class="trace-drawer-meta">
          <div><dt>运行 ID</dt><dd class="mono"><BreakableId :value="selectedTrace.runId" copyable /></dd></div>
          <div><dt>开始时间</dt><dd>{{ timeTitle(selectedTrace.startedAt) }}</dd></div>
          <div><dt>结果</dt><dd><StatusBadge :tone="outcomeTone(selectedTrace.outcome)" :label="outcomeName(selectedTrace.outcome)" dot /></dd></div>
          <div><dt>总耗时</dt><dd class="mono">{{ formatMs(selectedTrace.totalMs) }}</dd></div>
          <div><dt>缓存命中率</dt><dd class="mono">{{ cacheHitRate(selectedTrace) }}</dd></div>
          <div><dt>模型回退次数</dt><dd class="mono">{{ formatCount(selectedTrace.fallbackHops) }}</dd></div>
        </dl>

        <h3 class="nodes-heading">执行节点（{{ selectedTrace.nodes.length }}）</h3>
        <p v-if="selectedTrace.nodes.length === 0" class="muted">本轮未记录子节点详情。</p>
        <ol v-else class="node-list" data-testid="trace-nodes-list">
          <li v-for="(node, idx) in selectedTrace.nodes" :key="idx" class="node-item">
            <div class="node-head">
              <strong>{{ node.kind }}<template v-if="node.toolId"> · <code class="mono">{{ node.toolId }}</code></template></strong>
              <StatusBadge :tone="nodeTone(node.status)" :label="node.status" />
            </div>
            <div class="node-meta mono">
              <span>耗时 {{ formatMs(node.durationMs) }}</span>
              <span v-if="node.count && node.count > 1">× {{ node.count }}</span>
              <span v-if="node.reasonCode">原因：{{ node.reasonCode }}</span>
            </div>
          </li>
        </ol>
      </div>
      <template #footer>
        <button type="button" @click="closeTraceDetail">关闭</button>
      </template>
    </ManagementDialog>
  </section>
</template>

<style scoped>
.traces { display: flex; flex-direction: column; gap: 20px; }
.traces-actions { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
.traces-limit select { min-height: 36px; font-size: var(--text-sm); }
.traces-panel { display: flex; flex-direction: column; gap: 14px; }
.traces-kpis { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; }
.traces-kpi { display: grid; gap: 5px; padding: 14px 16px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface-muted); }
.traces-kpi > span { font-size: var(--text-xs); color: var(--text-soft); font-weight: 500; }
.traces-kpi > strong { font: 600 1.375rem/1.2 var(--font-display); letter-spacing: -0.02em; overflow-wrap: anywhere; }
.traces-kpi > p { margin: 0; font-size: var(--text-xs); color: var(--text-muted); }
.traces-kpi--unknown { border-color: var(--border); background: var(--warn-bg); }
.traces-kpi--unknown > span, .traces-kpi--unknown > p, .traces-kpi--unknown > strong { color: var(--warn-text); }
.traces-table-wrap { overflow-x: auto; }
.traces-table { width: 100%; border-collapse: collapse; font-size: var(--text-sm); }
.traces-table th { padding: 10px 14px 10px 0; border-bottom: 1px solid var(--border); text-align: left; white-space: nowrap; background: transparent; }
.traces-table td { padding: 11px 14px 11px 0; border-bottom: 1px solid var(--border-subtle); white-space: nowrap; }
.traces-table tbody tr:last-child td { border-bottom: 0; }
.trace-unknown, .hit-rate { color: var(--text-soft); font-size: var(--text-xs); }
.traces-foot { margin: 0; color: var(--text-soft); font-size: var(--text-xs); line-height: 1.65; }
.trace-drawer-body { display: grid; gap: 16px; }
.trace-drawer-meta { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 10px 14px; margin: 0; padding: 12px 14px; border-radius: var(--radius); background: var(--surface-muted); }
.trace-drawer-meta dt { font-size: var(--text-xs); color: var(--text-soft); }
.trace-drawer-meta dd { margin: 3px 0 0; font-size: var(--text-sm); overflow-wrap: anywhere; }
.nodes-heading { margin: 4px 0 0; font-size: var(--text-md); font-weight: 600; }
.node-list { list-style: none; padding: 0; margin: 0; display: grid; gap: 10px; }
.node-item { padding: 12px 14px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface); display: grid; gap: 6px; }
.node-head { display: flex; justify-content: space-between; align-items: center; gap: 8px; flex-wrap: wrap; font-size: var(--text-sm); }
.node-meta { display: flex; flex-wrap: wrap; gap: 10px; font-size: var(--text-xs); color: var(--text-muted); }
@media (max-width: 900px) { .traces-kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 560px) { .traces-kpis, .trace-drawer-meta { grid-template-columns: 1fr; } .traces-actions { width: 100%; justify-content: space-between; } }
</style>
