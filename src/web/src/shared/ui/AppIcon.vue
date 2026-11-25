<script setup lang="ts">
/**
 * 统一的线性图标（1.75px 笔触）。纯 currentColor、零外部资源，尺寸通过 size 控制。
 * 纯装饰：相邻文本提供可读名称，固定 aria-hidden。
 */
export type AppIconName =
  | 'overview'
  | 'capabilities'
  | 'provider'
  | 'platform'
  | 'plugin'
  | 'config'
  | 'tasks'
  | 'traces'
  | 'logs'
  | 'refresh'
  | 'lock'
  | 'shield'
  | 'spark'
  | 'link'
  | 'bolt'
  | 'alert'
  | 'sun'
  | 'moon'
  | 'help'
  | 'arrow'
  | 'check'
  | 'check-circle'
  | 'search'
  | 'close'
  | 'chevron-down'
  | 'chevron-right'
  | 'copy'
  | 'settings'
  | 'plus'
  | 'trash'
  | 'filter'
  | 'wrap'
  | 'sidebar'
  | 'external'
  | 'history'
  | 'play'
  | 'stop'
  | 'sliders'
  | 'info'
  | 'qq'
  | 'feishu'
  | 'terminal'
  | 'music'
  | 'voice'
  | 'sticker'
  | 'poke'
  | 'profile'
  | 'vibe'
  | 'quota'
  | 'globe'
  | 'python'
  | 'cpu'
  | 'store'
  | 'upload'

