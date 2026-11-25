<script setup lang="ts">
import AppIcon, { type AppIconName } from './AppIcon.vue'

withDefaults(defineProps<{
  title: string
  description?: string
  icon?: AppIconName
  testId?: string
}>(), { description: undefined, icon: 'info', testId: undefined })
</script>

<template>
  <div class="empty-state ui-empty" :data-testid="testId">
    <span class="ui-empty-icon" aria-hidden="true">
      <AppIcon :name="icon" :size="22" />
    </span>
    <strong class="ui-empty-title">{{ title }}</strong>
    <p v-if="description" class="ui-empty-desc">{{ description }}</p>
    <div v-if="$slots.default" class="ui-empty-actions">
      <slot />
    </div>
  </div>
</template>

<style scoped>
.ui-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
}
.ui-empty-icon {
  display: inline-grid;
  place-items: center;
  width: 44px;
  height: 44px;
  border-radius: 50%;
  background: var(--surface);
  border: 1px solid var(--border);
  color: var(--text-soft);
}
.ui-empty-title {
  font-size: var(--text-md);
  font-weight: 600;
  color: var(--text);
}
.ui-empty-desc {
  margin: 0;
  max-width: 46ch;
  color: var(--text-muted);
  font-size: var(--text-sm);
}
.ui-empty-actions {
  display: flex;
  flex-wrap: wrap;
  justify-content: center;
  gap: 8px;
  margin-top: 6px;
}
</style>
