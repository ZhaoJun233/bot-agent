<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { restoreFocus } from './focus-restore'
import { dialogFocusableElements, useModalIsolation } from './modal-isolation'

const props = withDefaults(defineProps<{
  title: string
  titleId: string
  testId: string
  active?: boolean
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
  wide?: boolean
  drawer?: boolean
}>(), { active: true, trigger: null, fallback: null, wide: false })
const emit = defineEmits<{ close: [] }>()
const root = ref<HTMLElement | null>(null)
const heading = ref<HTMLElement | null>(null)
useModalIsolation(root, () => props.active)

function focusHeading(): void {
  if (props.active) void nextTick(() => { if (props.active) heading.value?.focus() })
}
defineExpose({ focus: focusHeading })
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') { event.preventDefault(); event.stopPropagation(); emit('close'); return }
  if (event.key !== 'Tab' || !root.value) return
  const elements = dialogFocusableElements(root.value)
  const first = elements[0]
  const last = elements[elements.length - 1]
  if (!first || !last) { event.preventDefault(); heading.value?.focus(); return }
  const active = document.activeElement
  if (event.shiftKey && (active === first || active === heading.value || !root.value.contains(active))) {
    event.preventDefault(); last.focus()
  } else if (!event.shiftKey && (active === last || !root.value.contains(active))) {
    event.preventDefault(); first.focus()
  }
}
watch(() => props.active, focusHeading)
onMounted(focusHeading)
onBeforeUnmount(() => {
  if (props.active) void nextTick(() => restoreFocus(props.trigger, props.fallback))
})
</script>

<template>
  <Teleport to="body">
    <div v-show="props.active" class="dialog-backdrop" :class="{ 'drawer-backdrop': props.drawer }" :data-testid="props.testId" @click.self="emit('close')">
      <section ref="root" class="dialog management-dialog" :class="{ 'management-dialog-wide': props.wide, 'management-dialog-drawer': props.drawer }"
        role="dialog" aria-modal="true" :aria-labelledby="props.titleId" @keydown="onKeydown">
        <header class="management-dialog-head">
          <div class="management-dialog-title">
            <slot name="eyebrow" />
            <h2 :id="props.titleId" ref="heading" tabindex="-1">{{ props.title }}</h2>
          </div>
          <button type="button" class="icon-close" aria-label="关闭" :data-testid="props.testId + '-close'" @click="emit('close')">×</button>
        </header>
        <div class="management-dialog-body">
          <slot />
        </div>
        <footer v-if="$slots.footer" class="management-dialog-footer">
          <slot name="footer" />
        </footer>
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.management-dialog {
  display: flex;
  flex-direction: column;
  width: min(740px, 100%);
  max-height: calc(100dvh - 32px);
  overflow: hidden;
  padding: 0;
}
.management-dialog-wide {
  width: min(1060px, 100%);
}
.drawer-backdrop {
  justify-content: flex-end;
  padding: 0;
}
.management-dialog-drawer {
  width: min(680px, 100vw);
  height: 100dvh;
  max-height: 100dvh;
  border-radius: 18px 0 0 18px;
  border-right: 0;
  animation: drawer-in var(--duration) var(--ease);
}
.management-dialog-drawer.management-dialog-wide {
  width: min(860px, 100vw);
}
.management-dialog-head {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 16px;
  padding: 20px 24px 14px;
  border-bottom: 1px solid var(--border-subtle);
  flex: none;
}
.management-dialog-title {
  min-width: 0;
  flex: 1;
}
.management-dialog-head h2 {
  margin: 0;
  overflow-wrap: anywhere;
}
.management-dialog-body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  overscroll-behavior: contain;
  padding: 18px 24px 22px;
}
.management-dialog-footer {
  display: flex;
  flex-wrap: wrap;
  justify-content: flex-end;
  gap: 8px;
  padding: 14px 24px;
  border-top: 1px solid var(--border-subtle);
  background: var(--surface);
  flex: none;
}
@media (max-width: 640px) {
  .management-dialog {
    max-height: calc(100dvh - 20px);
  }
  .management-dialog-drawer {
    width: 100vw;
    max-height: 100dvh;
    border-radius: 0;
  }
  .management-dialog-head {
    padding: 16px 18px 12px;
  }
  .management-dialog-body {
    padding: 14px 18px 18px;
  }
  .management-dialog-footer {
    padding: 12px 18px;
  }
}
</style>
