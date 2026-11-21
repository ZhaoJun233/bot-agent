<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ZbaClient } from '../../api/zba'
import type { OperationFindResult } from '../../api/operation-recovery'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

const props = defineProps<{ client: ZbaClient; enabled: boolean }>()
const emit = defineEmits<{ 'auth-expired': [error: UiError] }>()
const owner = ref('')
const key = ref('')
const busy = ref(false)
const result = ref<OperationFindResult | null>(null)
const error = ref<UiError | null>(null)

const ownerValid = computed(() =>
  /^[a-z][a-z0-9.-]{0,79}$/.test(owner.value)
  && !owner.value.includes('..')
  && !owner.value.endsWith('.')
)
const keyValid = computed(() =>
  key.value.length >= 8
  && key.value.length <= 256
  && [...key.value].every(character => character >= ' ' && character <= '~' && character !== '/' && character.charCodeAt(0) !== 92)
)
const valid = computed(() => ownerValid.value && keyValid.value)

const formatHint = computed(() => {
  if (!owner.value && !key.value) return ''
  if (owner.value && !ownerValid.value) return '插件 ID 须为小写字母开头，仅含小写字母、数字、点或短横线。'
  if (key.value && !keyValid.value) return '原操作标识长度需在 8–256 个可见 ASCII 字符内，且不能包含斜杠。'
  return ''
})

let generation = 0
function invalidate(): void { generation += 1; busy.value = false; result.value = null; error.value = null }
watch([owner, key], invalidate, { flush: 'sync' })
watch(() => [props.client, props.enabled], () => { invalidate(); owner.value = ''; key.value = '' }, { flush: 'sync' })
onBeforeUnmount(invalidate)

async function find(): Promise<void> {
  if (!props.enabled || !valid.value || busy.value) return
  const current = ++generation
  const client = props.client
  busy.value = true
  result.value = null
  error.value = null
  try {
    const found = await client.findOperation(owner.value, key.value)
    if (current === generation) result.value = found
  } catch (cause) {
    if (current !== generation) return
    error.value = toUiError(cause)
    if (error.value.status === 401 || error.value.status === 403) emit('auth-expired', error.value)
  } finally {
    if (current === generation) busy.value = false
  }
}
</script>

<template>
  <details class="panel operation-recovery" data-testid="operation-recovery">
    <summary>核对先前的操作（故障恢复查询）</summary>
    <p class="muted">保存后未收到明确结果时，可通过插件 ID 与原操作标识查询服务端回执；插件暂不可用时也能核对已留存记录。</p>
    <p v-if="!enabled" class="muted">请先切换为管理员模式后再查询。</p>
    <form class="recovery-fields" @submit.prevent="find">
      <label>插件 ID<input v-model="owner" autocomplete="off" spellcheck="false" placeholder="例如 preset.feature.token-quota" :disabled="!enabled" /></label>
      <label>原操作标识<input v-model="key" autocomplete="off" spellcheck="false" placeholder="完整粘贴原操作标识" :disabled="!enabled" /></label>
      <button type="submit" class="tonal" :disabled="!enabled || !valid || busy">{{ busy ? '查询中…' : '查询记录' }}</button>
    </form>
    <p v-if="formatHint" class="format-warn" role="status">{{ formatHint }}</p>
    <ErrorNotice :error="error" title="暂时无法核对，请保留原操作标识" />
    <div v-if="result" role="status" class="recovery-result">
      <template v-if="result.found">
        <strong v-if="result.receipt.state === 'committed'">已提交</strong>
        <strong v-else-if="result.receipt.state === 'rejected'">已拒绝</strong>
        <strong v-else>结果未知（Unknown）</strong>
        <p v-if="result.receipt.state === 'unknown'">记录尚不能确认操作结果。请保留原标识并核对实际状态，避免重复提交。</p>
        <p v-else>已找到原操作记录，业务详情暂不可用。需要详情时请恢复对应插件后再核对。</p>
        <p v-if="result.receipt.provenance === 'legacy'" class="muted">这是历史记录，未记录当时的运行版本。</p>
      </template>
      <p v-else>未找到记录。这不能证明操作未执行，请确认插件 ID 和原标识后再核对。</p>
    </div>
  </details>
</template>

<style scoped>
.operation-recovery { padding: 1rem 1.25rem; }
summary { cursor: pointer; font-weight: 600; font-size: var(--text-sm); color: var(--text-muted); }
.operation-recovery p { margin: 0.65rem 0; font-size: var(--text-sm); line-height: 1.6; }
.recovery-fields { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.5fr) auto; gap: 0.8rem; align-items: end; margin-top: 0.85rem; }
.recovery-fields label { display: grid; gap: 0.35rem; min-width: 0; font-size: var(--text-xs); font-weight: 600; color: var(--text-muted); }
.recovery-fields input { width: 100%; min-width: 0; font-family: var(--font-mono); font-size: var(--text-xs); }
.format-warn { color: var(--warn-text); font-weight: 500; font-size: var(--text-xs); }
.recovery-result { margin-top: 0.9rem; overflow-wrap: anywhere; }
@media (max-width: 680px) { .recovery-fields { grid-template-columns: 1fr; } }
</style>
