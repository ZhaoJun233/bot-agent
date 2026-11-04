<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import type { ZbaClient, ZbaTracePage, ZbaTraceSummary } from '../../api/zba'
import { relativeTime } from '../../shared/friendly'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import AppIcon from '../../shared/ui/AppIcon.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'

const props = defineProps<{ client: ZbaClient; active?: boolean }>()

const page = ref<ZbaTracePage | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)
let unmounted = false
// 刷新可能重叠：只有最后一次读取可以写回状态。
let loadSequence = 0

const cache = computed(() => page.value?.cache ?? null)
const items = computed(() => page.value?.items ?? [])

const exactTime = new Intl.DateTimeFormat('zh-CN', { dateStyle: 'medium', timeStyle: 'medium', hour12: false })

function formatCount(value: number | null | undefined): string {
  return value === null || value === undefined ? '未知' : value.toLocaleString('zh-CN')
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

async function reload(): Promise<void> {
  const sequence = ++loadSequence
  loading.value = true
  error.value = null
  try {
    const result = await props.client.listTraces(20)
    if (unmounted || sequence !== loadSequence) return
    page.value = result
  } catch (cause) {
    if (unmounted || sequence !== loadSequence) return
    error.value = toUiError(cause)
  } finally {
    if (!unmounted && sequence === loadSequence) loading.value = false
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
      title="轨迹与缓存"
      title-id="traces-title"
      eyebrow="TURN TRACES"
      description="最近若干轮的耗时与 token 形状，以及进程级提示缓存用量；只读视图，不含任何会话内容。"
    >
      <div class="traces-actions">
        <span v-if="loading && page" class="muted" data-testid="traces-refreshing">正在刷新…</span>
        <button type="button" class="tonal" data-testid="traces-reload" :disabled="loading" @click="reload">
          <AppIcon name="refresh" :size="16" />
          {{ loading ? '正在读取…' : '刷新' }}
        </button>
      </div>
    </PageHeader>

    <ErrorNotice :error="error" title="轨迹读取失败" />

    <p v-if="loading && !page" class="empty-state" data-testid="traces-loading">正在读取最近的轨迹…</p>

    <p v-else-if="!page" class="empty-state" data-testid="traces-unavailable">
      还没有读取到轨迹数据，请点「刷新」重试。
    </p>

    <template v-else>
      <section class="panel traces-panel" aria-labelledby="traces-summary-title">
        <div class="panel-heading">
          <div>
            <h2 id="traces-summary-title">提示缓存用量</h2>
            <p>按进程统计；未知轮数单独计数，不与「0 命中」合并。</p>
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
            <p>进程内可保留的轮数</p>
          </article>
          <article class="traces-kpi" data-testid="traces-cached-tokens">
            <span>命中 token 合计</span>
            <strong>{{ formatCount(cache.cachedPromptTokens) }}</strong>
            <p>供应商上报的缓存命中</p>
          </article>
          <article class="traces-kpi" data-testid="traces-cache-write-tokens">
            <span>写入 token 合计</span>
            <strong>{{ formatCount(cache.cacheWriteTokens) }}</strong>
            <p>供应商上报的缓存写入</p>
          </article>
          <article class="traces-kpi traces-kpi--unknown" data-testid="traces-unknown-turns">
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
            <p>只展示形状：耗时、token 与节点计数，不含消息正文或工具载荷。</p>
          </div>
          <p v-if="items.length > 0" class="muted" data-testid="traces-count">共 {{ items.length }} 轮</p>
        </div>

        <p v-if="items.length === 0" class="empty-state" data-testid="traces-empty">
          还没有轨迹记录。机器人完成一轮对话后，这里会显示耗时与 token 形状。
        </p>

        <div v-else class="traces-table-wrap">
          <table class="traces-table">
            <thead>
              <tr>
                <th scope="col">时间</th>
                <th scope="col">结果</th>
                <th scope="col">总耗时</th>
                <th scope="col">prompt token</th>
                <th scope="col">completion token</th>
                <th scope="col">命中 token</th>
                <th scope="col">写入 token</th>
                <th scope="col">节点</th>
                <th scope="col">回退</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="trace in items" :key="trace.runId" data-testid="trace-row">
                <td><time :datetime="trace.startedAt" :title="timeTitle(trace.startedAt)">{{ relativeTime(trace.startedAt) }}</time></td>
                <td><span class="status-badge" :data-tone="outcomeTone(trace.outcome)">{{ outcomeName(trace.outcome) }}</span></td>
                <td class="mono">{{ formatCount(trace.totalMs) }} ms</td>
                <td class="mono">{{ formatCount(trace.promptTokens) }}</td>
                <td class="mono">{{ formatCount(trace.completionTokens) }}</td>
                <td class="mono">
                  <span v-if="cacheUnknown(trace)" class="trace-unknown" data-testid="trace-cache-unknown">未知</span>
                  <span v-else data-testid="trace-cache-hit">{{ formatCount(trace.cachedPromptTokens) }}</span>
                </td>
                <td class="mono">
                  <span v-if="trace.cacheWriteTokens === null || trace.cacheWriteTokens === undefined" class="trace-unknown">未知</span>
                  <span v-else data-testid="trace-cache-write">{{ formatCount(trace.cacheWriteTokens) }}</span>
                </td>
                <td class="mono">{{ formatCount(trace.nodes.length) }}</td>
                <td class="mono">{{ formatCount(trace.fallbackHops) }}</td>
              </tr>
            </tbody>
          </table>
        </div>

        <p class="traces-foot" data-testid="traces-footnote">
          只读视图：只展示轮次形状（耗时 / token / 未知轮数），不包含会话 key、消息正文或工具载荷。
        </p>
      </section>
    </template>
  </section>
</template>

<style scoped>
.traces { display: flex; flex-direction: column; gap: 20px; }
.traces-actions { display: flex; align-items: center; gap: 10px; }
.traces-panel { display: flex; flex-direction: column; gap: 16px; }
.traces-kpis { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 14px; }
.traces-kpi { display: grid; gap: 6px; padding: 14px 16px; border: 1px solid var(--border); border-radius: var(--radius); background: var(--surface-muted); }
.traces-kpi > span { font-size: 11px; color: var(--text-soft); }
.traces-kpi > strong { font: 600 22px/1.2 var(--font-display); letter-spacing: -.4px; overflow-wrap: anywhere; }
.traces-kpi > p { margin: 0; font-size: 11px; color: var(--text-muted); }
.traces-kpi--unknown { border-color: color-mix(in srgb, var(--warn-text) 28%, var(--border)); background: var(--warn-bg); }
.traces-kpi--unknown > span, .traces-kpi--unknown > p { color: var(--warn-text); }
.traces-table-wrap { overflow-x: auto; }
.traces-table { width: 100%; border-collapse: collapse; font-size: 12px; }
.traces-table th { padding: 0 14px 10px 0; border-bottom: 1px solid var(--border); text-align: left; white-space: nowrap; }
.traces-table td { padding: 11px 14px 11px 0; border-bottom: 1px solid var(--border); white-space: nowrap; }
.traces-table tbody tr:last-child td { border-bottom: 0; }
.trace-unknown { color: var(--text-soft); }
.traces-foot { margin: 0; color: var(--text-soft); font-size: 11px; line-height: 1.8; }
@media (max-width: 900px) { .traces-kpis { grid-template-columns: repeat(2, minmax(0, 1fr)); } }
@media (max-width: 560px) { .traces-kpis { grid-template-columns: 1fr; } .traces-actions { width: 100%; justify-content: space-between; } }
</style>
