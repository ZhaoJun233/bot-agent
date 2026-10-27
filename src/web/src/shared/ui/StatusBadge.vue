<script setup lang="ts">
import { computed } from 'vue'
import { stateName } from '../friendly'

const props = withDefaults(defineProps<{
  state: string
  available?: boolean
  /** 显示中文状态名，原始值放进 title 便于排障。 */
  friendly?: boolean
}>(), { available: undefined, friendly: false })

const rawState = computed(() => typeof props.state === 'string' && props.state.length > 0 ? props.state : 'unknown')

// 后端状态名写法不一（not_started / notstarted / Not-Started），先去分隔符再匹配。
const normalized = computed(() => rawState.value.toLowerCase().replace(/[\s_-]/g, ''))

const tone = computed(() => {
  const state = normalized.value
  if (state.includes('fail') || state.includes('error') || state.includes('unhealthy')) return 'bad'
  // 主动停用不是故障：中性色；故障仍走上面的 bad。
  if (state.includes('disabled') || state.includes('stopped') || state.includes('paused') || state.includes('inactive')) {
    return 'neutral'
  }
  if (state.includes('degrad') || state.includes('pending') || state.includes('restart')
    || state.includes('unknown') || state.includes('notstarted')) {
    return 'warn'
  }
  if (props.available === false) return 'bad'
  if (props.available === true) return 'ok'
  if (state.includes('ready') || state.includes('healthy') || state.includes('active') || state.includes('applied')) {
    return 'ok'
  }
  return 'neutral'
})

const label = computed(() => {
  const state = normalized.value
  const notAffirmative = state.includes('unknown') || state.includes('notstarted')
    || state.includes('degrad') || state.includes('pending') || state.includes('restart')
    || state.includes('fail') || state.includes('error') || state.includes('unhealthy')
    || state.includes('disabled') || state.includes('stopped') || state.includes('paused')
    || state.includes('inactive')
  // available=true 只表示“已登记/可调用”，不能覆盖未知、停用或失败状态。
  const name = props.friendly ? stateName(rawState.value) : rawState.value
  if (props.available === true && !notAffirmative) return props.friendly ? name : `${name} · 可用`
  if (props.available === false) return props.friendly && state.includes('disabled') ? name : `${name} · 不可用`
  return name
})
</script>

<template>
  <span class="status-badge" :data-tone="tone" :title="props.friendly ? rawState : undefined">{{ label }}</span>
</template>
