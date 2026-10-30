<script setup lang="ts">
import AppIcon from './AppIcon.vue'
import { useTheme } from './theme'
const { theme, toggle } = useTheme()
</script>

<template>
  <button type="button" class="theme-toggle" data-testid="theme-toggle"
    :aria-label="theme === 'light' ? '切换到深色主题' : '切换到浅色主题'"
    :title="theme === 'light' ? '切换到深色主题' : '切换到浅色主题'" @click="toggle">
    <AppIcon :name="theme === 'light' ? 'moon' : 'sun'" :size="18" />
  </button>
</template>
