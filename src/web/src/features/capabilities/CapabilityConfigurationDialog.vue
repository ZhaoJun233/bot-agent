<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ZbaClient } from '../../api/zba'
import type { UiError } from '../../shared/ui/ui-error'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import ConfigurationPage from '../configuration/ConfigurationPage.vue'
import type { CapabilityConfiguration } from './configuration-ownership'

const props = defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active: boolean
  capability: CapabilityConfiguration
  policyPlatformId?: string
  dialogTitle?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>()
const emit = defineEmits<{ close: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
const editor = ref<InstanceType<typeof ConfigurationPage> | null>(null)

async function close(): Promise<void> {
  if (editor.value?.hasUnresolvedIntent) return
  if (await editor.value?.requestLeave('close') ?? true) emit('close')
}

defineExpose({
  requestLeave: (reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null) =>
    editor.value?.requestLeave(reason, trigger) ?? Promise.resolve(true),
  discardSecretsOnLeave: () => editor.value?.discardSecretsOnLeave(),
  endSession: () => editor.value?.endSession(),
  draftCount: computed(() => editor.value?.draftCount ?? 0)
})
</script>

<template>
  <ManagementDialog
    :title="props.dialogTitle ?? props.capability.title"
    title-id="ability-configuration-title"
    test-id="ability-configuration-dialog"
    :active="props.active"
    :trigger="props.trigger"
    :fallback="props.fallback"
    wide
    drawer
    @close="close"
  >
    <ConfigurationPage
      ref="editor"
      :client="props.client"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :scope="props.capability.id"
      :policy-platform-id="props.policyPlatformId"
      :active="props.active"
      @enable-admin="emit('enable-admin')"
      @auth-expired="value => emit('auth-expired', value)"
    />
  </ManagementDialog>
</template>
