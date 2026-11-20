<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ZbaClient } from '../../api/zba'
import type { OperationFindResult } from '../../api/operation-recovery'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'

const props = defineProps<{ client: ZbaClient; enabled: boolean }>()
const emit = defineEmits<{ 'auth-expired': [error: UiError] }>()
const owner = ref(''), key = ref('')
const busy = ref(false), result = ref<OperationFindResult | null>(null), error = ref<UiError | null>(null)
const valid = computed(() => /^[a-z][a-z0-9.-]{0,79}$/.test(owner.value) && !owner.value.includes('..')
  && !owner.value.endsWith('.') && key.value.trim().length > 0 && key.value.length >= 8 && key.value.length <= 256
  && [...key.value].every(character => character >= ' ' && character <= '~' && character !== '/' && character.charCodeAt(0) !== 92))
let generation = 0
function invalidate(): void { generation += 1; busy.value = false; result.value = null; error.value = null }
watch([owner, key], invalidate, { flush: 'sync' })
watch(() => [props.client, props.enabled], () => { invalidate(); owner.value = ''; key.value = '' }, { flush: 'sync' })
onBeforeUnmount(invalidate)
async function find(): Promise<void> {
  if (!props.enabled || !valid.value || busy.value) return
  const current = ++generation, client = props.client
  busy.value = true; result.value = null; error.value = null
  try {
    const found = await client.findOperation(owner.value, key.value)
    if (current === generation) result.value = found
  } catch (cause) {
    if (current !== generation) return
    error.value = toUiError(cause)
    if (error.value.status === 401 || error.value.status === 403) emit('auth-expired', error.value)
  } finally { if (current === generation) busy.value = false }
}
</script>

<template>
  <details class="panel operation-recovery" data-testid="operation-recovery">
    <summary>核对先前的操作</summary>
    <p class="muted">保存后没收到结果时，用原操作标识查询。插件暂不可用时也能核对已留存的记录。</p>
    <p v-if="!enabled" class="muted">请先进入管理员模式。</p>
    <form class="recovery-fields" @submit.prevent="find">
      <label>插件 ID<input v-model="owner" autocomplete="off" spellcheck="false" placeholder="填写原操作所属插件" :disabled="!enabled" /></label>
      <label>原操作标识<input v-model="key" autocomplete="off" spellcheck="false" placeholder="完整粘贴原标识" :disabled="!enabled" /></label>
      <button type="submit" class="tonal" :disabled="!enabled || !valid || busy">{{ busy ? '查询中…' : '查询记录' }}</button>
    </form>
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
summary { cursor: pointer; font-weight: 650; }
.operation-recovery p { margin: .65rem 0; line-height: 1.6; }
.recovery-fields { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1.5fr) auto; gap: .8rem; align-items: end; margin-top: 1rem; }
.recovery-fields label { display: grid; gap: .4rem; min-width: 0; }
.recovery-fields input { width: 100%; min-width: 0; }
.recovery-result { margin-top: 1rem; overflow-wrap: anywhere; }
@media (max-width: 680px) { .recovery-fields { grid-template-columns: 1fr; } }
</style>
