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
      <aside class="auth-story" aria-label="BotAgent 工作台">
        <div class="auth-brand-head">
          <span class="brand-badge auth-logo"><BrandMark :size="26" /></span>
          <div class="auth-wordmark"><strong>BotAgent</strong><span>AGENT CONSOLE</span></div>
        </div>
        <p class="eyebrow">YOUR AGENT, UNDER CONTROL</p>
        <h2>让能力有序协作，<br />让执行始终可控。</h2>
        <p>一个专注的工作空间，掌握机器人状态、连接工具与模型，管理 Agent 的每一步。</p>
        <ul class="auth-points">
          <li><AppIcon name="overview" :size="17" />运行状态与异常，一目了然</li>
          <li><AppIcon name="capabilities" :size="17" />插件、平台与模型，统一管理</li>
          <li><AppIcon name="shield" :size="17" />关键操作，经你审批</li>
        </ul>
        <div class="auth-story-foot">BOTAGENT / ZBA 1.0</div>
      </aside>
      <div class="auth-entry">
        <header class="auth-card-head">
          <p class="eyebrow">工作空间认证</p>
          <h1 id="auth-title">连接 ZBA</h1>
          <p class="auth-subtitle">欢迎回来。使用访问令牌连接你的 BotAgent 工作台。</p>
        </header>
        <form class="auth-form" :aria-busy="props.busy" @submit.prevent="submit">
          <label for="zha-token">访问令牌</label>
          <div class="input-shell">
            <AppIcon class="input-icon" name="lock" :size="17" />
            <input id="zha-token" ref="input" v-model="token" data-testid="token-input"
              type="password" name="token" autocomplete="off" spellcheck="false"
              placeholder="输入 Bearer 令牌" aria-describedby="auth-token-hint"
              :aria-invalid="props.error?.status === 401 ? 'true' : undefined" :disabled="props.busy" />
          </div>
          <p id="auth-token-hint" class="auth-hint">令牌仅保存在当前页面内存中。锁定或关闭页面后，需要重新认证。</p>
          <p v-if="props.busy" class="inline-notice" data-testid="auth-loading" role="status">正在检查 ZBA 连接…</p>
          <button type="submit" class="primary auth-submit" :class="{ 'auth-submit--busy': props.busy }"
            data-testid="connect-button" :disabled="props.busy || token.trim().length === 0">
            <span>{{ props.busy ? '连接中…' : '连接' }}</span><AppIcon v-if="!props.busy" name="arrow" :size="16" />
          </button>
          <ErrorNotice :error="props.error" title="无法连接" />
          <p v-if="props.notice" class="inline-notice warn" data-testid="auth-notice" role="status">{{ props.notice }}</p>
          <p v-if="props.configured === false && !props.error" class="inline-notice warn" data-testid="not-configured">
            后端尚未配置访问令牌。请先在服务器设置 <code>BOTAGENT_ZBA_TOKEN</code>（可选 <code>BOTAGENT_ZBA_READ_TOKEN</code>）后重试。
          </p>
          <p v-else-if="!props.error && props.contractVersion && props.contractVersion !== 'zba-1.0'"
            class="inline-notice warn" data-testid="contract-warning">
            后端契约版本为 {{ props.contractVersion }}，本页面按 zba-1.0 构建；写入口会被禁用。
          </p>
        </form>
        <details class="auth-token-help"><summary>在哪里获取访问令牌？</summary>
          <p>使用服务器配置的 <code>BOTAGENT_ZBA_TOKEN</code>；只读访问使用 <code>BOTAGENT_ZBA_READ_TOKEN</code>。连接后默认进入标准模式。</p>
        </details>
        <footer class="auth-card-foot"><AppIcon name="shield" :size="15" /><span>默认只读 · 写入前验证管理员权限</span></footer>
      </div>
    </div>
  </section>
</template>
