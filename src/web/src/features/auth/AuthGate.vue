<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import AppIcon from '../../shared/ui/AppIcon.vue'
import BrandMark from '../../shared/ui/BrandMark.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import type { UiError } from '../../shared/ui/ui-error'

const props = withDefaults(defineProps<{
  configured: boolean | null
  contractVersion: string | null
  error: UiError | null
  busy: boolean
  probing?: boolean
  /** 会话被动结束时的说明（例如未保存草稿已清除）。 */
  notice?: string | null
}>(), { probing: false, notice: null })

const emit = defineEmits<{
  connect: [token: string]
}>()

const token = ref('')
const editedAfterError = ref(false)
const input = ref<HTMLInputElement | null>(null)
const visibleError = computed(() => (editedAfterError.value && props.error?.status === 401 ? null : props.error))

watch(() => props.error, () => {
  editedAfterError.value = false
})

onMounted(() => input.value?.focus())

watch(() => props.busy, (busy) => {
  if (busy) return
  const active = document.activeElement as HTMLElement | null
  if (active && active !== document.body && active !== input.value && active.isConnected) return
  input.value?.focus()
}, { flush: 'post' })

function onInput(): void {
  if (props.error?.status === 401) editedAfterError.value = true
}

function submit(): void {
  const value = token.value.trim()
  if (value.length > 0) {
    editedAfterError.value = false
    emit('connect', value)
  }
}
</script>

<template>
  <section class="auth-stage" data-testid="auth-gate" aria-labelledby="auth-title">
    <div class="auth-card">
      <aside class="auth-story" aria-label="BotAgent 控制台">
        <div class="auth-brand-head">
          <span class="brand-badge auth-logo"><BrandMark :size="26" /></span>
          <div class="auth-wordmark"><strong>BotAgent</strong><span>AGENT CONSOLE</span></div>
        </div>
        <p class="eyebrow">YOUR AGENT, UNDER CONTROL</p>
        <h2>模型、平台与插件，<br />各归其位、清晰可控。</h2>
        <p>面向 BotAgent 的现代管理控制台，分离管理模型供应商、消息平台驱动与扩展插件，让运行状态和关键审批始终透明。</p>
        <ul class="auth-points">
          <li><AppIcon name="provider" :size="17" />模型供应商路由与凭据，独立集中管理</li>
          <li><AppIcon name="platform" :size="17" />QQ / 飞书等消息平台驱动与白名单策略一目了然</li>
          <li><AppIcon name="plugin" :size="17" />功能插件、沙箱界面与市场扩展按需开关</li>
          <li><AppIcon name="shield" :size="17" />默认只读进入，敏感写操作与工具调用显式审批</li>
        </ul>
        <div class="auth-story-foot">BOTAGENT CONSOLE · {{ props.contractVersion || 'zba-1.0' }}</div>
      </aside>

      <div class="auth-entry">
        <header class="auth-card-head">
          <p class="eyebrow">控制台认证</p>
          <h1 id="auth-title">连接 BotAgent</h1>
          <p class="auth-subtitle">输入访问令牌以连接你的 BotAgent 服务端，默认以安全只读模式进入。</p>
        </header>

        <form class="auth-form" :aria-busy="props.busy" @submit.prevent="submit">
          <label for="botagent-token">访问令牌</label>
          <div class="input-shell">
            <AppIcon class="input-icon" name="lock" :size="17" />
            <input
              id="botagent-token"
              ref="input"
              v-model="token"
              data-testid="token-input"
              type="password"
              name="token"
              autocomplete="off"
              spellcheck="false"
              placeholder="输入管理员或只读访问令牌"
              aria-describedby="auth-token-hint"
              :aria-invalid="visibleError?.status === 401 ? 'true' : undefined"
              :disabled="props.busy"
              @input="onInput"
            />
          </div>
          <p id="auth-token-hint" class="auth-hint">令牌仅暂存于当前页面内存中，绝不写入本地存储；锁定或关闭标签页后自动清除。</p>

          <p v-if="props.busy" class="inline-notice" data-testid="auth-loading" role="status">正在验证访问令牌…</p>
          <p v-else-if="props.probing" class="inline-notice" data-testid="auth-probing" role="status">正在检查服务端状态，现在即可输入令牌…</p>

          <button
            type="submit"
            class="primary auth-submit"
            :class="{ 'auth-submit--busy': props.busy }"
            data-testid="connect-button"
            :disabled="props.busy || token.trim().length === 0"
          >
            <span>{{ props.busy ? '连接中…' : '连接控制台' }}</span>
            <AppIcon v-if="!props.busy" name="arrow" :size="16" />
          </button>

          <ErrorNotice :error="visibleError" title="无法连接" />
          <p v-if="props.notice" class="inline-notice warn" data-testid="auth-notice" role="status">{{ props.notice }}</p>
          <p v-if="props.configured === false && !visibleError" class="inline-notice warn" data-testid="not-configured">
            后端尚未配置访问令牌。请先在服务器设置 <code>BOTAGENT_ZBA_TOKEN</code>（可选 <code>BOTAGENT_ZBA_READ_TOKEN</code>）后重试。
          </p>
          <p v-else-if="!visibleError && props.contractVersion && props.contractVersion !== 'zba-1.0'"
            class="inline-notice warn" data-testid="contract-warning">
            后端契约版本为 {{ props.contractVersion }}，本页面按 zba-1.0 构建；写入口会被禁用。
          </p>
        </form>

        <details class="auth-token-help">
          <summary>在哪里获取访问令牌？</summary>
          <p>管理操作使用服务器环境变量 <code>BOTAGENT_ZBA_TOKEN</code>；仅查看运行状态可使用 <code>BOTAGENT_ZBA_READ_TOKEN</code>。</p>
        </details>

        <footer class="auth-card-foot">
          <AppIcon name="shield" :size="15" />
          <span>默认只读 · 修改配置或执行动作前需显式切换管理员模式</span>
        </footer>
      </div>
    </div>
  </section>
</template>
