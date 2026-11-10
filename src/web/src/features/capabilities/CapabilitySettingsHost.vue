<script setup lang="ts">
import { computed, defineAsyncComponent, defineComponent, h, ref } from 'vue'
import type { ZbaClient } from '../../api/zba'
import type { UiError } from '../../shared/ui/ui-error'
import type { CapabilityConfiguration } from './configuration-ownership'
import CapabilityConfigurationDialog from './CapabilityConfigurationDialog.vue'
import DeclaredPluginSettingsDialog from './DeclaredPluginSettingsDialog.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'

const props = defineProps<{
  client: ZbaClient; mode: 'read' | 'admin'; writesEnabled: boolean; active: boolean
  capability: CapabilityConfiguration; features: readonly string[]; runtimeState?: number
  trigger?: HTMLElement | null; fallback?: HTMLElement | null
}>()
const emit = defineEmits<{ close: []; saved: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
interface SettingsEditor {
  requestLeave(reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null): Promise<boolean>
  discardSecretsOnLeave?(): void
  endSession?(): void
  readonly draftCount?: number
}
const editor = ref<SettingsEditor | null>(null)
const loadFailed = ref(false)
// The host is keyed by owner. It adapts editor lifetime, never field ownership or save semantics.
const renderer = computed(() => {
  const spec = props.capability.editor
  if (spec?.type === 'declared') return DeclaredPluginSettingsDialog
  if (spec?.type === 'component') return defineAsyncComponent({
    async loader() {
      try { return (await spec.load()).default }
      catch {
        loadFailed.value = true
        return defineComponent({ inheritAttrs: false, render: () => null })
      }
    },
    delay: 0,
    loadingComponent: defineComponent({
      inheritAttrs: false,
      setup: () => () => h(ManagementDialog, {
        title: props.capability.title, titleId: 'settings-loading-title', testId: 'settings-loading',
        active: props.active, trigger: props.trigger, fallback: props.fallback,
        onClose: () => emit('close')
      }, () => h('p', { role: 'status' }, '正在加载设置界面…'))
    })
  })
  return CapabilityConfigurationDialog
})
const editorProps = computed(() => {
  const common = { client: props.client, mode: props.mode, writesEnabled: props.writesEnabled,
    active: props.active, trigger: props.trigger, fallback: props.fallback }
  switch (props.capability.editor.type) {
    case 'declared': return { ...common, pluginId: props.capability.pluginKey ?? props.capability.id }
    case 'component': return { ...common, features: props.features, runtimeState: props.runtimeState }
    default: return { ...common, capability: props.capability }
  }
})
const editorEvents = computed((): Record<string, () => void> => {
  const type = props.capability.editor.type
  if (type === 'declared') return { changed: () => emit('saved') }
  if (type === 'component') return { saved: () => emit('saved') }
  return {}
})
defineExpose({
  requestLeave: (reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null) =>
    editor.value?.requestLeave(reason, trigger) ?? Promise.resolve(true),
  discardSecretsOnLeave: () => editor.value?.discardSecretsOnLeave?.(),
  endSession: () => editor.value?.endSession?.(),
  draftCount: computed(() => editor.value?.draftCount ?? 0)
})
</script>

<template>
  <ManagementDialog v-if="loadFailed" :title="props.capability.title" title-id="settings-load-error-title"
    test-id="settings-load-error" :active="props.active" :trigger="props.trigger" :fallback="props.fallback"
    @close="emit('close')">
    <p role="alert">设置界面加载失败。请关闭后重试；未提交任何配置。</p>
  </ManagementDialog>
  <component :is="renderer" v-else ref="editor" v-bind="editorProps"
    v-on="editorEvents" @close="emit('close')"
    @enable-admin="emit('enable-admin')" @auth-expired="(value: UiError) => emit('auth-expired', value)" />
</template>
