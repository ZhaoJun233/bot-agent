<script setup lang="ts">
import AppIcon from '../../shared/ui/AppIcon.vue'
import BreakableId from '../../shared/ui/BreakableId.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import PluginGovernanceNotice from '../capabilities/PluginGovernanceNotice.vue'
import { runtimeLabel, runtimeTone, type PluginCardModel } from '../capabilities/domain-catalog'

const props = defineProps<{
  plugin: PluginCardModel
  trigger: HTMLElement | null
  fallback: HTMLElement | null
}>()

const emit = defineEmits<{
  close: []
  settings: [plugin: PluginCardModel]
  'open-ui': [plugin: PluginCardModel]
  'open-market': [pluginId: string]
}>()
</script>

<template>
  <ManagementDialog
    :title="props.plugin.title"
    title-id="plugin-detail-title"
    test-id="plugin-detail-drawer"
    :trigger="props.trigger"
    :fallback="props.fallback"
    drawer
    @close="emit('close')"
  >
    <template #eyebrow>
      <span class="eyebrow">
        {{ props.plugin.origin === 'builtin-feature' ? 'BUILTIN FEATURE PLUGIN' : 'EXTENSION PLUGIN' }}
      </span>
    </template>

    <div class="detail-stack">
      <p class="detail-summary">{{ props.plugin.summary }}</p>

      <dl class="detail-kv">
        <div>
          <dt>插件标识</dt>
          <dd class="mono"><BreakableId :value="props.plugin.id" copyable copy-label="复制插件 ID" /></dd>
        </div>
        <div>
          <dt>运行状态</dt>
          <dd>
            <StatusBadge
              :tone="props.plugin.restartRequired ? 'warn' : runtimeTone(props.plugin.descriptor.runtimeState)"
              :label="props.plugin.restartRequired ? '需重启生效' : runtimeLabel(props.plugin.descriptor.runtimeState)"
              dot
            />
          </dd>
        </div>
        <div>
          <dt>版本号</dt>
          <dd class="mono">{{ props.plugin.version ? 'v' + props.plugin.version : '随宿主内置' }}</dd>
        </div>
        <div>
          <dt>生效机制</dt>
          <dd>{{ props.plugin.adoption === 'cold' ? '冷重载（修改后需重启 BotAgent 生效）' : '支持热应用（以保存回执为准）' }}</dd>
        </div>
        <div>
          <dt>运行时</dt>
          <dd>{{ props.plugin.runtime === 'python' ? 'Python 子进程插件' : '.NET 插件包' }}</dd>
        </div>
        <div v-if="props.plugin.installed?.manifest.minimumHostVersion">
          <dt>最低宿主版本</dt>
          <dd class="mono">{{ props.plugin.installed.manifest.minimumHostVersion }}</dd>
        </div>
        <div v-if="props.plugin.installed?.manifest.permissions?.length">
          <dt>声明权限</dt>
          <dd>
            <ul class="perm-list">
              <li v-for="perm in props.plugin.installed.manifest.permissions" :key="perm" class="mono">{{ perm }}</li>
            </ul>
          </dd>
        </div>
        <div v-if="props.plugin.installed?.manifest.sha256">
          <dt>包校验摘要 (SHA-256)</dt>
          <dd class="mono hash"><BreakableId :value="props.plugin.installed.manifest.sha256" copyable /></dd>
        </div>
      </dl>

      <PluginGovernanceNotice
        v-if="props.plugin.installed?.installedGovernance || props.plugin.installed?.governance"
        :state="props.plugin.installed?.installedGovernance ?? props.plugin.installed?.governance"
        installed
      />
    </div>

    <template #footer>
      <button
        v-if="props.plugin.hasRuntimeUi"
        type="button"
        class="primary"
        @click="emit('open-ui', props.plugin)"
      >
        <AppIcon name="external" :size="14" />
        打开插件界面
      </button>
      <button type="button" class="tonal" @click="emit('settings', props.plugin)">
        <AppIcon name="settings" :size="14" />
        参数设置
      </button>
      <button
        type="button"
        class="tonal"
        data-testid="plugin-detail-open-market"
        @click="emit('open-market', props.plugin.id)"
      >
        <AppIcon name="store" :size="14" />
        {{ props.plugin.origin === 'extension' ? '在市场中更新 / 卸载' : '在插件市场中查看' }}
      </button>
      <button type="button" @click="emit('close')">关闭</button>
    </template>
  </ManagementDialog>
</template>

<style scoped>
.detail-stack {
  display: grid;
  gap: 16px;
}
.detail-summary {
  margin: 0;
  font-size: var(--text-base);
  color: var(--text-muted);
  line-height: 1.65;
}
.detail-kv {
  display: grid;
  gap: 12px;
  margin: 0;
}
.detail-kv > div {
  display: grid;
  gap: 4px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--border-subtle);
}
.detail-kv dt {
  font-size: var(--text-xs);
  color: var(--text-soft);
}
.detail-kv dd {
  margin: 0;
  font-size: var(--text-sm);
  overflow-wrap: anywhere;
}
.perm-list {
  margin: 4px 0 0;
  padding-left: 18px;
  display: grid;
  gap: 4px;
}
.hash {
  word-break: break-all;
  font-size: var(--text-xs);
}
</style>
