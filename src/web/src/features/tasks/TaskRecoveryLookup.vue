<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import type { ZbaClient, ZbaTaskFindResult, ZbaTaskSummary } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { taskFindCodeLabels } from './task-find-labels'

const props = defineProps<{ client: ZbaClient; enabled: boolean }>()
const emit = defineEmits<{
  found: [key: string, task: ZbaTaskSummary]
  'open-task': [task: ZbaTaskSummary]
  'auth-expired': [error: UiError]
}>()
const key = ref('')
const busy = ref(false)
const result = ref<ZbaTaskFindResult | null>(null)
const error = ref<UiError | null>(null)
// Match ZbaRules.ValidateIdempotencyKey; never shorten or replace the original key.
const validKey = computed(() => /^[A-Za-z0-9._~-]{8,128}$/.test(key.value))
let generation = 0

function invalidate(): void {
  generation += 1
  busy.value = false
  result.value = null
  error.value = null
}

watch(key, invalidate, { flush: 'sync' })
watch(() => [props.client, props.enabled], () => {
  invalidate()
  key.value = ''
}, { flush: 'sync' })
onBeforeUnmount(invalidate)

async function find(): Promise<void> {
  if (!props.enabled || busy.value || !validKey.value) return
  const owner = ++generation
  const client = props.client
  const originalKey = key.value
  busy.value = true
  result.value = null
  error.value = null
  try {
    const found = await client.findTask(originalKey)
    if (owner !== generation) return
    result.value = found
    if (found.code === 'found' && found.task) emit('found', originalKey, found.task)
  } catch (cause) {
    if (owner !== generation) return
    const failure = toUiError(cause)
    error.value = failure
    if (failure.status === 401 || failure.status === 403) emit('auth-expired', failure)
  } finally {
    if (owner === generation) busy.value = false
  }
}

function openTask(): void {
  if (props.enabled && result.value?.code === 'found' && result.value.task) {
    emit('open-task', result.value.task)
  }
}
</script>

<template>
  <details class="inline-notice task-recovery" data-testid="task-recovery">
    <summary>核对先前的提交</summary>
    <p id="task-recovery-help">刷新前请保留提交记录中的原幂等键。重新连接原服务、使用原身份后，在这里核对；查询不会再次执行任务。</p>
    <form class="recovery-form" @submit.prevent="find">
      <label for="task-recovery-key">原幂等键</label>
      <input
        id="task-recovery-key"
        v-model="key"
        data-testid="task-recovery-key"
        type="text"
        autocomplete="off"
        autocapitalize="off"
        :spellcheck="false"
        :disabled="!props.enabled"
        aria-describedby="task-recovery-help task-recovery-format"
      >
      <p id="task-recovery-format">8–128 个字符，仅限英文字母、数字和 - _ . ~，请完整粘贴。</p>
      <p v-if="!props.enabled">请先进入管理员模式，再核对原键。</p>
      <button type="submit" class="tonal" data-testid="task-recovery-find" :disabled="!props.enabled || !validKey || busy">
        {{ busy ? '正在核对…' : '核对原键' }}
      </button>
    </form>
    <div aria-live="polite" aria-atomic="true">
      <p v-if="result" data-testid="task-recovery-code">{{ taskFindCodeLabels[result.code] }}</p>
      <p v-if="result && result.code !== 'found'">请继续核对已有任务记录和实际操作结果，不能据此判断可以安全重做。</p>
    </div>
    <button v-if="result?.code === 'found'" type="button" class="tonal" data-testid="task-recovery-open" @click="openTask">查看任务结果</button>
    <ErrorNotice :error="error" title="核对未完成，原任务结果仍待确认" />
  </details>
</template>

<style scoped>
.task-recovery { min-width: 0; }
.recovery-form { display: grid; gap: 0.5rem; }
.recovery-form input { width: 100%; min-width: 0; box-sizing: border-box; }
.recovery-form button { justify-self: start; }
.task-recovery p { overflow-wrap: anywhere; }
</style>
