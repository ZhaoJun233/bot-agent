<script setup lang="ts">
/**
 * 全站统一开关原语（role="switch"）。
 * 使用原生 checkbox 保持键盘 Space 切换、表单无障碍与 label 联动，
 * 移动端外层容器满足 44px 最小触控高度。
 */
const props = withDefaults(defineProps<{
  modelValue: boolean
  disabled?: boolean
  label?: string
  ariaLabel?: string
  testId?: string
  id?: string
}>(), { disabled: false, label: undefined, ariaLabel: undefined, testId: undefined, id: undefined })

const emit = defineEmits<{
  'update:modelValue': [value: boolean]
  change: [value: boolean, event: Event]
}>()

function onChange(event: Event): void {
  const input = event.target as HTMLInputElement
  const next = input.checked
  emit('update:modelValue', next)
  emit('change', next, event)
}
</script>

<template>
  <label class="ui-switch" :class="{ 'is-disabled': props.disabled }">
    <input
      :id="props.id"
      type="checkbox"
      role="switch"
      class="ui-switch-input"
      :checked="props.modelValue"
      :aria-checked="props.modelValue"
      :aria-label="props.ariaLabel || props.label"
      :disabled="props.disabled"
      :data-testid="props.testId"
      @change="onChange"
    />
    <span v-if="props.label || $slots.default" class="ui-switch-label">
      <slot>{{ props.label }}</slot>
    </span>
  </label>
</template>

<style scoped>
.ui-switch {
  display: inline-flex;
  align-items: center;
  gap: 10px;
  min-height: 32px;
  cursor: pointer;
  user-select: none;
}
.ui-switch.is-disabled {
  cursor: not-allowed;
}
.ui-switch-input {
  appearance: none;
  -webkit-appearance: none;
  width: 44px;
  height: 24px;
  min-width: 44px;
  min-height: 24px;
  padding: 0;
  margin: 0;
  flex: none;
  border: 1px solid var(--border-input);
  border-radius: 999px;
  background: var(--border-strong);
  cursor: pointer;
  position: relative;
  transition: background-color var(--duration-fast) var(--ease), border-color var(--duration-fast) var(--ease);
}
.ui-switch-input::before {
  content: '';
  display: block;
  width: 18px;
  height: 18px;
  margin: 2px;
  border-radius: 50%;
  background: #ffffff;
  box-shadow: 0 1px 2px rgba(15, 23, 42, 0.25);
  transition: transform var(--duration-fast) var(--ease);
}
.ui-switch-input:checked {
  background: var(--primary);
  border-color: var(--primary);
}
.ui-switch-input:checked::before {
  transform: translateX(20px);
}
.ui-switch-input:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}
.ui-switch-input:focus-visible {
  outline: 3px solid var(--accent-deep);
  outline-offset: 2px;
}
.ui-switch-label {
  font-size: var(--text-sm);
  font-weight: 500;
  color: var(--text);
}
@media (max-width: 640px) {
  .ui-switch {
    min-height: 44px;
  }
}
</style>
