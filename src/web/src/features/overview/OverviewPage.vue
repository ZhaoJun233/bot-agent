<script setup lang="ts">
import { computed } from 'vue'
import type { Compatibility } from '../../api/compat'
import type { ZbaHealth, ZbaMeta, ZbaStatus } from '../../api/zba'
import AppIcon from '../../shared/ui/AppIcon.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import BreakableId from '../../shared/ui/BreakableId.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import type { UiError } from '../../shared/ui/ui-error'

const props = defineProps<{
  health: ZbaHealth
  meta: ZbaMeta
  status: ZbaStatus
  compatibility: Compatibility
  stale: boolean
  error: UiError | null
  lastSuccessAt: string | null
  mode: 'read' | 'admin'
  adminError: UiError | null
  busy: boolean
}>()

const emit = defineEmits<{
  refresh: []
  'enable-admin': []
  'disable-admin': []
}>()

const components = computed(() => Array.isArray(props.status.components) ? props.status.components : [])
const recentErrors = computed(() => Array.isArray(props.status.recentErrors) ? props.status.recentErrors : [])
const availableCount = computed(() => components.value.filter((component) => component.available).length)
const lastSuccessLabel = computed(() => props.lastSuccessAt === null ? '尚未成功读取' : formatDate(props.lastSuccessAt))

const formatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'medium',
  hour12: false
})

function formatDate(value: string | undefined | null): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : formatter.format(date)
}
</script>

