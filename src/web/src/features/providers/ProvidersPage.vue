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
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import UiBanner from '../../shared/ui/UiBanner.vue'
import UiEmptyState from '../../shared/ui/UiEmptyState.vue'
import UiTabs, { type UiTabOption } from '../../shared/ui/UiTabs.vue'
import { restoreFocus } from '../../shared/ui/focus-restore'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import CapabilityOperationPanel from '../capabilities/CapabilityOperationPanel.vue'
import CapabilitySettingsHost from '../capabilities/CapabilitySettingsHost.vue'
import ProviderEditorDialog from '../capabilities/ProviderEditorDialog.vue'
import { configurationFor, type CapabilityConfiguration } from '../capabilities/configuration-ownership'
import { buildDomainCatalog, keyIdentity, keySlug, runtimeLabel, runtimeTone, type ProviderCardModel } from '../capabilities/domain-catalog'
import { protocolLabel } from '../capabilities/provider-protocols'
import { supportedActions, useCapabilityOperations } from '../capabilities/use-capability-operations'

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

const descriptors = ref<ZbaManagedDescriptor[]>([])
const configSnapshot = ref<ZbaConfigurationReadResult | null>(null)
const installedSnapshot = ref<ZbaInstalledPlugins | null>(null)
const context = ref<{ generation: string; contractRevision: string } | null>(null)
const contextError = ref<UiError | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)
const search = ref('')
const statusFilter = ref<'all' | 'enabled' | 'disabled'>('all')
const detailTarget = ref<ProviderCardModel | null>(null)
const detailTrigger = ref<HTMLElement | null>(null)

const providerTarget = ref<{ key?: string; trigger: HTMLElement | null } | null>(null)
const providerDialog = ref<InstanceType<typeof ProviderEditorDialog> | null>(null)
const routingTarget = ref<{ capability: CapabilityConfiguration; trigger: HTMLElement | null } | null>(null)
const routingDialog = ref<InstanceType<typeof CapabilitySettingsHost> | null>(null)

const configurationRecoveries = computed(() =>
  pendingOperations(props.client, 'configuration')
    .filter(item => item.state !== 'settled' && (item.target.startsWith('provider:') || item.target === 'models'))
)
const recoveryChecking = ref<string | null>(null)
const recoveryNotice = ref('')

let loadSequence = 0
let unmounted = false

const catalog = computed(() => buildDomainCatalog(descriptors.value, configSnapshot.value, installedSnapshot.value))
const providers = computed(() => catalog.value.providers)
const modelEngine = computed(() => catalog.value.modelServicesPackage)

const enabledCount = computed(() => providers.value.filter(item => item.configView?.desiredEnabled ?? item.descriptor.desiredEnabled).length)
const disabledCount = computed(() => providers.value.length - enabledCount.value)

const filterOptions = computed<UiTabOption<'all' | 'enabled' | 'disabled'>[]>(() => [
  { value: 'all', label: '全部', count: providers.value.length, testId: 'providers-filter-all' },
  { value: 'enabled', label: '已启用', count: enabledCount.value, testId: 'providers-filter-enabled' },
  { value: 'disabled', label: '已停用', count: disabledCount.value, testId: 'providers-filter-disabled' }
])

const visibleProviders = computed(() => {
  const q = search.value.trim().toLocaleLowerCase()
  return providers.value.filter(item => {
    const isEnabled = item.configView?.desiredEnabled ?? item.descriptor.desiredEnabled
    if (statusFilter.value === 'enabled' && !isEnabled) return false
    if (statusFilter.value === 'disabled' && isEnabled) return false
    if (!q) return true
    const proto = protocolLabel(item.configView?.protocol)
    return [item.title, item.key, proto, item.configView?.modelName.display ?? '', item.configView?.baseUrl.display ?? '']
      .some(value => value.toLocaleLowerCase().includes(q))
  })
})

