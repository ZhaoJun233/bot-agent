<script setup lang="ts">
/**
 * 产品图标：负空间机器人头（双眼、天线、微笑），纯 currentColor。
 * 只画字形，不画外框；外框由使用处的 .brand-badge / favicon 圆角方片提供。
 * 16px 先读到“脸”，32px 读到双眼与微笑；纯装饰，旁边始终有可读 wordmark。
 */
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
    <path
      fill-rule="evenodd"
      d="M12.5 13.3h7a3.6 3.6 0 0 1 3.6 3.6v2.7a3.6 3.6 0 0 1-3.6 3.6h-7a3.6 3.6 0 0 1-3.6-3.6v-2.7a3.6 3.6 0 0 1 3.6-3.6zM13.2 16.6a1.6 1.6 0 1 0 0 3.2 1.6 1.6 0 0 0 0-3.2zM18.8 16.6a1.6 1.6 0 1 0 0 3.2 1.6 1.6 0 0 0 0-3.2z"
      fill="currentColor"
    />
    <rect x="14.9" y="9" width="2.2" height="4.9" rx="1.1" fill="currentColor" />
    <circle cx="16" cy="7.8" r="2.1" fill="currentColor" />
    <path
      d="M10.4 24.6c1.5 1.9 3.4 2.9 5.6 2.9s4.1-1 5.6-2.9"
      fill="none"
      stroke="currentColor"
      stroke-width="1.35"
      stroke-linecap="round"
    />
  </svg>
</template>
