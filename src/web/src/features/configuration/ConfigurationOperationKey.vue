<script setup lang="ts">
import { onBeforeUnmount, ref, useId } from 'vue'
import AppIcon from '../../shared/ui/AppIcon.vue'

const props = defineProps<{ operationKey: string }>()
const id = useId()
const copied = ref(false)
let resetTimer: ReturnType<typeof setTimeout> | null = null

function selectKey(event: Event): void {
  if (event.target instanceof HTMLInputElement) event.target.select()
}

async function copyKey(): Promise<void> {
  if (!props.operationKey) return
  try {
    await navigator.clipboard?.writeText(props.operationKey)
    copied.value = true
    if (resetTimer) clearTimeout(resetTimer)
    resetTimer = setTimeout(() => { copied.value = false }, 1800)
  } catch {
    copied.value = false
  }
}

onBeforeUnmount(() => {
  if (resetTimer) clearTimeout(resetTimer)
})
</script>

<template>
  <div class="inline-notice operation-key" data-testid="configuration-operation-key-notice">
    <label :for="id + '-key'">本次配置操作键</label>
    <div class="operation-key-row">
      <input
        :id="id + '-key'"
        :value="operationKey"
        data-testid="configuration-operation-key"
        type="text"
        readonly
        autocomplete="off"
        autocapitalize="off"
        :spellcheck="false"
        :aria-describedby="id + '-help'"
        @focus="selectKey"
        @click="selectKey"
      >
      <button
        type="button"
        class="tonal"
        data-testid="configuration-operation-key-copy"
        @click="copyKey"
      >
        <AppIcon :name="copied ? 'check' : 'copy'" :size="14" />
        {{ copied ? '已复制' : '复制' }}
      </button>
    </div>
    <p :id="id + '-help'">可一键复制留存；刷新或重连后在“核对先前的配置保存”中查询原回执，无需重复保存。</p>
  </div>
</template>

<style scoped>
.operation-key { display: grid; gap: 0.5rem; min-width: 0; }
.operation-key label { font-size: var(--text-xs); font-weight: 600; color: var(--text); }
.operation-key-row { display: flex; gap: 8px; align-items: center; }
.operation-key input { flex: 1; width: 100%; min-width: 0; box-sizing: border-box; font-family: var(--font-mono); font-size: var(--text-xs); }
.operation-key p { margin: 0; font-size: var(--text-xs); overflow-wrap: anywhere; }
</style>
