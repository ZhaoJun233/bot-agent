<script setup lang="ts">
/**
 * 统一的线性图标。纯 currentColor、无外部资源，尺寸通过 size 控制。
 * 纯装饰：相邻文本提供可读名称，因此固定 aria-hidden。
 */
withDefaults(defineProps<{
  name: 'overview' | 'capabilities' | 'config' | 'tasks' | 'refresh' | 'lock' | 'shield' | 'spark' | 'link' | 'bolt' | 'alert' | 'sun' | 'moon' | 'help' | 'arrow' | 'check'
  size?: number
}>(), { size: 18 })
</script>

<template>
  <svg
    class="app-icon"
    :width="size"
    :height="size"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    stroke-width="1.8"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <template v-if="name === 'overview'">
      <rect x="3" y="3" width="7" height="7" rx="1.6" />
      <rect x="14" y="3" width="7" height="7" rx="1.6" />
      <rect x="3" y="14" width="7" height="7" rx="1.6" />
      <rect x="14" y="14" width="7" height="7" rx="1.6" />
    </template>
    <template v-else-if="name === 'capabilities'">
      <path d="m12 2.8 8.5 4.7-8.5 4.7-8.5-4.7 8.5-4.7Z" />
      <path d="m3.5 12 8.5 4.7 8.5-4.7" />
      <path d="m3.5 16.6 8.5 4.7 8.5-4.7" />
    </template>
    <template v-else-if="name === 'config'">
      <path d="M4 6h9" /><path d="M17 6h3" />
      <circle cx="15" cy="6" r="2.2" />
      <path d="M4 12h3" /><path d="M11 12h9" />
      <circle cx="9" cy="12" r="2.2" />
      <path d="M4 18h11" /><path d="M19 18h1" />
      <circle cx="17" cy="18" r="2.2" />
    </template>
    <template v-else-if="name === 'tasks'">
      <rect x="3" y="4.5" width="18" height="15" rx="2.2" />
      <path d="m7 9 2.6 2.6L7 14.2" />
      <path d="M12.5 15h4.5" />
    </template>
    <template v-else-if="name === 'refresh'">
      <path d="M20.5 12a8.5 8.5 0 1 1-2.5-6" />
      <path d="M20.5 3.5V9H15" />
    </template>
    <template v-else-if="name === 'lock'">
      <rect x="4.5" y="10" width="15" height="10.5" rx="2.2" />
      <path d="M8 10V7.2a4 4 0 0 1 8 0V10" />
      <path d="M12 14v3" />
    </template>
    <template v-else-if="name === 'shield'">
      <path d="M12 3 5.2 5.9v5.7c0 4.3 2.9 7.2 6.8 8.7 3.9-1.5 6.8-4.4 6.8-8.7V5.9L12 3Z" />
      <path d="m9 12 2 2 4-4" />
    </template>
    <template v-else-if="name === 'spark'">
      <path d="M12 3v3.4" /><path d="M12 17.6V21" />
      <path d="M4.6 4.6 7 7" /><path d="m17 17 2.4 2.4" />
      <path d="M3 12h3.4" /><path d="M17.6 12H21" />
      <path d="m4.6 19.4 2.4-2.4" /><path d="m17 7 2.4-2.4" />
    </template>
    <template v-else-if="name === 'alert'">
      <path d="M12 3.4 2.9 19.6h18.2L12 3.4Z" />
      <path d="M12 9.4v4.4" />
      <path d="M12 16.9h.01" />
    </template>
    <template v-else-if="name === 'link'">
      <path d="M10.5 13.5a4.5 4.5 0 0 0 6.4 0l2-2a4.5 4.5 0 0 0-6.4-6.4l-1.1 1.1" />
      <path d="M13.5 10.5a4.5 4.5 0 0 0-6.4 0l-2 2a4.5 4.5 0 0 0 6.4 6.4l1.1-1.1" />
    </template>
    <template v-else-if="name === 'sun'">
      <circle cx="12" cy="12" r="4" /><path d="M12 2v2m0 16v2M2 12h2m16 0h2M5 5l1.4 1.4m11.2 11.2L19 19M5 19l1.4-1.4M17.6 6.4 19 5" />
    </template>
    <template v-else-if="name === 'moon'"><path d="M20.5 14A8.5 8.5 0 0 1 10 3.5 8.5 8.5 0 1 0 20.5 14Z" /></template>
    <template v-else-if="name === 'help'"><circle cx="12" cy="12" r="9" /><path d="M9.5 9a2.5 2.5 0 1 1 4.4 1.6C12.8 11.4 12 12 12 13.5M12 17h.01" /></template>
    <template v-else-if="name === 'arrow'"><path d="M5 12h14m-5-5 5 5-5 5" /></template>
    <template v-else-if="name === 'check'"><path d="m5 12 4.5 4.5L19 7" /></template>
    <template v-else>
      <path d="M13 2.5 4.8 13.4h6.1L9.9 21.5l8.3-11.1h-6.1l1-7.9Z" />
    </template>
  </svg>
</template>
