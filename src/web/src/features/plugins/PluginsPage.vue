<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  ZbaClient,
  ZbaRequestError,
  type ZbaConfigurationReadResult,
  type ZbaInstalledPlugins,
  type ZbaManagedDescriptor
} from '../../api/zba'
import { acquireScheduler } from '../../shared/sync/event-scheduler'
import { markOperation, pendingOperations, type PendingOperation } from '../../shared/sync/pending-operations'
import AppIcon from '../../shared/ui/AppIcon.vue'
import BreakableId from '../../shared/ui/BreakableId.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'
import UiBanner from '../../shared/ui/UiBanner.vue'
import UiEmptyState from '../../shared/ui/UiEmptyState.vue'
import UiTabs, { type UiTabOption } from '../../shared/ui/UiTabs.vue'
import { restoreFocus } from '../../shared/ui/focus-restore'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import CapabilitySettingsHost from '../capabilities/CapabilitySettingsHost.vue'
import PluginUiFrame from '../capabilities/PluginUiFrame.vue'
import { configurationFor, type CapabilityConfiguration } from '../capabilities/configuration-ownership'
import { buildDomainCatalog, type PluginCardModel } from '../capabilities/domain-catalog'
import { capabilityTitle } from '../../shared/friendly'
import PluginCard from './PluginCard.vue'
import PluginDetailDrawer from './PluginDetailDrawer.vue'
import PluginMarketplaceWorkspace from './PluginMarketplaceWorkspace.vue'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  features?: readonly string[]
  active?: boolean
}>(), { active: true, features: () => [] })

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
  busy: [value: boolean]
}>()

const headerRef = ref<InstanceType<typeof PageHeader> | null>(null)
const pageHeading = computed(() => headerRef.value?.heading ?? null)
const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)

const activeTab = ref<'installed' | 'catalog' | 'import'>('installed')
const marketFocusId = ref<string | undefined>(undefined)
const statusFilter = ref<'all' | 'enabled' | 'disabled' | 'builtin' | 'extension'>('all')
const search = ref('')

const descriptors = ref<ZbaManagedDescriptor[]>([])
const configSnapshot = ref<ZbaConfigurationReadResult | null>(null)
const installedSnapshot = ref<ZbaInstalledPlugins | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)

const busyCardIds = ref(new Set<string>())
const settingsTarget = ref<{ capability: CapabilityConfiguration; trigger: HTMLElement | null } | null>(null)
const settingsDialog = ref<InstanceType<typeof CapabilitySettingsHost> | null>(null)
const pluginUiTarget = ref<{ pluginId: string; trigger: HTMLElement | null } | null>(null)
const pluginUiDialog = ref<InstanceType<typeof PluginUiFrame> | null>(null)
const detailTarget = ref<{ plugin: PluginCardModel; trigger: HTMLElement | null } | null>(null)
const marketWorkspace = ref<InstanceType<typeof PluginMarketplaceWorkspace> | null>(null)

const configurationRecoveries = computed(() =>
  pendingOperations(props.client, 'configuration')
    .filter(item => item.state !== 'settled' && item.target.startsWith('plugin-switch:'))
)
const recoveryChecking = ref<string | null>(null)
const recoveryNotice = ref('')

let loadSequence = 0
let unmounted = false

const catalog = computed(() => buildDomainCatalog(descriptors.value, configSnapshot.value, installedSnapshot.value))
const plugins = computed(() => catalog.value.plugins)

const enabledCount = computed(() => plugins.value.filter(item => item.descriptor.desiredEnabled).length)
const builtinCount = computed(() => plugins.value.filter(item => item.origin === 'builtin-feature').length)
const extensionCount = computed(() => plugins.value.filter(item => item.origin === 'extension').length)

const mainTabs = computed<UiTabOption<'installed' | 'catalog' | 'import'>[]>(() => [
  { value: 'installed', label: '已安装插件', count: plugins.value.length, icon: 'plugin', testId: 'plugins-tab-installed' },
  { value: 'catalog', label: '插件市场', icon: 'store', testId: 'plugins-tab-catalog' },
  { value: 'import', label: '仓库与导入', icon: 'upload', testId: 'plugins-tab-import' }
])

const subFilters = computed<UiTabOption<'all' | 'enabled' | 'disabled' | 'builtin' | 'extension'>[]>(() => [
  { value: 'all', label: '全部', count: plugins.value.length, testId: 'plugins-filter-all' },
  { value: 'enabled', label: '已启用', count: enabledCount.value, testId: 'plugins-filter-enabled' },
  { value: 'disabled', label: '已停用', count: plugins.value.length - enabledCount.value, testId: 'plugins-filter-disabled' },
  { value: 'builtin', label: '内置功能', count: builtinCount.value, testId: 'plugins-filter-builtin' },
  { value: 'extension', label: '外部扩展', count: extensionCount.value, testId: 'plugins-filter-extension' }
])

