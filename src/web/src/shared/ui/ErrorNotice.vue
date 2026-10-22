<script setup lang="ts">
import type { UiError } from './ui-error'

defineProps<{
  error: UiError | null
  title?: string
}>()
</script>

<template>
  <div v-if="error" class="error-notice" role="alert" data-testid="error-notice">
    <strong>{{ title ?? '请求失败' }}</strong>
    <p>{{ error.message }}</p>
    <p class="error-meta">
      <span>错误码：{{ error.code }}</span>
      <span v-if="error.status !== undefined">HTTP {{ error.status }}</span>
      <span v-if="error.requestId">请求：{{ error.requestId }}</span>
    </p>
  </div>
</template>
