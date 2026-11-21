<script setup lang="ts">
import AppIcon, { type AppIconName } from './AppIcon.vue'

export type BannerTone = 'info' | 'ok' | 'warn' | 'bad'

const props = withDefaults(defineProps<{
  tone?: BannerTone
  title?: string
  role?: 'status' | 'alert' | 'note'
  testId?: string
}>(), { tone: 'info', title: undefined, role: 'status', testId: undefined })

const iconMap: Record<BannerTone, AppIconName> = {
  info: 'info',
  ok: 'check-circle',
  warn: 'alert',
  bad: 'alert'
}
</script>

<template>
  <div
    class="ui-banner inline-notice"
    :class="props.tone === 'info' ? '' : props.tone"
    :data-tone="props.tone === 'info' ? 'accent' : props.tone"
    :role="props.role"
    :data-testid="props.testId"
  >
    <AppIcon :name="iconMap[props.tone]" :size="16" class="ui-banner-icon" />
    <div class="ui-banner-body">
      <strong v-if="props.title" class="ui-banner-title">{{ props.title }}</strong>
      <div class="ui-banner-copy"><slot /></div>
    </div>
    <div v-if="$slots.actions" class="ui-banner-actions">
      <slot name="actions" />
    </div>
  </div>
</template>

<style scoped>
.ui-banner {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}
.ui-banner-icon {
  margin-top: 2px;
}
.ui-banner-body {
  min-width: 0;
  flex: 1;
}
.ui-banner-title {
  display: block;
  margin-bottom: 3px;
  font-weight: 600;
}
.ui-banner-copy {
  min-width: 0;
}
.ui-banner-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
  flex: none;
}
</style>
