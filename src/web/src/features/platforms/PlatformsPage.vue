<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import {
  ZbaClient,
  ZbaRequestError,
  type ZbaConfigurationReadResult,
  type ZbaInstalledPlugins,
  type ZbaManagedDescriptor,
  type ZbaManagedObjectKey
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
import { restoreFocus } from '../../shared/ui/focus-restore'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import CapabilityOperationPanel from '../capabilities/CapabilityOperationPanel.vue'
import CapabilitySettingsHost from '../capabilities/CapabilitySettingsHost.vue'
import OfficialMessagingDialog from '../capabilities/OfficialMessagingDialog.vue'
import { configurationFor, type CapabilityConfiguration } from '../capabilities/configuration-ownership'
import {
  buildDomainCatalog,
  keyIdentity,
  keySlug,
  runtimeLabel,
  runtimeTone,
  type PlatformChannelModel
} from '../capabilities/domain-catalog'
import { reasonLabel } from '../capabilities/reason-labels'
import {
  actionLabels,
  isDangerousAction,
  supportedActions,
  unsupportedActions,
  useCapabilityOperations
} from '../capabilities/use-capability-operations'

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

const settingsTarget = ref<{ capability: CapabilityConfiguration; trigger: HTMLElement | null } | null>(null)
const settingsDialog = ref<InstanceType<typeof CapabilitySettingsHost> | null>(null)
const messagingTarget = ref<ZbaManagedObjectKey | null>(null)
const messagingTrigger = ref<HTMLElement | null>(null)
const detailTarget = ref<PlatformChannelModel | null>(null)
const detailTrigger = ref<HTMLElement | null>(null)

const configurationRecoveries = computed(() =>
  pendingOperations(props.client, 'configuration')
    .filter(item => item.state !== 'settled' && item.target === 'platform')
)
const recoveryChecking = ref<string | null>(null)
const recoveryNotice = ref('')

let loadSequence = 0
let unmounted = false

const catalog = computed(() => buildDomainCatalog(descriptors.value, configSnapshot.value, installedSnapshot.value))
const platforms = computed(() => catalog.value.platforms)
const runningCount = computed(() => platforms.value.filter(item => item.descriptor.runtimeState === 4).length)

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

function eventTrigger(event?: Event): HTMLElement | null {
  return event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
}

function policySummary(channel: PlatformChannelModel): string {
  const policy = channel.policy
  if (!policy) return '跟随全局默认规则'
  if (policy.enabled.value === 2) return '策略已停用'
  if (policy.chatEnabled.value === 2) return '已启用 · 静默（不回复聊天）'
  return '已启用 · 按名单与动作规则响应'
}

function actionHint(channel: PlatformChannelModel): string | null {
  if (supportedActions(channel.descriptor).length > 0) return null
  const reasons = [...new Set(unsupportedActions(channel.descriptor).map(cap => reasonLabel(cap.reasonCode)))]
  return reasons.length > 0 ? reasons.join('；') : null
}

function openPlatformSettings(event?: Event): void {
  if (settingsTarget.value) return
  const capability = configurationFor('platform')
  if (!capability) return
  settingsTarget.value = { capability, trigger: eventTrigger(event) }
}

function closePlatformSettings(): void {
  const trigger = settingsTarget.value?.trigger ?? null
  settingsTarget.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
  void load('background')
}

function openMessaging(channel: PlatformChannelModel, event?: Event): void {
  messagingTrigger.value = eventTrigger(event)
  messagingTarget.value = channel.descriptor.key
}

function closeMessaging(): void {
  const trigger = messagingTrigger.value
  messagingTarget.value = null
  messagingTrigger.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
}

function openDetail(channel: PlatformChannelModel, event?: Event): void {
  detailTrigger.value = eventTrigger(event)
  detailTarget.value = channel
}

function closeDetail(): void {
  const trigger = detailTrigger.value
  detailTarget.value = null
  detailTrigger.value = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
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
      ? '原提交已有确定回执，平台配置已刷新。'
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
      if (cfgRes.value.code === 0 && cfgRes.value.generation && cfgRes.value.contractRevision) {
        context.value = { generation: cfgRes.value.generation, contractRevision: cfgRes.value.contractRevision }
        contextError.value = null
      } else {
        context.value = null
        contextError.value = { code: 'configuration_context_missing', message: '未获取到配置代际，控制动作暂不可提交。' }
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
  settingsDialog.value?.discardSecretsOnLeave()
}

async function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') { discardSecretsOnLeave(); return true }
  if (settingsDialog.value && !(await settingsDialog.value.requestLeave(reason, trigger))) return false
  discardSecretsOnLeave()
  return true
}

watch(() => props.client, (client) => {
  settingsTarget.value = null
  messagingTarget.value = null
  detailTarget.value = null
  attachSync(client)
  void load('interactive')
})

watch(() => props.active, active => {
  if (!active) messagingTarget.value = null
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
  openPlatformSettings: () => openPlatformSettings(),
  requestLeave,
  discardSecretsOnLeave,
  draftCount: computed(() => settingsDialog.value?.draftCount ?? 0)
})
</script>

<template>
  <section class="domain-page platforms-page" data-testid="platforms-page">
    <PageHeader
      ref="headerRef"
      eyebrow="PLATFORM DRIVERS"
      title="消息平台"
      title-id="platforms-title"
      description="管理 QQ 私域、QQ 官方开放平台、飞书与本地调试等平台驱动的连接启停、接入凭据与访问策略。"
    >
      <template #metadata>
        <span>共 <strong>{{ platforms.length }}</strong> 条通道 · <strong>{{ runningCount }}</strong> 条运行中</span>
      </template>
      <button type="button" class="primary" data-testid="platforms-open-settings" @click="openPlatformSettings($event)">
        <AppIcon name="sliders" :size="15" />
        平台接入与访问策略
      </button>
    </PageHeader>

    <UiBanner v-if="props.mode !== 'admin'" tone="info" test-id="platforms-readonly">
      当前为标准只读模式，可查看通道运行态与消息能力；执行启停、连接测试或修改白名单策略需进入管理员模式。
      <template #actions>
        <button type="button" class="tonal" data-testid="platforms-enable-admin" :disabled="!props.writesEnabled" @click="emit('enable-admin')">
          进入管理员模式
        </button>
      </template>
    </UiBanner>
    <UiBanner v-else-if="!props.writesEnabled" tone="bad" role="alert">
      契约不兼容，平台驱动控制与策略编辑已禁用。
    </UiBanner>

    <UiBanner v-if="configurationRecoveries.length > 0" tone="warn" title="存在未确认的平台配置提交">
      <p v-for="record in configurationRecoveries" :key="record.key" class="recovery-row">
        <BreakableId :value="record.key" copyable />
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
    <UiBanner v-if="ops.abortedNotice.value" tone="warn">{{ ops.abortedNotice.value }}</UiBanner>
    <UiBanner v-if="contextError" tone="bad" test-id="cap-context-error" role="alert">
      未能读取配置代际（{{ contextError.code }}）：当前可以查看平台状态，但执行启停或测试前需重新读取。
    </UiBanner>
    <ErrorNotice :error="error" title="读取消息平台失败" />

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

    <UiEmptyState
      v-if="loading && platforms.length === 0"
      icon="platform"
      title="正在读取消息平台驱动…"
      test-id="platforms-loading"
    />
    <UiEmptyState
      v-else-if="!loading && platforms.length === 0"
      icon="platform"
      title="尚未声明消息平台驱动"
      description="可以在「平台接入与访问策略」中配置 QQ 私域、QQ 官方或飞书通道。"
      test-id="platforms-empty"
    >
      <button type="button" class="primary" @click="openPlatformSettings($event)">打开平台接入设置</button>
    </UiEmptyState>

    <div v-else class="platform-grid" data-testid="platforms-grid">
      <article
        v-for="channel in platforms"
        :key="channel.identity"
        class="cap-card platform-card"
        :data-testid="'cap-item-' + keySlug(channel.descriptor.key)"
      >
        <header class="cap-card-head">
          <span class="cap-avatar" data-domain="platform" aria-hidden="true">
            <AppIcon :name="channel.icon" :size="20" />
          </span>
          <div class="cap-title">
            <div class="title-row">
              <h2 class="cap-name">{{ channel.title }}</h2>
              <span class="domain-pill" data-domain="platform">平台驱动</span>
              <code v-if="channel.driverPackage?.version" class="pkg-ver">v{{ channel.driverPackage.version }}</code>
            </div>
            <p class="platform-summary">{{ channel.summary }}</p>
          </div>
          <StatusBadge
            :tone="runtimeTone(channel.descriptor.runtimeState)"
            :label="runtimeLabel(channel.descriptor.runtimeState)"
            dot
            data-testid="cap-state"
          />
        </header>

        <dl class="platform-facts">
          <div>
            <dt>开关配置</dt>
            <dd>{{ channel.descriptor.desiredEnabled ? '已启用' : '已停用' }}</dd>
          </div>
          <div>
            <dt>接入凭据</dt>
            <dd>{{ channel.descriptor.credentialConfigured ? '已配置' : '未配置' }}</dd>
          </div>
          <div>
            <dt>访问策略</dt>
            <dd>{{ policySummary(channel) }}</dd>
          </div>
          <div>
            <dt>通道实例</dt>
            <dd class="mono">{{ channel.accountScope }} / {{ channel.instanceId }}</dd>
          </div>
        </dl>

        <p v-if="channel.descriptor.errorCode" class="inline-notice bad">
          驱动异常：<code>{{ channel.descriptor.errorCode }}</code>
        </p>
        <p v-else-if="channel.driverPackage?.restartRequired" class="inline-notice warn">
          驱动包变更等待重启宿主后生效。
        </p>
        <p v-else-if="actionHint(channel)" class="platform-hint">
          <AppIcon name="info" :size="14" />
          <span>{{ actionHint(channel) }}</span>
        </p>

        <footer class="cap-card-foot">
          <button
            v-for="cap in supportedActions(channel.descriptor)"
            :key="cap.action"
            type="button"
            class="tonal"
            :class="{ danger: isDangerousAction(cap.action) }"
            :disabled="!canWrite"
            :data-testid="'cap-action-' + keySlug(channel.descriptor.key) + '-' + cap.action"
            @click="ops.requestAction(channel.descriptor, cap, $event)"
          >
            <AppIcon :name="cap.action === 3 ? 'play' : cap.action === 4 ? 'stop' : 'bolt'" :size="14" />
            {{ actionLabels[cap.action] ?? `动作 #${cap.action}` }}
          </button>

          <button
            type="button"
            class="tonal"
            :data-testid="'platform-policy-' + channel.platformId"
            @click="openPlatformSettings($event)"
          >
            <AppIcon name="shield" :size="14" />
            接入与策略
          </button>

          <button
            v-if="channel.platformId === 'qq.official'"
            type="button"
            class="tonal"
            :data-testid="'cap-messaging-' + keySlug(channel.descriptor.key)"
            @click="openMessaging(channel, $event)"
          >
            消息能力与限制
          </button>

          <button
            type="button"
            class="ghost"
            :data-testid="'cap-inspect-' + keySlug(channel.descriptor.key)"
            @click="openDetail(channel, $event)"
          >
            详情
          </button>
        </footer>
      </article>
    </div>

    <ManagementDialog
      v-if="detailTarget"
      :title="detailTarget.title"
      title-id="platform-detail-title"
      test-id="platform-detail-drawer"
      :trigger="detailTrigger"
      :fallback="pageHeading"
      drawer
      @close="closeDetail"
    >
      <template #eyebrow><span class="eyebrow">PLATFORM DRIVER DETAIL</span></template>
      <dl class="detail-kv">
        <div><dt>平台标识</dt><dd class="mono"><BreakableId :value="detailTarget.platformId" copyable /></dd></div>
        <div><dt>实例范围</dt><dd class="mono">{{ detailTarget.accountScope }} / {{ detailTarget.instanceId }}</dd></div>
        <div><dt>运行状态</dt><dd><StatusBadge :tone="runtimeTone(detailTarget.descriptor.runtimeState)" :label="runtimeLabel(detailTarget.descriptor.runtimeState)" dot /></dd></div>
        <div><dt>凭据状态</dt><dd>{{ detailTarget.descriptor.credentialConfigured ? '已配置' : '未配置' }}</dd></div>
        <div v-if="detailTarget.driverPackage">
          <dt>底层驱动包</dt>
          <dd class="mono">
            <BreakableId :value="detailTarget.driverPackage.id" />
            <template v-if="detailTarget.driverPackage.version"> · v{{ detailTarget.driverPackage.version }}</template>
            · {{ detailTarget.driverPackage.adoption === 'cold' ? '冷重载（变更需重启）' : '支持热应用' }}
          </dd>
        </div>
        <div>
          <dt>可执行动作</dt>
          <dd>{{ supportedActions(detailTarget.descriptor).map(a => actionLabels[a.action]).join('、') || '暂无（请先完成配置）' }}</dd>
        </div>
        <div v-if="detailTarget.descriptor.errorCode">
          <dt>异常代码</dt>
          <dd class="cap-error-code mono">{{ detailTarget.descriptor.errorCode }}</dd>
        </div>
      </dl>
      <template #footer>
        <button type="button" class="tonal" @click="closeDetail(); openPlatformSettings()">编辑接入与策略</button>
        <button type="button" @click="closeDetail">关闭</button>
      </template>
    </ManagementDialog>

    <OfficialMessagingDialog
      v-if="messagingTarget"
      :client="props.client"
      :target="messagingTarget"
      :active="props.active"
      :trigger="messagingTrigger"
      :fallback="pageHeading"
      @close="closeMessaging"
      @auth-expired="emit('auth-expired', $event)"
    />

    <CapabilitySettingsHost
      v-if="settingsTarget"
      ref="settingsDialog"
      :client="props.client"
      :capability="settingsTarget.capability"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :features="props.features"
      :active="props.active"
      :trigger="settingsTarget.trigger"
      :fallback="pageHeading"
      @close="closePlatformSettings"
      @enable-admin="emit('enable-admin')"
      @auth-expired="emit('auth-expired', $event)"
    />
  </section>
</template>

<style scoped>
.platform-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(min(440px, 100%), 1fr));
  gap: 16px;
}
.title-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.pkg-ver {
  font-size: var(--text-xs);
  color: var(--text-soft);
}
.platform-summary {
  margin: 4px 0 0;
  font-size: var(--text-sm);
  color: var(--text-muted);
  line-height: 1.5;
}
.platform-facts {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 10px 16px;
  margin: 0;
  padding: 12px 14px;
  border-radius: var(--radius);
  background: var(--surface-muted);
}
.platform-facts dt {
  font-size: var(--text-xs);
  color: var(--text-soft);
}
.platform-facts dd {
  margin: 3px 0 0;
  font-size: var(--text-sm);
  font-weight: 500;
  overflow-wrap: anywhere;
}
.platform-hint {
  margin: 0;
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: var(--text-xs);
  color: var(--text-soft);
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
@media (max-width: 640px) {
  .platform-facts {
    grid-template-columns: 1fr;
  }
}
</style>
