<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import AppIcon from '../../shared/ui/AppIcon.vue'
import BrandMark from '../../shared/ui/BrandMark.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import type { UiError } from '../../shared/ui/ui-error'

const props = defineProps<{
  configured: boolean | null
  contractVersion: string | null
  error: UiError | null
  busy: boolean
  /** 会话被动结束时的说明（例如未保存草稿已清除）。 */
  notice?: string | null
}>()

const emit = defineEmits<{
  connect: [token: string]
}>()

const token = ref('')
const input = ref<HTMLInputElement | null>(null)

onMounted(() => input.value?.focus())

/**
 * 输入框被 `disabled` 时浏览器会把焦点丢回 body，恢复可用后不会自己还回来。
 *
 * App 首帧就会发 health 探测，`busy` 随之变 true——于是"挂载时聚焦"紧接着
 * 就被这一次禁用抹掉，用户落地时焦点在 body 上（S4-F1，键盘/焦点门禁）。
 * 所以聚焦要跟着 `busy` 回到 false 再做一次；只在**焦点本来就在本表单里或
 * 已经丢了**时补，不抢用户主动点到别处的焦点。
 *
 * `flush: 'post'` 不能省：默认的 pre 时机在组件重渲染之前，那一刻 `:disabled`
 * 还没摘掉，对禁用控件调用 focus() 是无声的空操作——正是这个时序让"补聚焦"
 * 看起来生效了、实际没有。
 */
watch(() => props.busy, (busy) => {
  if (busy) return
  const active = document.activeElement as HTMLElement | null
  if (active && active !== document.body && active !== input.value && active.isConnected) return
  input.value?.focus()
}, { flush: 'post' })

function submit(): void {
  const value = token.value.trim()
  if (value.length > 0) emit('connect', value)
}
</script>

<template>
  <section class="auth-stage" data-testid="auth-gate" aria-labelledby="auth-title">
    <div class="auth-card">
      <header class="auth-card-head">
        <div class="auth-brand-head">
          <span class="brand-badge auth-logo"><BrandMark :size="34" /></span>
          <div class="auth-wordmark">
            <strong>BotAgent</strong>
            <span>ZBA 工作台</span>
          </div>
        </div>
        <h1 id="auth-title">连接 ZBA</h1>
        <p class="auth-subtitle">输入访问令牌以继续</p>
        <BrandMark class="auth-watermark" :size="150" />
      </header>

      <form class="auth-form" @submit.prevent="submit">
        <label for="zha-token">访问令牌</label>
        <div class="input-shell">
          <AppIcon class="input-icon" name="lock" :size="18" />
          <input
            id="zha-token"
            ref="input"
            v-model="token"
            data-testid="token-input"
            type="password"
            name="token"
            autocomplete="off"
            spellcheck="false"
            placeholder="输入 Bearer 令牌"
            :disabled="props.busy"
          />
        </div>

        <p v-if="props.busy" class="inline-notice" data-testid="auth-loading" role="status">
          正在检查 ZBA 连接…
        </p>
        <button
          type="submit"
          class="primary auth-submit"
          :class="{ 'auth-submit--busy': props.busy }"
          data-testid="connect-button"
          :disabled="props.busy || token.trim().length === 0"
        >
          <span>{{ props.busy ? '连接中…' : '连接' }}</span>
        </button>

        <ErrorNotice :error="props.error" title="无法连接" />

        <p v-if="props.notice" class="inline-notice warn" data-testid="auth-notice" role="status">
          {{ props.notice }}
        </p>

        <p v-if="props.configured === false && !props.error" class="inline-notice warn" data-testid="not-configured">
          后端尚未配置访问令牌。请先在服务器设置 <code>BOTAGENT_ZBA_TOKEN</code>（可选
          <code>BOTAGENT_ZBA_READ_TOKEN</code>）后重试。
        </p>
        <p
          v-else-if="!props.error && props.contractVersion && props.contractVersion !== 'zba-1.0'"
          class="inline-notice warn"
          data-testid="contract-warning"
        >
          后端契约版本为 {{ props.contractVersion }}，本页面按 zba-1.0 构建；写入口会被禁用。
        </p>
      </form>

      <footer class="auth-card-foot">
        <span>ZBA 1.0</span>
      </footer>
    </div>
  </section>
</template>