const visiblePlugins = computed(() => {
  const q = search.value.trim().toLocaleLowerCase()
  return plugins.value.filter(item => {
    if (statusFilter.value === 'enabled' && !item.descriptor.desiredEnabled) return false
    if (statusFilter.value === 'disabled' && item.descriptor.desiredEnabled) return false
    if (statusFilter.value === 'builtin' && item.origin !== 'builtin-feature') return false
    if (statusFilter.value === 'extension' && item.origin !== 'extension') return false
    if (!q) return true
    return [item.title, item.id, item.summary, item.category]
      .some(value => value.toLocaleLowerCase().includes(q))
  })
})

function eventTrigger(event?: Event): HTMLElement | null {
  return event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
}

function setCardBusy(id: string, value: boolean): void {
  const next = new Set(busyCardIds.value)
  if (value) next.add(id)
  else next.delete(id)
  busyCardIds.value = next
}

const settingsRuntimeState = computed(() => {
  const id = settingsTarget.value?.capability.pluginKey ?? settingsTarget.value?.capability.id
  if (!id) return undefined
  return plugins.value.find(item => item.id === id)?.descriptor.runtimeState
})

function openSettingsById(id: string, trigger?: HTMLElement | null): void {
  if (settingsTarget.value || pluginUiTarget.value) return
  const found = plugins.value.find(item => item.id === id)
  const capability: CapabilityConfiguration = configurationFor(id) ?? {
    id,
    kind: 'plugin',
    pluginKey: id,
    title: found?.title ?? capabilityTitle('plugin', { kind: 'plugin', key: id }),
    summary: found?.summary || '此插件声明的配置与凭据',
    sections: [],
    secrets: [],
    editor: { type: 'declared' }
  }
  settingsTarget.value = { capability, trigger: trigger ?? pageHeading.value }
}

function closeSettings(): void {
  const trigger = settingsTarget.value?.trigger ?? null
  settingsTarget.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
  void load('background')
}

function openPluginUi(pluginId: string, trigger?: HTMLElement | null): void {
  if (settingsTarget.value || pluginUiTarget.value) return
  pluginUiTarget.value = { pluginId, trigger: trigger ?? pageHeading.value }
}

function closePluginUi(): void {
  const trigger = pluginUiTarget.value?.trigger ?? null
  pluginUiTarget.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
  void load('background')
}

function openDetail(plugin: PluginCardModel, event?: Event): void {
  detailTarget.value = { plugin, trigger: eventTrigger(event) }
}

function closeDetail(): void {
  const trigger = detailTarget.value?.trigger ?? null
  detailTarget.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
}

function openInMarket(pluginId: string): void {
  detailTarget.value = null
  marketFocusId.value = pluginId
  activeTab.value = 'catalog'
}

async function checkConfigurationRecovery(record: PendingOperation): Promise<void> {
  if (!canWrite.value || record.state === 'in-flight' || recoveryChecking.value) return
  const client = props.client
  recoveryChecking.value = record.key
  recoveryNotice.value = ''
  try {
    const result = await client.findConfiguration(record.key)
    const receipt = result.receipt
    const confirmed = result.code === 0 && receipt?.key === record.key && receipt.commitState !== 2
      && (receipt.decision === 1 || receipt.commitState === 1)
    if (confirmed || result.code === 14) markOperation(client, record.key, 'settled')
    if (client !== props.client || unmounted) return
    recoveryNotice.value = confirmed
      ? '原开关提交已有确定回执，状态已同步。'
      : result.code === 14 ? '服务端确认没有这次提交。' : '仍无法确认原提交，操作键已保留。'
    if (confirmed) await load('background')
  } catch (cause) {
    if (client === props.client && !unmounted) {
      if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', toUiError(cause))
      else recoveryNotice.value = '核对暂不可用，原操作键仍保留。'
    }
  } finally {
    if (client === props.client) recoveryChecking.value = null
  }
}

