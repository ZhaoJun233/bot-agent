<script setup lang="ts">
import { nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { restoreFocus } from './focus-restore'

const props = withDefaults(defineProps<{
  title: string
  titleId: string
  testId: string
  active?: boolean
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
  wide?: boolean
}>(), { active: true, trigger: null, fallback: null, wide: false })
const emit = defineEmits<{ close: [] }>()
const root = ref<HTMLElement | null>(null)
const heading = ref<HTMLElement | null>(null)

function focusHeading(): void {
  if (props.active) void nextTick(() => heading.value?.focus())
}
function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') { event.preventDefault(); emit('close'); return }
  if (event.key !== 'Tab' || !root.value) return
  const elements = Array.from(root.value.querySelectorAll<HTMLElement>(
    'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]'
  )).filter((element) => !element.closest('[hidden], [inert]'))
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
    <div v-show="props.active" class="dialog-backdrop" :data-testid="props.testId" @click.self="emit('close')">
      <section ref="root" class="dialog management-dialog" :class="{ 'management-dialog-wide': props.wide }"
        role="dialog" aria-modal="true" :aria-labelledby="props.titleId" @keydown="onKeydown">
        <header class="management-dialog-head">
          <div><slot name="eyebrow" /><h2 :id="props.titleId" ref="heading" tabindex="-1">{{ props.title }}</h2></div>
          <button type="button" class="icon-close" aria-label="关闭" :data-testid="props.testId + '-close'" @click="emit('close')">×</button>
        </header>
        <slot />
      </section>
    </div>
  </Teleport>
</template>

<style scoped>
.management-dialog { width: min(720px, 100%); max-height: calc(100dvh - 32px); overflow-y: auto; }
.management-dialog-wide { width: min(1040px, 100%); }
.management-dialog-head { display: flex; align-items: flex-start; justify-content: space-between; gap: 16px; margin-bottom: 16px; }
.management-dialog-head h2 { margin: 0; overflow-wrap: anywhere; }
@media (max-width: 600px) {
  .management-dialog { max-height: calc(100dvh - 20px); padding: 18px; }
}
</style>