<template>
  <section class="overview" data-testid="overview" aria-labelledby="overview-title">
    <header class="ov-head">
      <div class="ov-head-main">
        <h1 id="overview-title">运行状态</h1>
        <p class="ov-sub">
          最后一次成功读取：<strong data-testid="last-success">{{ lastSuccessLabel }}</strong>
          <span class="ov-sub-sep" aria-hidden="true">·</span>
          契约 {{ props.meta.contractVersion }}
        </p>
      </div>
      <div class="ov-head-actions">
        <span class="ov-pill" :data-tone="props.mode === 'admin' ? 'accent' : 'neutral'" data-testid="mode-badge">
          <span class="ov-pill-dot" aria-hidden="true"></span>
          {{ props.mode === 'admin' ? '管理员模式' : '标准模式' }}
        </span>
        <button
          v-if="props.mode === 'read'"
          type="button"
          class="tonal"
          data-testid="enable-admin"
          :disabled="props.busy || !props.compatibility.writesEnabled"
          :title="props.compatibility.writesEnabled ? undefined : '契约不兼容，写入口已禁用'"
          @click="emit('enable-admin')"
        >
          管理员模式
        </button>
        <button v-else type="button" class="tonal" data-testid="disable-admin" @click="emit('disable-admin')">
          退出管理员模式
        </button>
        <button type="button" class="primary" data-testid="refresh" :disabled="props.busy" @click="emit('refresh')">
          {{ props.busy ? '刷新中…' : '刷新' }}
        </button>
      </div>
    </header>

    <p v-if="props.stale" class="inline-notice warn" data-testid="stale-banner" role="status">
      显示的是上次成功读取的数据；最近一次刷新失败，状态可能已过期。
    </p>

    <div
      v-if="!props.compatibility.compatible"
      class="inline-notice bad"
      data-testid="compatibility-warning"
      role="alert"
    >
      <strong>契约不兼容，写入口已禁用。</strong>
      <ul>
        <li v-for="reason in props.compatibility.reasons" :key="reason">{{ reason }}</li>
      </ul>
    </div>
    <div
      v-else-if="props.compatibility.warnings.length > 0"
      class="inline-notice warn"
      data-testid="compatibility-warning"
    >
      <strong>兼容性提示</strong>
      <ul>
        <li v-for="warning in props.compatibility.warnings" :key="warning">{{ warning }}</li>
      </ul>
    </div>

    <ErrorNotice :error="props.error" title="刷新失败" />
    <ErrorNotice :error="props.adminError" title="无法进入管理员模式" />

    <div class="ov-kpis">
      <article class="ov-card">
        <span class="ov-chip" aria-hidden="true"><AppIcon name="bolt" :size="18" /></span>
        <div class="ov-kpi-body">
          <p class="ov-kpi-label">总体状态</p>
          <p class="ov-kpi-value" data-testid="overview-state">{{ props.status.state }}</p>
          <div class="ov-kpi-foot">
            <StatusBadge :state="props.status.state" :available="props.status.ready" />
            <span>liveness {{ props.health.status }} · ready {{ props.health.ready }}</span>
          </div>
        </div>
      </article>

      <article class="ov-card">
        <span class="ov-chip" aria-hidden="true"><AppIcon name="overview" :size="18" /></span>
        <div class="ov-kpi-body">
          <p class="ov-kpi-label">组件</p>
          <p class="ov-kpi-value">{{ availableCount }} / {{ components.length }}</p>
          <p class="ov-kpi-foot">可用 / 已登记</p>
        </div>
      </article>

      <article class="ov-card">
        <span class="ov-chip" aria-hidden="true"><AppIcon name="config" :size="18" /></span>
        <div class="ov-kpi-body">
          <p class="ov-kpi-label">配置版本</p>
          <p class="ov-kpi-value ov-kpi-value--compact">{{ props.status.configurationVersion ?? '未记录' }}</p>
          <div class="ov-kpi-foot">
            <span>生效状态</span>
            <StatusBadge :state="props.status.configurationApplyState" />
          </div>
        </div>
      </article>

      <article class="ov-card">
        <span class="ov-chip" aria-hidden="true"><AppIcon name="alert" :size="18" /></span>
        <div class="ov-kpi-body">
          <p class="ov-kpi-label">最近错误</p>
          <p class="ov-kpi-value">{{ recentErrors.length }}</p>
          <p class="ov-kpi-foot">只显示后端允许的安全错误码</p>
        </div>
      </article>
    </div>

    <div class="ov-split">
      <section class="panel ov-panel" aria-labelledby="components-title">
        <div class="panel-heading ov-panel-head">
          <div>
            <h2 id="components-title">组件状态</h2>
            <p class="ov-panel-sub">共 {{ components.length }} 项 · 最近更新 {{ formatDate(props.status.updatedAt) }}</p>
          </div>
          <span
            class="ov-pill"
            :data-tone="components.length > 0 && availableCount === components.length ? 'ok' : 'warn'"
          >
            {{ availableCount }} / {{ components.length }} 可用
          </span>
        </div>

        <p v-if="components.length === 0" class="empty-state" data-testid="components-empty">
          后端尚未登记任何组件。
        </p>
        <div v-else class="table-scroll">
          <table data-testid="components">
            <caption class="sr-only">后端组件状态</caption>
            <thead>
              <tr>
                <th scope="col">组件</th>
                <th scope="col">类型</th>
                <th scope="col">状态</th>
                <th scope="col">原因码</th>
                <th scope="col">更新时间</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="component in components" :key="component.id">
                <td>
                  <strong>{{ component.displayName }}</strong>
                  <span class="cell-sub mono"><BreakableId :value="component.id" /></span>
                </td>
                <td>{{ component.kind }}</td>
                <td><StatusBadge :state="component.state" :available="component.available" /></td>
                <td>{{ component.reasonCode ?? '—' }}</td>
                <td>{{ formatDate(component.updatedAt) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <div class="ov-side">
        <section class="panel ov-panel" aria-labelledby="service-title">
          <div class="panel-heading ov-panel-head">
            <div>
              <h2 id="service-title">服务信息</h2>
              <p class="ov-panel-sub">版本、契约与实例</p>
            </div>
          </div>
          <dl class="ov-meta">
            <div><dt>服务版本</dt><dd>{{ props.meta.serverVersion }}</dd></div>
            <div><dt>契约版本</dt><dd class="mono">{{ props.meta.contractVersion }}</dd></div>
            <div><dt>实例</dt><dd class="mono">{{ props.meta.instanceId }}</dd></div>
            <div><dt>启动时间</dt><dd>{{ formatDate(props.status.startedAt) }}</dd></div>
            <div><dt>最近读取</dt><dd>{{ lastSuccessLabel }}</dd></div>
            <div><dt>认证 / 权限</dt><dd>{{ props.mode === 'admin' ? '管理员' : '标准' }}</dd></div>
          </dl>
          <p class="ov-hint">scope 是支持角色列表，不是当前调用者身份。</p>
        </section>

        <section class="panel ov-panel" aria-labelledby="errors-title">
          <div class="panel-heading ov-panel-head">
            <div>
              <h2 id="errors-title">最近错误</h2>
              <p class="ov-panel-sub">仅安全错误码，不含日志正文</p>
            </div>
          </div>
          <p v-if="recentErrors.length === 0" class="empty-state" data-testid="errors-empty">
            没有待处理错误。
          </p>
          <ul v-else class="error-list" data-testid="recent-errors">
            <li v-for="(item, index) in recentErrors" :key="`${item.at}-${item.scope}-${index}`">
              <span>{{ item.code }}</span>
              <span>{{ item.scope }}</span>
              <time>{{ formatDate(item.at) }}</time>
            </li>
          </ul>
        </section>
      </div>
    </div>
  </section>
</template>
