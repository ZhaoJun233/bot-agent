<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ZbaClient } from '../../api/zba'
import type { UiError } from '../../shared/ui/ui-error'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import ConfigurationPage from '../configuration/ConfigurationPage.vue'
import type { CapabilityConfiguration } from './configuration-ownership'
const props = defineProps<{
  client: ZbaClient; mode: 'read' | 'admin'; writesEnabled: boolean; active: boolean
  capability: CapabilityConfiguration; trigger?: HTMLElement | null; fallback?: HTMLElement | null
}>()
const emit = defineEmits<{ close: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
const editor = ref<InstanceType<typeof ConfigurationPage> | null>(null)
async function close(): Promise<void> {
  // Pending submissions cannot be discarded; use the editor's existing leave/receipt state machine.
  if (editor.value?.hasUnresolvedIntent) return
  if (await editor.value?.requestLeave('close') ?? true) emit('close')
}
defineExpose({
  requestLeave: (reason: 'lock' | 'downgrade', trigger?: HTMLElement | null) => editor.value?.requestLeave(reason, trigger) ?? Promise.resolve(true),
  discardSecretsOnLeave: () => editor.value?.discardSecretsOnLeave(),
  endSession: () => editor.value?.endSession(),
  draftCount: computed(() => editor.value?.draftCount ?? 0)
})
</script>
<template>
  <ManagementDialog :title="props.capability.title" title-id="ability-configuration-title" test-id="ability-configuration-dialog"
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" wide @close="close">
    <p class="muted">{{ props.capability.summary }}</p>
    <ConfigurationPage ref="editor" :client="props.client" :mode="props.mode" :writes-enabled="props.writesEnabled"
      :scope="props.capability.id" :active="props.active" @enable-admin="emit('enable-admin')" @auth-expired="value => emit('auth-expired', value)" />
  </ManagementDialog>
</template>
<style scoped>
.muted { color: var(--text-muted); line-height: 1.7; }

</style>
