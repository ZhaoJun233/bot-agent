<script setup lang="ts">
import { computed, onBeforeUnmount, ref, useId, watch } from 'vue'
import type { ZbaClient, ZbaConfigurationFindResult, ZbaConfigurationReceipt } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { configurationApplyLabel, configurationCommitLabel, configurationStepLabel, receiptOutcome } from './receipt-notice'

const props = defineProps<{ client: ZbaClient; enabled: boolean; pending: boolean }>()
const emit = defineEmits<{ 'auth-expired': [error: UiError] }>()
const id = useId()
const key = ref('')
const busy = ref(false)
const result = ref<ZbaConfigurationFindResult | null>(null)
const error = ref<UiError | null>(null)
const allowed = computed(() => props.enabled && !props.pending)
const validKey = computed(() => {
  // Canonical ConfigurationKeyConverter format, including its Int64 expiry headroom.
  const parts = /^cfg1:([0-9a-f]{32}):([1-9][0-9]{0,18}):([0-9a-f]{32})$/.exec(key.value)
  return parts !== null && parts[1] !== '0'.repeat(32) && parts[3] !== '0'.repeat(32)
    && BigInt(parts[2]!) <= 9223372036854775807n - 86400n
})
const receipt = computed(() => result.value?.code === 0 ? result.value.receipt : undefined)
const outcome = computed(() => {
  if (!receipt.value) return null
  const summary = receiptOutcome(receipt.value)
  return receipt.value.applyState === 3
    ? { ...summary, detail: '该次应用记录了重启要求。请先重新读取当前状态；仅凭历史回执不能判断现在是否仍需重启。' }
    : summary
})
const resultMessage = computed(() => {
  switch (result.value?.code) {
    case 0: return '已找到原保存回执'
    case 14: return '当前查询范围内未找到这次保存回执。先读取当前配置，再判断下一步。'
    case 5: return '原操作键已过期，无法再查询回执。请读取当前配置并人工核对。'
    case 6: return '配置代际已变化，旧键无法再核对。请读取当前配置并人工核对。'
    default: return '查询未取得可确认的回执，原保存结果仍待核对。结果码：' + result.value?.code
  }
})
let generation = 0
function invalidate(): void {
  generation += 1
  busy.value = false
  result.value = null
  error.value = null
}
watch(key, invalidate, { flush: 'sync' })
watch(() => [props.client, props.enabled, props.pending], () => {
  invalidate()
  key.value = ''
}, { flush: 'sync' })
onBeforeUnmount(invalidate)

function verified(value: ZbaConfigurationReceipt | undefined, originalKey: string): boolean {
  return value !== undefined && value.key === originalKey
    && [0, 1].includes(value.decision) && [0, 1].includes(value.commitState)
    && (value.commitState === 1 || value.decision === 1)
    && [0, 1, 2, 3, 4, 5].includes(value.applyState)
    && value.steps.every(step => [0, 1, 2, 3, 4, 5].includes(step.state))
}
async function find(): Promise<void> {
  if (!allowed.value || busy.value || !validKey.value) return
  const owner = ++generation
  const client = props.client
  const originalKey = key.value
  busy.value = true
  result.value = null
  error.value = null
  try {
    const found = await client.findConfiguration(originalKey)
    if (owner !== generation) return
    if (found.code === 1) {
      const failure = { status: 403, code: 'configuration_forbidden', message: '当前身份没有查询权限。' }
      error.value = failure
      emit('auth-expired', failure)
    } else if (found.code === 0 && !verified(found.receipt, originalKey)) {
      error.value = { code: 'configuration_receipt_unverified', message: '回执缺失、操作键不符或状态不能确认，请保留原键继续核对。' }
    } else {
      result.value = found
    }
  } catch (cause) {
    if (owner !== generation) return
    const failure = toUiError(cause)
    error.value = failure
    if (failure.status === 401 || failure.status === 403) emit('auth-expired', failure)
  } finally {
    if (owner === generation) busy.value = false
  }
}
</script>

<template>
  <details class="inline-notice configuration-recovery" data-testid="config-recovery">
    <summary>核对先前的配置保存</summary>
    <p :id="id + '-help'">重新连接原服务、使用原身份后，粘贴自行保留的配置操作键。这里只查询原回执，不重新保存配置。</p>
    <form class="recovery-form" @submit.prevent="find">
      <label :for="id + '-key'">原配置操作键</label>
      <input :id="id + '-key'" v-model="key" data-testid="config-recovery-key" type="text" autocomplete="off" autocapitalize="off" :spellcheck="false" :disabled="!allowed" :aria-describedby="id + '-help ' + id + '-format'">
      <p :id="id + '-format'">请完整粘贴以 cfg1: 开头的原操作键；不会自动补齐或替换。</p>
      <p v-if="pending">当前保存仍在进行或等待核对，请先使用原来的“核对结果”入口。</p>
      <p v-else-if="!enabled">请先进入管理员模式，再核对原键。</p>
      <button type="submit" class="tonal" data-testid="config-recovery-find" :disabled="!allowed || !validKey || busy">{{ busy ? '正在核对…' : '核对原保存' }}</button>
    </form>
    <div aria-live="polite" aria-atomic="true">
      <p v-if="result" data-testid="config-recovery-code">{{ resultMessage }}</p>
      <section v-if="receipt && outcome" data-testid="config-recovery-receipt" :aria-labelledby="id + '-receipt-title'">
        <h2 :id="id + '-receipt-title'">该次提交的历史结果</h2>
        <p class="history-boundary">历史回执不代表当前配置值和运行状态。请使用页面上方的“重新读取”查看当前情况；本次查询不会清空草稿或解除其他未决操作。</p>
        <p :data-tone="outcome.tone"><strong>{{ outcome.title }}</strong> · {{ outcome.detail }}</p>
        <dl>
          <dt>提交状态</dt><dd>{{ configurationCommitLabel(receipt.commitState) }}</dd>
          <dt>该次持久化版本</dt><dd>{{ receipt.committedVersion ?? '—' }}</dd>
          <dt>该次应用状态</dt><dd>{{ configurationApplyLabel(receipt.applyState) }}</dd>
        </dl>
        <ol v-if="receipt.steps.length">
          <li v-for="step in receipt.steps" :key="step.step">{{ configurationStepLabel(step.step) }}：{{ configurationApplyLabel(step.state) }}<template v-if="step.errorCode"> · {{ step.errorCode }}</template></li>
        </ol>
      </section>
    </div>
    <ErrorNotice :error="error" title="核对未完成，原保存结果仍待确认" />
  </details>
</template>

<style scoped>
.configuration-recovery { min-width: 0; }
.recovery-form { display: grid; gap: 0.5rem; }
.recovery-form input { width: 100%; min-width: 0; box-sizing: border-box; }
.recovery-form button { justify-self: start; }
.configuration-recovery p, .configuration-recovery dd, .configuration-recovery li { overflow-wrap: anywhere; }
.configuration-recovery dd { margin: 0 0 0.6rem; }
.history-boundary { font-weight: 600; }
</style>