withDefaults(defineProps<{
  name: AppIconName
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
    stroke-width="1.75"
    stroke-linecap="round"
    stroke-linejoin="round"
    aria-hidden="true"
    focusable="false"
  >
    <template v-if="name === 'overview'">
      <rect x="3" y="3" width="7.5" height="7.5" rx="1.8" />
      <rect x="13.5" y="3" width="7.5" height="7.5" rx="1.8" />
      <rect x="3" y="13.5" width="7.5" height="7.5" rx="1.8" />
      <rect x="13.5" y="13.5" width="7.5" height="7.5" rx="1.8" />
    </template>
    <template v-else-if="name === 'capabilities'">
      <path d="m12 2.8 8.5 4.7-8.5 4.7-8.5-4.7 8.5-4.7Z" />
      <path d="m3.5 12 8.5 4.7 8.5-4.7" />
      <path d="m3.5 16.6 8.5 4.7 8.5-4.7" />
    </template>
    <template v-else-if="name === 'provider' || name === 'cpu'">
      <rect x="7" y="7" width="10" height="10" rx="2" />
      <rect x="10" y="10" width="4" height="4" rx="0.7" />
      <path d="M9.5 3.5V7M14.5 3.5V7M9.5 17v3.5M14.5 17v3.5M3.5 9.5H7M3.5 14.5H7M17 9.5h3.5M17 14.5h3.5" />
    </template>
    <template v-else-if="name === 'platform'">
      <path d="M5 6.5h14a2 2 0 0 1 2 2v7a2 2 0 0 1-2 2h-6l-4 3v-3H5a2 2 0 0 1-2-2v-7a2 2 0 0 1 2-2Z" />
      <path d="M8.5 11.5h7M8.5 14.5h4.5" />
    </template>
    <template v-else-if="name === 'plugin'">
      <path d="M12 3a2.2 2.2 0 0 1 2.2 2.2V7H18a1.8 1.8 0 0 1 1.8 1.8v3.7h-1.6a2.1 2.1 0 0 0 0 4.2h1.6v2.5A1.8 1.8 0 0 1 18 21h-3.8v-1.8a2.2 2.2 0 0 0-4.4 0V21H6a1.8 1.8 0 0 1-1.8-1.8v-3.5H6a2.1 2.1 0 0 0 0-4.2H4.2V8.8A1.8 1.8 0 0 1 6 7h3.8V5.2A2.2 2.2 0 0 1 12 3Z" />
    </template>
    <template v-else-if="name === 'config' || name === 'sliders'">
      <path d="M4 6h9" /><path d="M17 6h3" />
      <circle cx="15" cy="6" r="2.2" />
      <path d="M4 12h3" /><path d="M11 12h9" />
      <circle cx="9" cy="12" r="2.2" />
      <path d="M4 18h11" /><path d="M19 18h1" />
      <circle cx="17" cy="18" r="2.2" />
    </template>
    <template v-else-if="name === 'tasks' || name === 'terminal'">
      <rect x="3" y="4.5" width="18" height="15" rx="2.2" />
      <path d="m7 9 2.6 2.6L7 14.2" />
      <path d="M12.5 15h4.5" />
    </template>
    <template v-else-if="name === 'traces'">
      <path d="M3.5 12h3.2l2.3-5.6 3.4 11.2 2.7-7 1.9 3.4h3.5" />
    </template>
    <template v-else-if="name === 'logs'">
      <rect x="4" y="3" width="16" height="18" rx="2" />
      <path d="M8 7.5h8M8 12h8M8 16.5h5" />
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
      <path d="M12 3v3.4M12 17.6V21M4.6 4.6 7 7m10 10 2.4 2.4M3 12h3.4M17.6 12H21M4.6 19.4 7 17m10-10 2.4-2.4" />
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
    <template v-else-if="name === 'moon'">
      <path d="M20.5 14A8.5 8.5 0 0 1 10 3.5 8.5 8.5 0 1 0 20.5 14Z" />
    </template>
    <template v-else-if="name === 'help'">
      <circle cx="12" cy="12" r="9" />
      <path d="M9.5 9a2.5 2.5 0 1 1 4.4 1.6C12.8 11.4 12 12 12 13.5M12 17h.01" />
    </template>
    <template v-else-if="name === 'arrow'">
      <path d="M5 12h14m-5-5 5 5-5 5" />
    </template>
    <template v-else-if="name === 'check'">
      <path d="m5 12 4.5 4.5L19 7" />
    </template>
    <template v-else-if="name === 'check-circle'">
      <circle cx="12" cy="12" r="9" />
      <path d="m8.5 12.2 2.4 2.4 4.8-5" />
    </template>
    <template v-else-if="name === 'search'">
      <circle cx="11" cy="11" r="6.8" />
      <path d="m20 20-4.2-4.2" />
    </template>
    <template v-else-if="name === 'close'">
      <path d="m6 6 12 12M18 6 6 18" />
    </template>
    <template v-else-if="name === 'chevron-down'">
      <path d="m6 9 6 6 6-6" />
    </template>
    <template v-else-if="name === 'chevron-right'">
      <path d="m9 6 6 6-6 6" />
    </template>
    <template v-else-if="name === 'copy'">
      <rect x="9" y="9" width="11" height="11" rx="2" />
      <path d="M5 15H4.8A1.8 1.8 0 0 1 3 13.2V4.8A1.8 1.8 0 0 1 4.8 3h8.4A1.8 1.8 0 0 1 15 4.8V5" />
    </template>
    <template v-else-if="name === 'settings'">
      <circle cx="12" cy="12" r="3" />
      <path d="M19.4 15a1.6 1.6 0 0 0 .3 1.8l.1.1a2 2 0 0 1-2.8 2.8l-.1-.1a1.6 1.6 0 0 0-1.8-.3 1.6 1.6 0 0 0-1 1.5V21a2 2 0 0 1-4 0v-.1a1.6 1.6 0 0 0-1-1.5 1.6 1.6 0 0 0-1.8.3l-.1.1a2 2 0 0 1-2.8-2.8l.1-.1a1.6 1.6 0 0 0 .3-1.8 1.6 1.6 0 0 0-1.5-1H3a2 2 0 0 1 0-4h.1a1.6 1.6 0 0 0 1.5-1 1.6 1.6 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.6 1.6 0 0 0 1.8.3h.1a1.6 1.6 0 0 0 .9-1.5V3a2 2 0 0 1 4 0v.1a1.6 1.6 0 0 0 1 1.5 1.6 1.6 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.6 1.6 0 0 0-.3 1.8v.1a1.6 1.6 0 0 0 1.5.9H21a2 2 0 0 1 0 4h-.1a1.6 1.6 0 0 0-1.5 1Z" />
    </template>
    <template v-else-if="name === 'plus'">
      <path d="M12 5v14M5 12h14" />
    </template>
    <template v-else-if="name === 'trash'">
      <path d="M4 7h16M9 7V4.8A1.8 1.8 0 0 1 10.8 3h2.4A1.8 1.8 0 0 1 15 4.8V7" />
      <path d="M6.5 7 7.4 19a2 2 0 0 0 2 1.8h5.2a2 2 0 0 0 2-1.8L17.5 7" />
    </template>
    <template v-else-if="name === 'filter'">
      <path d="M3.5 5h17l-6.5 7.4V19l-4 1.6v-8.2L3.5 5Z" />
    </template>
    <template v-else-if="name === 'wrap'">
      <path d="M4 6h16M4 12h13a3 3 0 1 1 0 6h-4" />
      <path d="m15 15.5-2.5 2.5 2.5 2.5" />
      <path d="M4 18h5" />
    </template>
    <template v-else-if="name === 'sidebar'">
      <rect x="3" y="4" width="18" height="16" rx="2" />
      <path d="M9 4v16" />
    </template>
    <template v-else-if="name === 'external'">
      <path d="M14 4h6v6M20 4 10.5 13.5" />
      <path d="M18 13.5V18a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V8a2 2 0 0 1 2-2h4.5" />
    </template>
    <template v-else-if="name === 'history'">
      <path d="M3.5 12a8.5 8.5 0 1 0 2.5-6L3.5 8.5" />
      <path d="M3.5 3.5v5h5" />
      <path d="M12 7.5V12l3 2" />
    </template>
    <template v-else-if="name === 'play'">
      <path d="M7 5.5 18.5 12 7 18.5V5.5Z" />
    </template>
    <template v-else-if="name === 'stop'">
      <rect x="6" y="6" width="12" height="12" rx="2" />
    </template>
    <template v-else-if="name === 'info'">
      <circle cx="12" cy="12" r="9" />
      <path d="M12 11v5M12 8h.01" />
    </template>
    <template v-else-if="name === 'qq'">
      <path d="M12 3.5c-3.5 0-5.8 2.6-5.8 6.4 0 1.3-.9 2.7-1.5 3.8-.5.9.1 1.9 1.1 1.7.7-.1 1.2-.5 1.6-.9.6 1.6 1.7 2.8 3.2 3.3-.9.5-1.5 1.1-1.5 1.7 0 .6 1.3 1 2.9 1s2.9-.4 2.9-1c0-.6-.6-1.2-1.5-1.7 1.5-.5 2.6-1.7 3.2-3.3.4.4.9.8 1.6.9 1 .2 1.6-.8 1.1-1.7-.6-1.1-1.5-2.5-1.5-3.8 0-3.8-2.3-6.4-5.8-6.4Z" />
      <circle cx="10" cy="9.6" r="0.8" fill="currentColor" stroke="none" />
      <circle cx="14" cy="9.6" r="0.8" fill="currentColor" stroke="none" />
    </template>
    <template v-else-if="name === 'feishu'">
      <path d="M4.5 5.5 19.5 10l-5.8 3.2L19 19l-8.5-3.2L4.5 19l2.4-6.4-2.4-7.1Z" />
    </template>
    <template v-else-if="name === 'music'">
      <path d="M9 17.5V6.5l10-2v11" />
      <circle cx="6.5" cy="17.5" r="2.5" />
      <circle cx="16.5" cy="15.5" r="2.5" />
    </template>
    <template v-else-if="name === 'voice'">
      <rect x="9" y="3.5" width="6" height="10.5" rx="3" />
      <path d="M5.5 11.5a6.5 6.5 0 0 0 13 0M12 18v3M8.5 21h7" />
    </template>
    <template v-else-if="name === 'sticker'">
      <circle cx="12" cy="12" r="9" />
      <circle cx="9" cy="10" r="0.9" fill="currentColor" stroke="none" />
      <circle cx="15" cy="10" r="0.9" fill="currentColor" stroke="none" />
      <path d="M8.5 14.5a4.2 4.2 0 0 0 7 0" />
    </template>
    <template v-else-if="name === 'poke'">
      <path d="M12 4.5v7M9 7.5 12 4.5l3 3" />
      <path d="M6.5 13.5a5.5 5.5 0 0 0 11 0" />
      <path d="M8.5 18.5h7" />
    </template>
    <template v-else-if="name === 'profile'">
      <circle cx="12" cy="8.5" r="3.5" />
      <path d="M5 19.5a7 7 0 0 1 14 0" />
    </template>
    <template v-else-if="name === 'vibe'">
      <path d="M12 20s-7-4.4-7-9.8A4.2 4.2 0 0 1 12 7a4.2 4.2 0 0 1 7 3.2c0 5.4-7 9.8-7 9.8Z" />
    </template>
    <template v-else-if="name === 'quota'">
      <path d="M4 18V8m5 10V5m5 13v-7m5 7v-4" />
    </template>
    <template v-else-if="name === 'globe'">
      <circle cx="12" cy="12" r="9" />
      <path d="M3.5 12h17M12 3a13.5 13.5 0 0 1 0 18M12 3a13.5 13.5 0 0 0 0 18" />
    </template>
    <template v-else-if="name === 'python'">
      <path d="M8.5 6.5A2.5 2.5 0 0 1 11 4h2a2.5 2.5 0 0 1 2.5 2.5V10h-7V6.5Z" />
      <path d="M15.5 17.5A2.5 2.5 0 0 1 13 20h-2a2.5 2.5 0 0 1-2.5-2.5V14h7v3.5Z" />
      <path d="M4.5 10.5A2 2 0 0 1 6.5 8.5H15v3.5a2 2 0 0 1-2 2H6.5a2 2 0 0 1-2-2v-1.5Z" />
      <path d="M19.5 13.5a2 2 0 0 1-2 2H9V12a2 2 0 0 1 2-2h6.5a2 2 0 0 1 2 2v1.5Z" />
    </template>
    <template v-else-if="name === 'store'">
      <path d="M4 9.5 5.6 4.5h12.8L20 9.5" />
      <path d="M4 9.5a2.6 2.6 0 0 0 5.2 0 2.6 2.6 0 0 0 5.6 0 2.6 2.6 0 0 0 5.2 0" />
      <path d="M5.5 12v7.5h13V12" />
    </template>
    <template v-else-if="name === 'upload'">
      <path d="M12 15V4.5M8 8.5 12 4.5l4 4" />
      <path d="M4.5 15.5V18a2 2 0 0 0 2 2h11a2 2 0 0 0 2-2v-2.5" />
    </template>
    <template v-else>
      <path d="M13 2.5 4.8 13.4h6.1L9.9 21.5l8.3-11.1h-6.1l1-7.9Z" />
    </template>
  </svg>
</template>
