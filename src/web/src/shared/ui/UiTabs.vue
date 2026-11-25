<script setup lang="ts">
import AppIcon, { type AppIconName } from './AppIcon.vue'

export interface UiTabOption<T extends string | number = string> {
  value: T
  label: string
  count?: number
  icon?: AppIconName
  testId?: string
  disabled?: boolean
}

const props = withDefaults(defineProps<{
  modelValue: string | number
  options: readonly UiTabOption<string | number>[]
  label: string
  variant?: 'segmented' | 'pills'
}>(), { variant: 'segmented' })

const emit = defineEmits<{
  'update:modelValue': [value: string | number]
}>()

function onKeydown(event: KeyboardEvent, index: number): void {
  if (event.key !== 'ArrowRight' && event.key !== 'ArrowLeft') return
  const enabled = props.options
    .map((option, idx) => ({ option, idx }))
    .filter(item => !item.option.disabled)
  if (enabled.length < 2) return
  const pos = enabled.findIndex(item => item.idx === index)
  if (pos < 0) return
  const delta = event.key === 'ArrowRight' ? 1 : -1
  const next = enabled[(pos + delta + enabled.length) % enabled.length]
  if (!next) return
  event.preventDefault()
  emit('update:modelValue', next.option.value)
}
</script>

<template>
  <div
    class="ui-tabs"
    :class="`ui-tabs--${props.variant}`"
    role="tablist"
    :aria-label="props.label"
  >
    <button
      v-for="(option, index) in props.options"
      :key="String(option.value)"
      type="button"
      role="tab"
      class="ui-tab"
      :class="{ active: props.modelValue === option.value }"
      :aria-selected="props.modelValue === option.value"
      :disabled="option.disabled"
      :data-testid="option.testId"
      @click="emit('update:modelValue', option.value)"
      @keydown="onKeydown($event, index)"
    >
      <AppIcon v-if="option.icon" :name="option.icon" :size="15" />
      <span>{{ option.label }}</span>
      <span v-if="option.count !== undefined" class="ui-tab-count">{{ option.count }}</span>
    </button>
  </div>
</template>

<style scoped>
.ui-tabs {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 4px;
  max-width: 100%;
}
.ui-tabs--segmented {
  padding: 4px;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface-muted);
}
.ui-tab {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  min-height: 32px;
  padding: 6px 12px;
  border: 1px solid transparent;
  border-radius: calc(var(--radius) - 2px);
  background: transparent;
  color: var(--text-muted);
  font-size: var(--text-sm);
  font-weight: 500;
}
.ui-tab:hover:not(:disabled) {
  color: var(--text);
  background: var(--surface);
  border-color: transparent;
}
.ui-tabs--segmented .ui-tab.active {
  background: var(--surface);
  color: var(--accent-deep);
  border-color: var(--border-subtle);
  box-shadow: var(--shadow-1);
  font-weight: 600;
}
.ui-tabs--pills .ui-tab {
  border-radius: 999px;
  border-color: var(--border-subtle);
  background: var(--surface);
}
.ui-tabs--pills .ui-tab.active {
  background: var(--accent-soft);
  border-color: var(--accent-strong);
  color: var(--accent-deep);
  font-weight: 600;
}
.ui-tab-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 20px;
  padding: 0 6px;
  border-radius: 999px;
  background: var(--neutral-bg);
  color: var(--text-muted);
  font: 600 var(--text-xs)/1.5 var(--font-mono);
}
.ui-tab.active .ui-tab-count {
  background: var(--accent-soft);
  color: var(--accent-deep);
}
@media (max-width: 640px) {
  .ui-tab {
    min-height: 44px;
  }
}
</style>
