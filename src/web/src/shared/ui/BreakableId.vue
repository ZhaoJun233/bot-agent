<script setup lang="ts">
import { computed, onBeforeUnmount, ref } from 'vue'
import AppIcon from './AppIcon.vue'

/**
 * 标识符（plugin:preset.feature.websearch 这类）只在分隔符后允许换行，
 * 不在单词中间断开。<wbr> 不进入复制文本，复制出来仍是原值。
 */
const props = withDefaults(defineProps<{
  value: string
  copyable?: boolean
  copyLabel?: string
}>(), { copyable: false, copyLabel: '复制标识' })

const parts = computed(() => (props.value ?? '').split(/(?<=[.:/_-])/))
const copied = ref(false)
let resetTimer: ReturnType<typeof setTimeout> | null = null

async function copy(): Promise<void> {
  if (!props.value) return
  try {
    await navigator.clipboard?.writeText(props.value)
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
  <span class="breakable-id-wrap" :class="{ 'is-copyable': props.copyable }">
    <span class="breakable-id"><template v-for="(part, index) in parts" :key="index"><wbr v-if="index > 0" />{{ part }}</template></span>
    <button
      v-if="props.copyable && props.value"
      type="button"
      class="ghost id-copy"
      :aria-label="copied ? '已复制' : props.copyLabel"
      :title="copied ? '已复制' : props.copyLabel"
      @click="copy"
    >
      <AppIcon :name="copied ? 'check' : 'copy'" :size="14" />
    </button>
  </span>
</template>

<style scoped>
.breakable-id-wrap {
  display: inline;
}
.breakable-id-wrap.is-copyable {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: 100%;
  vertical-align: middle;
}
</style>
