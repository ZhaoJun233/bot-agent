<script setup lang="ts">
import { computed, ref } from 'vue'
import type { ZbaClient, ZbaDriverKey } from '../../api/zba'
import type { UiError } from '../../shared/ui/ui-error'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import ConfigurationPage from '../configuration/ConfigurationPage.vue'
import type { CapabilityConfiguration } from './configuration-ownership'
import EmbeddedDeclaredPluginSettings from './EmbeddedDeclaredPluginSettings.vue'

const props = defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active: boolean
  capability: CapabilityConfiguration
  policyPlatformId?: string
  driverKey?: ZbaDriverKey
  dialogTitle?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>()
const emit = defineEmits<{ close: []; saved: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
const editor = ref<InstanceType<typeof ConfigurationPage> | null>(null)
const embeddedDeclared = ref<InstanceType<typeof EmbeddedDeclaredPluginSettings> | null>(null)

async function close(): Promise<void> {
  if (editor.value?.hasUnresolvedIntent || embeddedDeclared.value?.hasUnresolvedIntent) return
  if (!(await editor.value?.requestLeave('close') ?? true)) return
  if (!(await embeddedDeclared.value?.requestLeave('close') ?? true)) return
  editor.value?.discardSecretsOnLeave()
  embeddedDeclared.value?.discardSecretsOnLeave()
  emit('close')
}

defineExpose({
  requestLeave: async (reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null) => {
    if (reason === 'lock') {
      editor.value?.discardSecretsOnLeave()
      embeddedDeclared.value?.discardSecretsOnLeave()
      return true
    }
    if (!(await editor.value?.requestLeave(reason, trigger) ?? true)) return false
    if (!(await embeddedDeclared.value?.requestLeave(reason, trigger) ?? true)) return false
    editor.value?.discardSecretsOnLeave()
    embeddedDeclared.value?.discardSecretsOnLeave()
    return true
  },
  discardSecretsOnLeave: () => {
    editor.value?.discardSecretsOnLeave()
    embeddedDeclared.value?.discardSecretsOnLeave()
  },
  endSession: () => {
    editor.value?.endSession()
    embeddedDeclared.value?.endSession()
  },
  draftCount: computed(() => (editor.value?.draftCount ?? 0) + (embeddedDeclared.value?.dirty ? 1 : 0))
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
      :driver-key="props.driverKey"
      :active="props.active"
      @enable-admin="emit('enable-admin')"
      @auth-expired="value => emit('auth-expired', value)"
    />
    <EmbeddedDeclaredPluginSettings
      ref="embeddedDeclared"
      :client="props.client"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :active="props.active"
      :plugin-id="props.capability.pluginKey ?? props.capability.id"
      @saved="emit('saved')"
      @enable-admin="emit('enable-admin')"
      @auth-expired="value => emit('auth-expired', value)"
    />
  </ManagementDialog>
</template>
