<script setup lang="ts">
import { computed } from 'vue'

/**
 * 标识符（plugin:preset.feature.websearch 这类）只在分隔符后允许换行，
 * 不在单词中间断开。<wbr> 不进入复制文本，复制出来仍是原值。
 */
const props = defineProps<{ value: string }>()

const parts = computed(() => (props.value ?? '').split(/(?<=[.:/_-])/))
</script>

<template>
  <span class="breakable-id"><template v-for="(part, index) in parts" :key="index"><wbr v-if="index > 0" />{{ part }}</template></span>
</template>
