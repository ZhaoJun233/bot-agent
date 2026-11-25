<script setup lang="ts">
import type { ZbaClient, ZbaConfigurationReadResult } from '../../api/zba'
import AppIcon from '../../shared/ui/AppIcon.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import type { UiError } from '../../shared/ui/ui-error'
import RegisteredPluginSwitch from '../capabilities/RegisteredPluginSwitch.vue'
import { keySlug, runtimeLabel, runtimeTone, type PluginCardModel } from '../capabilities/domain-catalog'

const props = defineProps<{
  plugin: PluginCardModel
  client: ZbaClient
  configuration: ZbaConfigurationReadResult | null
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active: boolean
  blocked: boolean
  busy: boolean
}>()

const emit = defineEmits<{
  busy: [value: boolean]
  pinned: [value: boolean]
  saved: []
  'auth-expired': [error: UiError]
  settings: [plugin: PluginCardModel, event: Event]
  'open-ui': [plugin: PluginCardModel, event: Event]
  detail: [plugin: PluginCardModel, event: Event]
}>()

const slug = keySlug(props.plugin.descriptor.key)
</script>

<template>
  <article
    class="cap-card plugin-card"
    :data-testid="'cap-item-' + slug"
    :data-plugin-id="props.plugin.id"
  >
    <header class="plugin-card-head">
      <span class="cap-avatar" data-domain="plugin" aria-hidden="true">
        <AppIcon :name="props.plugin.icon" :size="19" />
      </span>
      <div class="plugin-card-title">
        <div class="plugin-title-row">
          <h2 class="cap-name">{{ props.plugin.title }}</h2>
          <span class="domain-pill" data-domain="plugin">
            {{ props.plugin.origin === 'builtin-feature' ? '内置功能' : props.plugin.runtime === 'python' ? 'Python 扩展' : '扩展插件' }}
          </span>
        </div>
        <div class="plugin-meta-line">
          <StatusBadge
            :tone="props.plugin.restartRequired ? 'warn' : runtimeTone(props.plugin.descriptor.runtimeState)"
            :label="props.plugin.restartRequired ? '需重启生效' : runtimeLabel(props.plugin.descriptor.runtimeState)"
            dot
            data-testid="cap-state"
          />
          <span v-if="props.plugin.version" class="plugin-ver mono" :title="'插件版本 ' + props.plugin.version">
            v{{ props.plugin.version }}
          </span>
          <span v-if="props.plugin.adoption === 'cold'" class="plugin-adoption">需重启应用</span>
        </div>
      </div>
    </header>

    <p class="plugin-summary">{{ props.plugin.summary }}</p>

    <RegisteredPluginSwitch
      :client="props.client"
      :plugin-key="props.plugin.id"
      :minimal="true"
      :adoption="props.plugin.adoption"
      :configuration="props.configuration"
      :title="props.plugin.title"
      :runtime-state="props.plugin.descriptor.runtimeState"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :active="props.active"
      :blocked="props.blocked"
      @busy="emit('busy', $event)"
      @pinned="emit('pinned', $event)"
      @saved="emit('saved')"
      @auth-expired="emit('auth-expired', $event)"
    />

    <p v-if="props.plugin.descriptor.errorCode && props.plugin.descriptor.errorCode !== 'plugin_restart_required'" class="inline-notice bad plugin-err">
      运行异常：<code>{{ props.plugin.descriptor.errorCode }}</code>
    </p>

    <footer class="cap-card-foot">
      <button
        v-if="props.plugin.hasRuntimeUi"
        type="button"
        class="primary plugin-ui-btn"
        :data-testid="'plugin-open-ui-' + props.plugin.id"
        :disabled="props.busy"
        @click="emit('open-ui', props.plugin, $event)"
      >
        <AppIcon name="external" :size="14" />
        打开界面
      </button>
      <button
        type="button"
        class="tonal"
        :disabled="props.busy"
        :data-testid="'cap-settings-' + slug"
        :aria-label="'设置 ' + props.plugin.title"
        @click="emit('settings', props.plugin, $event)"
      >
        <AppIcon name="settings" :size="14" />
        设置
      </button>
      <button
        type="button"
        class="ghost"
        :data-testid="'cap-inspect-' + slug"
        @click="emit('detail', props.plugin, $event)"
      >
        详情
      </button>
    </footer>
  </article>
</template>

<style scoped>
.plugin-card {
  gap: 12px;
  padding: 16px 18px;
}
.plugin-card-head {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}
.plugin-card-title {
  min-width: 0;
  flex: 1;
  display: grid;
  gap: 6px;
}
.plugin-title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.plugin-meta-line {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}
.plugin-ver {
  font-size: var(--text-xs);
  color: var(--text-soft);
}
.plugin-adoption {
  font-size: var(--text-xs);
  color: var(--warn-text);
}
.plugin-summary {
  margin: 0;
  font-size: var(--text-sm);
  color: var(--text-muted);
  line-height: 1.55;
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}
.plugin-err {
  padding: 8px 10px;
  font-size: var(--text-xs);
}
.plugin-ui-btn {
  min-height: 32px;
  padding: 6px 12px;
}
@media (max-width: 640px) {
  .plugin-ui-btn {
    min-height: 44px;
  }
}
</style>