const ops = useCapabilityOperations({
  client: () => props.client,
  canWrite: () => canWrite.value,
  active: () => props.active,
  context,
  onDescriptorUpdated: (fresh) => {
    const id = keyIdentity(fresh.key)
    descriptors.value = descriptors.value.map(item => keyIdentity(item.key) === id ? fresh : item)
  },
  onAuthExpired: (ui) => emit('auth-expired', ui)
})

const credentialLabels: Record<number, string> = {
  0: '未配置',
  1: '共用主凭据',
  2: '独立凭据',
  3: '需人工核对'
}

function credentialText(item: ProviderCardModel): string {
  if (item.configView) return credentialLabels[item.configView.credential] ?? '未知'
  return item.descriptor.credentialConfigured ? '已配置' : '未配置'
}

function testActionFor(item: ProviderCardModel) {
  return supportedActions(item.descriptor).find(cap => cap.action === 5) ?? null
}

function eventTrigger(event?: Event): HTMLElement | null {
  return event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
}

function openProvider(key?: string, event?: Event): void {
  if (providerTarget.value || routingTarget.value) return
  providerTarget.value = { key, trigger: eventTrigger(event) }
}

function closeProvider(): void {
  const trigger = providerTarget.value?.trigger ?? null
  providerTarget.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
  void load('background')
}

function openRoutingSettings(event?: Event): void {
  if (providerTarget.value || routingTarget.value) return
  const capability = configurationFor('models')
  if (!capability) return
  routingTarget.value = { capability, trigger: eventTrigger(event) }
}

function closeRoutingSettings(): void {
  const trigger = routingTarget.value?.trigger ?? null
  routingTarget.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
  void load('background')
}

function openDetail(item: ProviderCardModel, event?: Event): void {
  detailTrigger.value = eventTrigger(event)
  detailTarget.value = item
}

function closeDetail(): void {
  const trigger = detailTrigger.value
  detailTarget.value = null
  detailTrigger.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
}

async function testSavedProvider(key: string): Promise<void> {
  if (!canWrite.value) return
  try {
    const result = await props.client.inspectCapability({ kind: 'provider', key })
    const descriptor = result.descriptor
    const action = descriptor?.actions.find(cap => cap.action === 5 && cap.supported)
    providerTarget.value = null
    if (result.code !== 0 || !descriptor || !action) {
      ops.abortedNotice.value = '此模型供应商暂未公布测试连接动作，请刷新后核对。'
      return
    }
    await nextTick()
    ops.requestAction(descriptor, action)
  } catch (cause) {
    if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', toUiError(cause))
    else ops.actionError.value = toUiError(cause)
  }
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
      ? '原提交已有确定回执，配置已同步刷新。'
      : result.code === 14
        ? '服务端确认没有这次提交，不会自动重发。'
        : '仍无法确认原提交，原操作键已保留。'
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
      if (cfgRes.value.code === 0 && cfgRes.value.generation && cfgRes.value.contractRevision) {
        context.value = { generation: cfgRes.value.generation, contractRevision: cfgRes.value.contractRevision }
        contextError.value = null
      } else {
        context.value = null
        contextError.value = { code: 'configuration_context_missing', message: '未获取到配置代际，连接测试暂不可提交。' }
      }
    } else if (cfgRes.reason instanceof ZbaRequestError && cfgRes.reason.status === 401) {
      emit('auth-expired', toUiError(cfgRes.reason))
      return
    } else {
      context.value = null
      contextError.value = toUiError(cfgRes.reason)
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
  providerDialog.value?.discardSecretsOnLeave()
  routingDialog.value?.discardSecretsOnLeave()
}

async function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') { discardSecretsOnLeave(); return true }
  for (const editor of [providerDialog.value, routingDialog.value]) {
    if (editor && !(await editor.requestLeave(reason, trigger))) return false
  }
  discardSecretsOnLeave()
  return true
}

