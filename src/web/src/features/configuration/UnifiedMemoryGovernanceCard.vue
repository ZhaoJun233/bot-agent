<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaComponent } from '../../api/zba'
import StatusBadge, { type BadgeTone } from '../../shared/ui/StatusBadge.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

const props = withDefaults(defineProps<{
  client: ZbaClient
  active?: boolean
}>(), { active: true })

const emit = defineEmits<{
  'auth-expired': [error: UiError]
}>()

const loading = ref(false)
const error = ref<UiError | null>(null)
const component = ref<ZbaComponent | null>(null)
let seq = 0
let unmounted = false

interface MemoryShapeSummary {
  stateLabel: string
  tone: BadgeTone
  confirmedCount: number
  pendingCount: number
  suppressionCount: number
  adminConfigured: boolean
  revision: number
  metricsUnavailable: boolean
}

const summary = computed<MemoryShapeSummary | null>(() => {
  const c = component.value
  if (!c) return null
  const metricsUnavailable = c.reasonCode === 'governance_query_failed'
  const parts = new Map<string, string>()
  for (const item of (c.reasonCode ?? '').split(';')) {
    const idx = item.indexOf('=')
    if (idx > 0) parts.set(item.slice(0, idx), item.slice(idx + 1))
  }
  const num = (key: string): number => {
    const parsed = Number(parts.get(key) ?? '0')
    return Number.isFinite(parsed) && parsed >= 0 ? Math.trunc(parsed) : 0
  }
  const stateLabel = c.state === 'faulted'
    ? (metricsUnavailable ? '状态读取失败（Fail-Closed）' : '存储异常（Fail-Closed）')
    : c.state === 'enabled'
      ? '已启用'
      : '已停用'
  const tone: BadgeTone = c.state === 'faulted' ? 'bad' : c.state === 'enabled' ? 'ok' : 'neutral'
  return {
    stateLabel,
    tone,
    confirmedCount: num('confirmed'),
    pendingCount: num('pending'),
    suppressionCount: num('suppressed'),
    adminConfigured: parts.get('admin') === '1',
    revision: num('rev'),
    metricsUnavailable
  }
})

async function load(): Promise<void> {
  const currentSeq = ++seq
  const client = props.client
  loading.value = true
  error.value = null
  try {
    const status = await client.status()
    if (unmounted || currentSeq !== seq || client !== props.client) return
    component.value = status.components.find((item: ZbaComponent) => item.id === 'unified-memory') ?? null
  } catch (err) {
    if (unmounted || currentSeq !== seq || client !== props.client) return
    if (err instanceof ZbaRequestError && err.status === 401) {
      emit('auth-expired', toUiError(err))
      return
    }
    error.value = toUiError(err)
  } finally {
    if (!unmounted && currentSeq === seq) loading.value = false
  }
}

watch(() => [props.client, props.active] as const, ([, isActive]) => {
  if (isActive) void load()
})

onMounted(() => {
  if (props.active) void load()
})

onBeforeUnmount(() => { unmounted = true })

defineExpose({ reload: load })
</script>

<template>
  <section class="panel umem-card" data-testid="unified-memory-governance-card" aria-labelledby="umem-gov-title">
    <div class="panel-heading">
      <div>
        <h3 id="umem-gov-title">统一记忆只读治理（ADR-0004 / ADR-0005）</h3>
        <p class="umem-sub">
          仅展示聚合计数与治理态势；遵守隐私宪法，管理员控制台绝不提供他人候选或事实正文列表。
        </p>
      </div>
      <div class="umem-actions">
        <StatusBadge v-if="summary" :tone="summary.tone" :label="summary.stateLabel" />
        <button type="button" class="tonal small" :disabled="loading" @click="load">刷新状态</button>
      </div>
    </div>

    <p v-if="error" class="umem-err" data-testid="umem-gov-error">{{ error.message }}</p>

    <p v-else-if="summary?.metricsUnavailable" class="umem-err" data-testid="umem-gov-fault">
      统一记忆治理状态读取失败（Fail-Closed，安全计数暂不可用，请刷新重试或检查后端日志）。
    </p>

    <dl v-else-if="summary" class="umem-metrics" data-testid="umem-gov-metrics">
      <div class="umem-metric">
        <dt>已确认事实</dt>
        <dd data-testid="umem-confirmed-count">{{ summary.confirmedCount }}</dd>
      </div>
      <div class="umem-metric">
        <dt>待私聊确认</dt>
        <dd data-testid="umem-pending-count">{{ summary.pendingCount }}</dd>
      </div>
      <div class="umem-metric">
        <dt>最小化抑制标记</dt>
        <dd data-testid="umem-suppressed-count">{{ summary.suppressionCount }}</dd>
      </div>
      <div class="umem-metric">
        <dt>治理管理员凭据</dt>
        <dd data-testid="umem-admin-status">{{ summary.adminConfigured ? '已配置' : '未配置' }}</dd>
      </div>
    </dl>

    <p v-else class="umem-empty" data-testid="umem-gov-empty">
      {{ loading ? '正在读取治理状态…' : '当前宿主未返回统一记忆治理投影。' }}
    </p>

    <ul class="umem-rules">
      <li><strong>停用（Disable）</strong>：停止采集、提炼与模型注入，不删除已有记忆。</li>
      <li><strong>撤销共享（Revoke sharing）</strong>：撤回指定目标使用权，覆盖待发送输出；不影响原私有来源或原始聊天历史。</li>
      <li><strong>遗忘（Forget）</strong>：删除事实及派生检索数据并保留无内容抑制标记，防止旧材料再生或经旧画像回流。</li>
    </ul>
  </section>
</template>

<style scoped>
.umem-card {
  margin-bottom: var(--space-4, 1rem);
}
.umem-sub {
  margin: 0.25rem 0 0;
  color: var(--text-muted, #64748b);
  font-size: 0.875rem;
}
.umem-actions {
  display: flex;
  align-items: center;
  gap: 0.5rem;
}
.umem-metrics {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(140px, 1fr));
  gap: 0.75rem;
  margin: 0.75rem 0;
}
.umem-metric {
  padding: 0.625rem 0.75rem;
  border: 1px solid var(--border, #e2e8f0);
  border-radius: var(--radius, 8px);
}
.umem-metric dt {
  font-size: 0.75rem;
  color: var(--text-muted, #64748b);
}
.umem-metric dd {
  margin: 0.25rem 0 0;
  font-size: 1.125rem;
  font-weight: 600;
}
.umem-rules {
  margin: 0.5rem 0 0;
  padding-left: 1.25rem;
  font-size: 0.8125rem;
  color: var(--text-muted, #64748b);
}
.umem-err {
  color: var(--bad, #b91c1c);
  font-size: 0.875rem;
}
.umem-empty {
  color: var(--text-muted, #64748b);
  font-size: 0.875rem;
}
</style>