async function load(reason: 'interactive' | 'background' = 'interactive'): Promise<void> {
  const sequence = ++loadSequence
  const client = props.client
  if (reason === 'interactive') {
    loading.value = true
    emit('busy', true)
    error.value = null
  }
  try {
    const [capRes, cfgRes, installedRes] = await Promise.allSettled([
      client.listCapabilities({ limit: 100 }),
      client.readConfiguration(1),
      typeof client.installedPlugins === 'function' ? client.installedPlugins() : Promise.resolve(null)
    ])
    if (unmounted || sequence !== loadSequence || client !== props.client) return

    if (capRes.status === 'fulfilled') {
      descriptors.value = capRes.value.items
    } else {
      const cause = capRes.reason
      if (cause instanceof ZbaRequestError && cause.status === 401) { emit('auth-expired', toUiError(cause)); return }
      error.value = toUiError(cause)
    }

    if (cfgRes.status === 'fulfilled') {
      configSnapshot.value = cfgRes.value
    } else if (cfgRes.reason instanceof ZbaRequestError && cfgRes.reason.status === 401) {
      emit('auth-expired', toUiError(cfgRes.reason))
      return
    }

    if (installedRes.status === 'fulfilled' && installedRes.value) {
      installedSnapshot.value = installedRes.value
    }
  } finally {
    if (!unmounted && sequence === loadSequence && reason === 'interactive') {
      loading.value = false
      emit('busy', false)
    }
  }
}

let detachScheduler: (() => void) | null = null
function attachSync(client: ZbaClient): void {
  detachScheduler?.()
  const shared = acquireScheduler(client)
  const unsubscribe = shared.scheduler.subscribe({
    name: 'capabilities',
    matches: (event) => event.type === 'capability.executed' || event.type === 'configuration.saved',
    reload: async () => { if (props.active) await load('background') }
  })
  detachScheduler = () => { unsubscribe(); shared.release() }
}

function discardSecretsOnLeave(): void {
  settingsDialog.value?.discardSecretsOnLeave()
}

async function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') { discardSecretsOnLeave(); return true }
  for (const editor of [settingsDialog.value, pluginUiDialog.value]) {
    if (editor && !(await editor.requestLeave(reason, trigger))) return false
  }
  discardSecretsOnLeave()
  return true
}

watch(() => props.client, (client) => {
  settingsTarget.value = null
  pluginUiTarget.value = null
  detailTarget.value = null
  busyCardIds.value = new Set()
  attachSync(client)
  void load('interactive')
})

onMounted(() => {
  attachSync(props.client)
  void load('interactive')
})

onBeforeUnmount(() => {
  unmounted = true
  detachScheduler?.()
  detachScheduler = null
})

defineExpose({
  reload: async () => {
    await load('interactive')
    if (activeTab.value !== 'installed') await marketWorkspace.value?.reload()
  },
  openSettingsById,
  openPluginUi,
  openMarket: (pluginId?: string) => {
    marketFocusId.value = pluginId
    activeTab.value = 'catalog'
  },
  requestLeave,
  discardSecretsOnLeave,
  draftCount: computed(() => (settingsDialog.value?.draftCount ?? 0) + (marketWorkspace.value?.draftCount ?? 0))
})
</script>