watch(() => props.client, (client) => {
  providerTarget.value = null
  routingTarget.value = null
  detailTarget.value = null
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
  reload: () => load('interactive'),
  openRoutingSettings: () => openRoutingSettings(),
  requestLeave,
  discardSecretsOnLeave,
  draftCount: computed(() => (providerDialog.value?.draftCount ?? 0) + (routingDialog.value?.draftCount ?? 0))
})
</script>

<template>
  <section class="domain-page providers-page" data-testid="providers-page">
    <PageHeader
      ref="headerRef"
      eyebrow="MODEL PROVIDERS"
      title="模型供应商"
      title-id="providers-title"
      description="统一管理模型供应商接入、协议与优先级、默认主/快模型路由及连接测试。"
    >
      <template #metadata>
        <span>共 <strong>{{ providers.length }}</strong> 个模型供应商 · 已启用 <strong>{{ enabledCount }}</strong></span>
        <span v-if="modelEngine" class="engine-pill" data-testid="model-engine-badge">
          <AppIcon name="cpu" :size="14" />
          <span>路由引擎</span>
          <code v-if="modelEngine.version">v{{ modelEngine.version }}</code>
          <StatusBadge
            :tone="modelEngine.restartRequired ? 'warn' : runtimeTone(modelEngine.runtimeState)"
            :label="modelEngine.restartRequired ? '需重启' : runtimeLabel(modelEngine.runtimeState)"
            dot
          />
        </span>
      </template>
      <button type="button" class="tonal" data-testid="providers-routing-settings" @click="openRoutingSettings($event)">
        <AppIcon name="sliders" :size="15" />
        路由与默认模型
      </button>
      <button type="button" class="primary" data-testid="cap-add-provider" @click="openProvider(undefined, $event)">
        <AppIcon name="plus" :size="15" />
        新增模型供应商
      </button>
    </PageHeader>

    <UiBanner v-if="props.mode !== 'admin'" tone="info" test-id="providers-readonly">
      当前为标准只读模式，可浏览模型供应商配置与状态；如需新增、修改或测试连接，请切换到管理员模式。
      <template #actions>
        <button type="button" class="tonal" data-testid="providers-enable-admin" :disabled="!props.writesEnabled" @click="emit('enable-admin')">
          进入管理员模式
        </button>
      </template>
    </UiBanner>
    <UiBanner v-else-if="!props.writesEnabled" tone="bad" role="alert">
      契约不兼容，写入与连接测试入口已禁用。
    </UiBanner>

    <UiBanner v-if="configurationRecoveries.length > 0" tone="warn" title="存在未确认的配置提交" test-id="cap-configuration-recovery">
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
    <UiBanner v-if="recoveryNotice" tone="info" test-id="cap-configuration-recovery-notice">{{ recoveryNotice }}</UiBanner>
    <UiBanner v-if="ops.abortedNotice.value" tone="warn">{{ ops.abortedNotice.value }}</UiBanner>
    <UiBanner v-if="contextError" tone="bad" test-id="cap-context-error" role="alert">
      未能读取配置代际（{{ contextError.code }}）：当前可查看供应商列表，执行连接测试前需重新读取。
    </UiBanner>
    <ErrorNotice :error="error" title="读取模型供应商失败" />

    <CapabilityOperationPanel
      :active-record="ops.activeRecord.value"
      :waiting-records="ops.waitingRecords.value"
      :pending-action="ops.pendingAction.value"
      :action-trigger="ops.actionTrigger.value"
      :fallback-heading="pageHeading"
      :action-busy="ops.actionBusy.value"
      :action-error="ops.actionError.value"
      :dismiss-notice="ops.dismissNotice.value"
      :can-write="canWrite"
      :has-context="context !== null"
      :can-query="ops.canQuery"
      :can-retry="ops.canRetry"
      :unsettled="ops.unsettled"
      @confirm="ops.confirmAction()"
      @cancel-pending="ops.cancelPendingAction()"
      @query="ops.queryRecord($event)"
      @retry="ops.retryRecord($event)"
      @cancel-record="ops.cancelRecord($event)"
      @close-result="ops.closeResult()"
      @focus-record="ops.focusRecord($event)"
    />

    <div class="domain-toolbar">
      <UiTabs
        :model-value="statusFilter"
        :options="filterOptions"
        label="按启用状态筛选模型供应商"
        @update:model-value="statusFilter = $event as 'all' | 'enabled' | 'disabled'"
      />
      <label class="search-field">
        <AppIcon name="search" :size="15" class="search-icon" />
        <span class="sr-only">搜索模型供应商</span>
        <input v-model="search" type="search" placeholder="搜索名称、协议、模型或标识" data-testid="providers-search" />
      </label>
    </div>

    <UiEmptyState
      v-if="loading && providers.length === 0"
      icon="provider"
      title="正在读取模型供应商…"
      test-id="providers-loading"
    />
    <UiEmptyState
      v-else-if="!loading && providers.length === 0"
      icon="provider"
      title="尚未配置模型供应商"
      description="添加一个 OpenAI、Anthropic、Gemini 或兼容接口的模型供应商，即可为对话和 Agent 提供推理能力。"
      test-id="providers-empty"
    >
      <button type="button" class="primary" @click="openProvider(undefined, $event)">
        <AppIcon name="plus" :size="15" />
        新增模型供应商
      </button>
    </UiEmptyState>
    <UiEmptyState
      v-else-if="visibleProviders.length === 0"
      icon="search"
      title="没有匹配的模型供应商"
      description="尝试换个关键词或切换筛选状态。"
    >
      <button type="button" class="tonal" @click="search = ''; statusFilter = 'all'">重置筛选</button>
    </UiEmptyState>

    <div v-else class="cap-grid provider-grid" data-testid="providers-grid">
      <article
        v-for="item in visibleProviders"
        :key="item.key"
        class="cap-card provider-card"
        :data-testid="'cap-item-' + keySlug(item.descriptor.key)"
      >
        <header class="cap-card-head">
          <span class="cap-avatar" data-domain="provider" aria-hidden="true">
            <AppIcon :name="item.icon" :size="19" />
          </span>
          <div class="cap-title">
            <div class="title-row">
              <h2 class="cap-name">{{ item.title }}</h2>
              <span v-if="item.configView" class="domain-pill" data-domain="provider">
                {{ protocolLabel(item.configView.protocol) }}
              </span>
            </div>
            <p class="cap-key"><BreakableId :value="item.key" /></p>
          </div>
          <StatusBadge
            :tone="runtimeTone(item.descriptor.runtimeState)"
            :label="runtimeLabel(item.descriptor.runtimeState)"
            dot
            data-testid="cap-state"
          />
        </header>

        <dl class="provider-facts">
          <div>
            <dt>默认模型</dt>
            <dd class="mono">{{ item.configView?.modelName.display || '—' }}</dd>
          </div>
          <div>
            <dt>优先级</dt>
            <dd class="mono">{{ item.configView?.priority ?? '—' }}</dd>
          </div>
          <div>
            <dt>凭据</dt>
            <dd>{{ credentialText(item) }}</dd>
          </div>
          <div>
            <dt>配置状态</dt>
            <dd>
              {{ (item.configView?.desiredEnabled ?? item.descriptor.desiredEnabled) ? '已启用' : '已停用' }}
              <StatusBadge v-if="item.configView?.runtimeApplyState === 3" tone="warn" label="需重启" />
            </dd>
          </div>
        </dl>

        <p v-if="item.descriptor.errorCode" class="inline-notice bad">
          异常代码：<code>{{ item.descriptor.errorCode }}</code>
        </p>

        <footer class="cap-card-foot">
          <button
            type="button"
            class="tonal"
            :data-testid="'cap-provider-edit-' + keySlug(item.descriptor.key)"
            @click="openProvider(item.key, $event)"
          >
            <AppIcon name="settings" :size="15" />
            编辑配置
          </button>
          <button
            v-if="testActionFor(item)"
            type="button"
            class="tonal"
            :disabled="!canWrite"
            :data-testid="'cap-action-' + keySlug(item.descriptor.key) + '-5'"
            @click="ops.requestAction(item.descriptor, testActionFor(item)!, $event)"
          >
            <AppIcon name="bolt" :size="15" />
            测试连接
          </button>
          <button
            type="button"
            class="ghost"
            :data-testid="'cap-inspect-' + keySlug(item.descriptor.key)"
            @click="openDetail(item, $event)"
          >
            详情
          </button>
        </footer>
      </article>
    </div>

    <ManagementDialog
      v-if="detailTarget"
      :title="detailTarget.title"
      title-id="provider-detail-title"
      test-id="provider-detail-drawer"
      :trigger="detailTrigger"
      :fallback="pageHeading"
      drawer
      @close="closeDetail"
    >
      <template #eyebrow><span class="eyebrow">MODEL PROVIDER DETAIL</span></template>
      <dl class="detail-kv">
        <div><dt>供应商标识</dt><dd class="mono"><BreakableId :value="detailTarget.key" copyable /></dd></div>
        <div><dt>运行状态</dt><dd><StatusBadge :tone="runtimeTone(detailTarget.descriptor.runtimeState)" :label="runtimeLabel(detailTarget.descriptor.runtimeState)" dot /></dd></div>
        <div v-if="detailTarget.configView"><dt>接入协议</dt><dd>{{ protocolLabel(detailTarget.configView.protocol) }}</dd></div>
        <div v-if="detailTarget.configView"><dt>API 地址</dt><dd class="mono">{{ detailTarget.configView.baseUrl.display || '—' }}</dd></div>
        <div v-if="detailTarget.configView"><dt>模型名称</dt><dd class="mono">{{ detailTarget.configView.modelName.display || '—' }}</dd></div>
        <div v-if="detailTarget.configView"><dt>优先级</dt><dd class="mono">{{ detailTarget.configView.priority }}</dd></div>
        <div><dt>凭据状态</dt><dd>{{ credentialText(detailTarget) }}</dd></div>
        <div v-if="detailTarget.descriptor.errorCode"><dt>错误代码</dt><dd class="cap-error-code mono">{{ detailTarget.descriptor.errorCode }}</dd></div>
      </dl>
      <template #footer>
        <button type="button" class="tonal" @click="const k = detailTarget.key; closeDetail(); openProvider(k)">编辑配置</button>
        <button type="button" @click="closeDetail">关闭</button>
      </template>
    </ManagementDialog>

    <ProviderEditorDialog
      v-if="providerTarget"
      ref="providerDialog"
      :client="props.client"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :active="props.active"
      :provider-key="providerTarget.key"
      :trigger="providerTarget.trigger"
      :fallback="pageHeading"
      @close="closeProvider"
      @saved="load('background')"
      @test="testSavedProvider"
      @enable-admin="emit('enable-admin')"
      @auth-expired="emit('auth-expired', $event)"
    />

    <CapabilitySettingsHost
      v-if="routingTarget"
      ref="routingDialog"
      :client="props.client"
      :capability="routingTarget.capability"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :features="props.features"
      :active="props.active"
      :trigger="routingTarget.trigger"
      :fallback="pageHeading"
      @close="closeRoutingSettings"
      @enable-admin="emit('enable-admin')"
      @auth-expired="emit('auth-expired', $event)"
    />
  </section>
</template>

<style scoped>
.engine-pill {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: 12px;
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--surface-muted);
  border: 1px solid var(--border-subtle);
  font-size: var(--text-xs);
  color: var(--text-muted);
}
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
.title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.provider-facts {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 16px;
  margin: 0;
  padding: 12px 14px;
  border-radius: var(--radius);
  background: var(--surface-muted);
}
.provider-facts dt {
  font-size: var(--text-xs);
  color: var(--text-soft);
}
.provider-facts dd {
  margin: 3px 0 0;
  font-size: var(--text-sm);
  font-weight: 500;
  overflow-wrap: anywhere;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.recovery-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin: 6px 0 0;
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
</style>
