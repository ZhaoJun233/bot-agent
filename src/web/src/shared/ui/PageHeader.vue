<script setup lang="ts">
import { ref } from 'vue'

defineProps<{
  title: string
  titleId: string
  description?: string
  eyebrow?: string
}>()

const heading = ref<HTMLElement | null>(null)
defineExpose({ heading })
</script>

<template>
  <header class="page-heading">
    <div class="page-heading-copy">
      <p v-if="eyebrow" class="eyebrow">{{ eyebrow }}</p>
      <h1 :id="titleId" ref="heading" tabindex="-1">{{ title }}</h1>
      <p v-if="description" class="subtitle">{{ description }}</p>
      <div v-if="$slots.metadata" class="page-heading-meta"><slot name="metadata" /></div>
    </div>
    <div v-if="$slots.default || $slots.actions" class="heading-actions">
      <slot name="actions" />
      <slot />
    </div>
  </header>
</template>