<template>
  <section class="domain-page plugins-page" data-testid="plugins-page">
    <PageHeader
      ref="headerRef"
      eyebrow="PLUGIN CENTER"
      title="插件中心"
      title-id="plugins-title"
      description="管理对话功能插件与第三方扩展的启停、参数、运行界面及市场安装；模型供应商与消息平台已独立分栏。"
    >
      <template #metadata>
        <span>已安装 <strong>{{ plugins.length }}</strong> 个插件 · 已启用 <strong>{{ enabledCount }}</strong></span>
      </template>
      <UiTabs
        :model-value="activeTab"
        :options="mainTabs"
        label="插件中心工作区"
        @update:model-value="activeTab = $event as 'installed' | 'catalog' | 'import'"
      />
    </PageHeader>

    <UiBanner v-if="props.mode !== 'admin'" tone="info" test-id="plugins-readonly">
      当前为标准只读模式，可浏览插件列表、配置说明与市场目录；切换开关、修改参数或安装扩展需进入管理员模式。
      <template #actions>
        <button type="button" class="tonal" data-testid="plugins-enable-admin" :disabled="!props.writesEnabled" @click="emit('enable-admin')">
          进入管理员模式
        </button>
      </template>
    </UiBanner>
    <UiBanner v-else-if="!props.writesEnabled" tone="bad" role="alert">
      契约不兼容，插件管理写操作已禁用。
    </UiBanner>

    <UiBanner v-if="configurationRecoveries.length > 0" tone="warn" title="存在未确认的插件开关提交">
      <p v-for="record in configurationRecoveries" :key="record.key" class="recovery-row">
        <BreakableId :value="record.target" /> · <BreakableId :value="record.key" copyable />
        <button
          type="button"
          class="tonal"
          :disabled="!canWrite || record.state === 'in-flight' || recoveryChecking !== null"
          @click="checkConfigurationRecovery(record)"
        >
          {{ recoveryChecking === record.key ? '核对中…' : '核对原提交' }}
        </button>
      </p>
    </UiBanner>
    <UiBanner v-if="recoveryNotice" tone="info">{{ recoveryNotice }}</UiBanner>
    <ErrorNotice :error="error" title="读取插件列表失败" />

    <template v-if="activeTab === 'installed'">
      <div class="domain-toolbar">
        <UiTabs
          :model-value="statusFilter"
          :options="subFilters"
          variant="pills"
          label="筛选已安装插件"
          @update:model-value="statusFilter = $event as 'all' | 'enabled' | 'disabled' | 'builtin' | 'extension'"
        />
        <label class="search-field">
          <AppIcon name="search" :size="15" class="search-icon" />
          <span class="sr-only">搜索插件</span>
          <input v-model="search" type="search" placeholder="搜索中文名、功能简介或插件 ID" data-testid="plugins-search" />
        </label>
      </div>

      <UiEmptyState
        v-if="loading && plugins.length === 0"
        icon="plugin"
        title="正在读取已安装插件…"
        test-id="plugins-loading"
      />
      <UiEmptyState
        v-else-if="!loading && plugins.length === 0"
        icon="plugin"
        title="暂无已安装的功能插件"
        description="你可以切换到「插件市场」发现并安装新扩展，或从「仓库与导入」加载自定义包。"
        test-id="plugins-empty"
      >
        <button type="button" class="primary" @click="activeTab = 'catalog'">浏览插件市场</button>
      </UiEmptyState>
      <UiEmptyState
        v-else-if="visiblePlugins.length === 0"
        icon="search"
        title="没有匹配的插件"
        description="换个关键词或重置筛选条件再试试。"
      >
        <button type="button" class="tonal" @click="search = ''; statusFilter = 'all'">重置筛选</button>
      </UiEmptyState>

      <div v-else class="cap-grid plugins-grid" data-testid="plugins-grid">
        <PluginCard
          v-for="plugin in visiblePlugins"
          :key="plugin.id"
          :plugin="plugin"
          :client="props.client"
          :configuration="configSnapshot"
          :mode="props.mode"
          :writes-enabled="props.writesEnabled"
          :active="props.active"
          :blocked="Boolean(settingsTarget || pluginUiTarget)"
          :busy="busyCardIds.has(plugin.id)"
          @busy="setCardBusy(plugin.id, $event)"
          @saved="load('background')"
          @auth-expired="emit('auth-expired', $event)"
          @settings="(p, ev) => openSettingsById(p.id, eventTrigger(ev))"
          @open-ui="(p, ev) => openPluginUi(p.id, eventTrigger(ev))"
          @detail="(p, ev) => openDetail(p, ev)"
        />
      </div>
    </template>

    <PluginMarketplaceWorkspace
      v-else
      ref="marketWorkspace"
      :client="props.client"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :active="props.active"
      :section="activeTab"
      :focus-plugin-id="marketFocusId"
      @changed="load('background')"
      @open-settings="id => openSettingsById(id, pageHeading)"
      @open-ui="id => openPluginUi(id, pageHeading)"
      @enable-admin="emit('enable-admin')"
      @auth-expired="emit('auth-expired', $event)"
    />

    <PluginDetailDrawer
      v-if="detailTarget"
      :plugin="detailTarget.plugin"
      :trigger="detailTarget.trigger"
      :fallback="pageHeading"
      @close="closeDetail"
      @settings="p => { closeDetail(); openSettingsById(p.id, pageHeading) }"
      @open-ui="p => { closeDetail(); openPluginUi(p.id, pageHeading) }"
      @open-market="openInMarket"
    />

    <CapabilitySettingsHost
      v-if="settingsTarget"
      ref="settingsDialog"
      :client="props.client"
      :capability="settingsTarget.capability"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :features="props.features"
      :runtime-state="settingsRuntimeState"
      :active="props.active"
      :trigger="settingsTarget.trigger"
      :fallback="pageHeading"
      @close="closeSettings"
      @saved="load('background')"
      @enable-admin="emit('enable-admin')"
      @auth-expired="emit('auth-expired', $event)"
    />

    <PluginUiFrame
      v-if="pluginUiTarget"
      ref="pluginUiDialog"
      :key="pluginUiTarget.pluginId"
      :client="props.client"
      :plugin-id="pluginUiTarget.pluginId"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :active="props.active"
      :trigger="pluginUiTarget.trigger"
      :fallback="pageHeading"
      @close="closePluginUi"
      @saved="load('background')"
      @auth-expired="emit('auth-expired', $event)"
    />
  </section>
</template>

<style scoped>
.domain-toolbar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.search-field {
  position: relative;
  display: inline-flex;
  align-items: center;
  min-width: min(280px, 100%);
  flex: 0 1 320px;
}
.search-icon {
  position: absolute;
  left: 11px;
  color: var(--text-soft);
  pointer-events: none;
}
.search-field input {
  width: 100%;
  padding-left: 34px;
}
.recovery-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin: 6px 0 0;
}
</style>
