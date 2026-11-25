<script setup lang="ts">
import { computed } from 'vue'
import { stateName } from '../friendly'

export type BadgeTone = 'ok' | 'warn' | 'bad' | 'neutral' | 'accent'

const props = withDefaults(defineProps<{
  state?: string
  available?: boolean
  /** 显示中文状态名，原始值放进 title 便于排障。 */
  friendly?: boolean
  /** 显式语义色调（传入后优先于 state 推断）。 */
  tone?: BadgeTone
  /** 显式显示文本（传入后优先于 state）。 */
  label?: string
  /** 是否显示前置状态小圆点。 */
  dot?: boolean
}>(), { state: '', available: undefined, friendly: false, tone: undefined, label: undefined, dot: false })

const rawState = computed(() => typeof props.state === 'string' && props.state.length > 0 ? props.state : 'unknown')
const normalized = computed(() => rawState.value.toLowerCase().replace(/[\s_-]/g, ''))

const resolvedTone = computed<BadgeTone>(() => {
  if (props.tone) return props.tone
  const state = normalized.value
  if (state.includes('fail') || state.includes('error') || state.includes('unhealthy')) return 'bad'
  if (state.includes('disabled') || state.includes('stopped') || state.includes('paused') || state.includes('inactive')) {
    return 'neutral'
  }
  if (state.includes('degrad') || state.includes('pending') || state.includes('restart')
    || state.includes('unknown') || state.includes('notstarted')) {
    return 'warn'
  }
  if (props.available === false) return 'bad'
  if (props.available === true) return 'ok'
  if (state.includes('ready') || state.includes('healthy') || state.includes('active') || state.includes('applied') || state.includes('running')) {
    return 'ok'
  }
  return 'neutral'
})

const resolvedLabel = computed(() => {
  if (props.label !== undefined) return props.label
  const state = normalized.value
  const notAffirmative = state.includes('unknown') || state.includes('notstarted')
    || state.includes('degrad') || state.includes('pending') || state.includes('restart')
    || state.includes('fail') || state.includes('error') || state.includes('unhealthy')
    || state.includes('disabled') || state.includes('stopped') || state.includes('paused')
    || state.includes('inactive')
  const name = props.friendly ? stateName(rawState.value) : rawState.value
  if (props.available === true && !notAffirmative) return props.friendly ? name : `${name} · 可用`
  if (props.available === false) return props.friendly && state.includes('disabled') ? name : `${name} · 不可用`
  return name
})
</script>

<template>
  <span class="status-badge" :data-tone="resolvedTone" :title="props.friendly && !props.label ? rawState : undefined">
    <span v-if="props.dot" class="ov-pill-dot" aria-hidden="true" />
    {{ resolvedLabel }}
  </span>
</template>
