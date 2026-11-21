<script setup lang="ts">
import { nextTick, onMounted, ref } from 'vue'
import ManagementDialog from './ManagementDialog.vue'

const props = withDefaults(defineProps<{
  title: string
  titleId: string
  testId: string
  active?: boolean
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
  confirmLabel?: string
  cancelLabel?: string
  confirmTestId?: string
  danger?: boolean
  busy?: boolean
  disabled?: boolean
}>(), {
  active: true,
  trigger: null,
  fallback: null,
  confirmLabel: '确认',
  cancelLabel: '取消',
  confirmTestId: undefined,
  danger: false,
  busy: false,
  disabled: false
})

const emit = defineEmits<{
  confirm: []
  cancel: []
}>()

const confirmBtn = ref<HTMLButtonElement | null>(null)
const cancelBtn = ref<HTMLButtonElement | null>(null)

onMounted(() => {
  // S4-M2 / S4-R1: 焦点优先落在可用确认按钮；若确认按钮禁用（在途或缺少代际），立即落到安全取消按钮，确保 Escape 与键盘可达。
  void nextTick(() => {
    if (confirmBtn.value && !confirmBtn.value.disabled) {
      confirmBtn.value.focus()
    } else if (cancelBtn.value && !cancelBtn.value.disabled) {
      cancelBtn.value.focus()
    }
  })
})
</script>

<template>
  <ManagementDialog
    :title="props.title"
    :title-id="props.titleId"
    :test-id="props.testId"
    :active="props.active"
    :trigger="props.trigger"
    :fallback="props.fallback"
    @close="emit('cancel')"
  >
    <div class="ui-confirm-body">
      <slot />
    </div>
    <template #footer>
      <button
        ref="confirmBtn"
        type="button"
        :class="props.danger ? 'danger' : 'primary'"
        :data-testid="props.confirmTestId ?? (props.testId + '-ok')"
        :disabled="props.busy || props.disabled"
        @click="emit('confirm')"
      >
        {{ props.confirmLabel }}
      </button>
      <button
        ref="cancelBtn"
        type="button"
        :data-testid="props.testId + '-cancel'"
        @click="emit('cancel')"
      >
        {{ props.cancelLabel }}
      </button>
    </template>
  </ManagementDialog>
</template>

<style scoped>
.ui-confirm-body {
  display: grid;
  gap: 12px;
  font-size: var(--text-base);
  color: var(--text-muted);
  line-height: 1.65;
}
</style>
