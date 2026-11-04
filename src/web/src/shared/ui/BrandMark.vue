<script setup lang="ts">
/** Minimal conversation mark; the surrounding UI owns the badge background and color. */
withDefaults(defineProps<{
  size?: number
}>(), { size: 32 })
</script>

<template>
  <svg
    class="brand-mark"
    :width="size"
    :height="size"
    viewBox="0 0 32 32"
    aria-hidden="true"
    focusable="false"
  >
    <path d="M10 6.5h12a5.5 5.5 0 0 1 5.5 5.5v7a5.5 5.5 0 0 1-5.5 5.5h-9l-5.5 4v-5.1a5.5 5.5 0 0 1-3-4.9v-6.5A5.5 5.5 0 0 1 10 6.5Z" fill="none" stroke="currentColor" stroke-width="2" stroke-linejoin="round" />
    <circle cx="10.5" cy="15.5" r="1.5" fill="currentColor" />
    <circle cx="16" cy="15.5" r="1.5" fill="currentColor" />
    <circle cx="21.5" cy="15.5" r="1.5" fill="currentColor" />
  </svg>
</template>
