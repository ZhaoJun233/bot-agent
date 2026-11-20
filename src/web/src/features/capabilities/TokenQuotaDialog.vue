<script setup lang="ts">
import { computed, ref } from 'vue'
import QuotaEditor from '../../../../../packages/token-quota/frontend/TokenQuotaDialog.vue'
import { quotaPresentation } from '../../../../../packages/token-quota/frontend/presentation'
import type { QuotaClient } from '../../../../../packages/token-quota/frontend/quotas'
import type { UiError } from '../../../../../contracts/ui'
import type { ZbaClient } from '../../api/zba'
import { createPluginUiBridge } from './plugin-ui-bridge'

const props = withDefaults(defineProps<{
  client: ZbaClient; mode: 'read' | 'admin'; writesEnabled: boolean; features: readonly string[]
  active?: boolean; trigger?: HTMLElement | null; fallback?: HTMLElement | null; runtimeState?: number
}>(), { active: true, trigger: null, fallback: null })
const emit = defineEmits<{ close: []; saved: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
const ui = createPluginUiBridge(() => props, quotaPresentation.pluginKey, 'quota')
const clients = new WeakMap<ZbaClient, QuotaClient>()
const client = computed(() => {
  const source = props.client
  let api = clients.get(source)
  if (!api) {
    api = Object.freeze({ listQuotas: () => source.listQuotas(),
      saveQuota: (request: Parameters<QuotaClient['saveQuota']>[0]) => source.saveQuota(request),
      findQuota: (key: string) => source.findQuota(key) })
    clients.set(source, api)
  }
  return api
})
const editor = ref<InstanceType<typeof QuotaEditor> | null>(null)
defineExpose({
  requestLeave: (reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null) =>
    editor.value?.requestLeave(reason, trigger) ?? Promise.resolve(true),
  draftCount: computed(() => editor.value?.draftCount ?? 0)
})
</script>

<template>
  <QuotaEditor ref="editor" v-bind="props" :client="client" :ui="ui" @close="emit('close')"
    @saved="emit('saved')" @enable-admin="emit('enable-admin')" @auth-expired="(value: UiError) => emit('auth-expired', value)" />
</template>
