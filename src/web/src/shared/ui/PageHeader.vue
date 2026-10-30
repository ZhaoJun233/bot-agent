<script setup lang="ts">
defineProps<{ title: string; titleId: string; description?: string; eyebrow?: string }>()
</script>

<template>
  <header class="page-heading">
    <div class="page-heading-copy">
      <p v-if="eyebrow" class="eyebrow">{{ eyebrow }}</p>
      <h1 :id="titleId">{{ title }}</h1>
      <p v-if="description" class="subtitle">{{ description }}</p>
      <div v-if="$slots.metadata" class="page-heading-meta"><slot name="metadata" /></div>
    </div>
    <div v-if="$slots.default" class="heading-actions"><slot /></div>
  </header>
</template>
