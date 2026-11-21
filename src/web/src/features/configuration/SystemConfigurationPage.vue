<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaInstalledPlugins, type ZbaManagedDescriptor } from '../../api/zba'
import AppIcon, { type AppIconName } from '../../shared/ui/AppIcon.vue'
import BreakableId from '../../shared/ui/BreakableId.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import PageHeader from '../../shared/ui/PageHeader.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { buildDomainCatalog, runtimeLabel, runtimeTone } from '../capabilities/domain-catalog'
import ConfigurationPage from './ConfigurationPage.vue'

type SystemTab = 'basic' | 'conversation' | 'agent' | 'reporting' | 'system-packages'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
}>(), { active: true })

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
  'open-domain': [target: 'providers' | 'platforms' | 'plugins', ownerId?: string]
}>()

const tabs: ReadonlyArray<{ id: SystemTab; label: string; hint: string; icon: AppIconName }> = [
  { id: 'basic', label: '基础行为与人设', hint: 'AI 开关、人设、插话与上下文', icon: 'sliders' },
  { id: 'conversation', label: '对话编排', hint: '多轮编排与全局对话参数', icon: 'config' },
  { id: 'agent', label: '内置 Agent 与工具', hint: '附加提示词、工具与 MCP 来源', icon: 'tasks' },
  { id: 'reporting', label: '健康日报', hint: '每日运行摘要与定时推送', icon: 'traces' },
  { id: 'system-packages', label: '高级与系统包', hint: '核心引擎包版本与状态', icon: 'cpu' }
]

const currentTab = ref<SystemTab>('basic')
const mountedTabs = ref<Record<Exclude<SystemTab, 'system-packages'>, boolean>>({
  basic: true,
  conversation: false,
  agent: false,
  reporting: false
})

const basicRef = ref<InstanceType<typeof ConfigurationPage> | null>(null)
const conversationRef = ref<InstanceType<typeof ConfigurationPage> | null>(null)
const agentRef = ref<InstanceType<typeof ConfigurationPage> | null>(null)
const reportingRef = ref<InstanceType<typeof ConfigurationPage> | null>(null)

const descriptors = ref<ZbaManagedDescriptor[]>([])
const installedSnapshot = ref<ZbaInstalledPlugins | null>(null)
const packagesLoading = ref(false)
const packagesError = ref<UiError | null>(null)
let pkgSeq = 0
let unmounted = false

const systemPackages = computed(() =>
  buildDomainCatalog(descriptors.value, null, installedSnapshot.value).systemPackages
)

function editorFor(tab: SystemTab): InstanceType<typeof ConfigurationPage> | null {
  if (tab === 'basic') return basicRef.value
  if (tab === 'conversation') return conversationRef.value
  if (tab === 'agent') return agentRef.value
  if (tab === 'reporting') return reportingRef.value
  return null
}

function allEditors(): Array<InstanceType<typeof ConfigurationPage>> {
  return [basicRef.value, conversationRef.value, agentRef.value, reportingRef.value]
    .filter((item): item is InstanceType<typeof ConfigurationPage> => item !== null)
}

const tabDraftCounts = computed<Record<SystemTab, number>>(() => ({
  basic: basicRef.value?.draftCount ?? 0,
  conversation: conversationRef.value?.draftCount ?? 0,
  agent: agentRef.value?.draftCount ?? 0,
  reporting: reportingRef.value?.draftCount ?? 0,
  'system-packages': 0
}))

const draftCount = computed(() =>
  tabDraftCounts.value.basic
  + tabDraftCounts.value.conversation
  + tabDraftCounts.value.agent
  + tabDraftCounts.value.reporting
)

async function selectTab(next: SystemTab, event?: MouseEvent): Promise<void> {
  if (currentTab.value === next) return
  const trigger = event?.currentTarget instanceof HTMLElement ? event.currentTarget : null
  const activeEditor = editorFor(currentTab.value)
  if (activeEditor && !(await activeEditor.requestLeave('navigate', trigger))) return
  activeEditor?.discardSecretsOnLeave()
  currentTab.value = next
  if (next !== 'system-packages') {
    const first = !mountedTabs.value[next]
    mountedTabs.value[next] = true
    if (!first) await editorFor(next)?.refreshOnEnter()
  } else if (descriptors.value.length === 0) {
    void loadSystemPackages()
  }
}

async function loadSystemPackages(): Promise<void> {
  const seq = ++pkgSeq
  const client = props.client
  packagesLoading.value = true
  packagesError.value = null
  try {
    const [capRes, instRes] = await Promise.allSettled([
      client.listCapabilities({ limit: 100 }),
      typeof client.installedPlugins === 'function' ? client.installedPlugins() : Promise.resolve(null)
    ])
    if (unmounted || seq !== pkgSeq || client !== props.client) return
    if (capRes.status === 'fulfilled') descriptors.value = capRes.value.items
    else if (capRes.reason instanceof ZbaRequestError && capRes.reason.status === 401) {
      emit('auth-expired', toUiError(capRes.reason))
      return
    } else {
      packagesError.value = toUiError(capRes.reason)
    }
    if (instRes.status === 'fulfilled' && instRes.value) installedSnapshot.value = instRes.value
  } finally {
    if (!unmounted && seq === pkgSeq) packagesLoading.value = false
  }
}

function domainLabel(domain: 'provider' | 'platform' | 'system'): string {
  if (domain === 'provider') return '模型供应商'
  if (domain === 'platform') return '消息平台'
  return '系统核心'
}

function jumpFromSystemPackage(pkg: { domain: 'provider' | 'platform' | 'system'; configScope?: string }): void {
  if (pkg.domain === 'provider') { emit('open-domain', 'providers'); return }
  if (pkg.domain === 'platform') { emit('open-domain', 'platforms'); return }
  if (pkg.configScope === 'conversation' || pkg.configScope === 'agent' || pkg.configScope === 'reporting') {
    void selectTab(pkg.configScope)
  }
}

async function reload(): Promise<void> {
  if (currentTab.value === 'system-packages') {
    await loadSystemPackages()
    return
  }
  await editorFor(currentTab.value)?.reload()
}

async function refreshOnEnter(): Promise<void> {
  if (currentTab.value === 'system-packages') {
    await loadSystemPackages()
    return
  }
  await editorFor(currentTab.value)?.refreshOnEnter()
}

async function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') {
    discardSecretsOnLeave()
    return true
  }
  for (const editor of allEditors()) {
    if (!(await editor.requestLeave(reason, trigger))) return false
  }
  discardSecretsOnLeave()
  return true
}

function discardSecretsOnLeave(): void {
  for (const editor of allEditors()) editor.discardSecretsOnLeave()
}

function endSession(): void {
  for (const editor of allEditors()) editor.endSession()
  currentTab.value = 'basic'
  mountedTabs.value = { basic: true, conversation: false, agent: false, reporting: false }
  descriptors.value = []
  installedSnapshot.value = null
}

watch(() => props.client, () => {
  descriptors.value = []
  installedSnapshot.value = null
  if (currentTab.value === 'system-packages') void loadSystemPackages()
})

onMounted(() => {
  if (currentTab.value === 'system-packages') void loadSystemPackages()
})

onBeforeUnmount(() => { unmounted = true })

defineExpose({
  reload,
  refreshOnEnter,
  requestLeave,
  discardSecretsOnLeave,
  endSession,
  draftCount
})
</script>

<template>
  <section class="domain-page sysconfig-page" data-testid="system-configuration">
    <PageHeader
      eyebrow="SYSTEM CONFIGURATION"
      title="系统配置"
      title-id="sysconfig-title"
      description="统一管理机器人基础行为与人设、对话编排、内置 Agent 工具、健康日报与核心系统包状态。"
    >
      <template #actions>
        <span class="mode-badge" :data-mode="props.mode" data-testid="config-mode-badge">
          {{ props.mode === 'admin' ? '管理员模式' : '标准模式' }}
        </span>
      </template>
    </PageHeader>

    <div class="sysconfig-layout">
      <nav class="sysconfig-nav" aria-label="系统配置分区">
        <button
          v-for="tab in tabs"
          :key="tab.id"
          type="button"
          class="sysconfig-nav-item"
          :class="{ active: currentTab === tab.id }"
          :aria-current="currentTab === tab.id ? 'page' : undefined"
          :data-testid="'sysconfig-tab-' + tab.id"
          @click="selectTab(tab.id, $event)"
        >
          <AppIcon :name="tab.icon" :size="16" />
          <span class="sysconfig-nav-copy">
            <strong>{{ tab.label }}</strong>
            <small>{{ tab.hint }}</small>
          </span>
          <span v-if="tabDraftCounts[tab.id] > 0" class="nav-badge">{{ tabDraftCounts[tab.id] }}</span>
        </button>
      </nav>

      <div class="sysconfig-content">
        <div v-if="mountedTabs.basic" v-show="currentTab === 'basic'" data-testid="sysconfig-pane-basic">
          <ConfigurationPage
            ref="basicRef"
            scope="basic"
            :embedded="true"
            :active="props.active && currentTab === 'basic'"
            :client="props.client"
            :mode="props.mode"
            :writes-enabled="props.writesEnabled"
            @enable-admin="emit('enable-admin')"
            @auth-expired="emit('auth-expired', $event)"
            @open-capabilities="owner => emit('open-domain', owner === 'models' ? 'providers' : owner === 'platform' ? 'platforms' : 'plugins', owner)"
          />
        </div>
        <div v-if="mountedTabs.conversation" v-show="currentTab === 'conversation'" data-testid="sysconfig-pane-conversation">
          <ConfigurationPage
            ref="conversationRef"
            scope="conversation"
            :embedded="true"
            :active="props.active && currentTab === 'conversation'"
            :client="props.client"
            :mode="props.mode"
            :writes-enabled="props.writesEnabled"
            @enable-admin="emit('enable-admin')"
            @auth-expired="emit('auth-expired', $event)"
          />
        </div>
        <div v-if="mountedTabs.agent" v-show="currentTab === 'agent'" data-testid="sysconfig-pane-agent">
          <ConfigurationPage
            ref="agentRef"
            scope="agent"
            :embedded="true"
            :active="props.active && currentTab === 'agent'"
            :client="props.client"
            :mode="props.mode"
            :writes-enabled="props.writesEnabled"
            @enable-admin="emit('enable-admin')"
            @auth-expired="emit('auth-expired', $event)"
          />
        </div>
        <div v-if="mountedTabs.reporting" v-show="currentTab === 'reporting'" data-testid="sysconfig-pane-reporting">
          <ConfigurationPage
            ref="reportingRef"
            scope="reporting"
            :embedded="true"
            :active="props.active && currentTab === 'reporting'"
            :client="props.client"
            :mode="props.mode"
            :writes-enabled="props.writesEnabled"
            @enable-admin="emit('enable-admin')"
            @auth-expired="emit('auth-expired', $event)"
          />
        </div>

        <section
          v-if="currentTab === 'system-packages'"
          class="panel syspkg-panel"
          data-testid="system-packages-panel"
          aria-labelledby="syspkg-heading"
        >
          <div class="panel-heading">
            <div>
              <h2 id="syspkg-heading">核心系统包（只读状态）</h2>
              <p>展示底层已解耦的 <code>system.*</code> 引擎包版本与运行状态；日常配置请直接在对应功能页操作。</p>
            </div>
            <button type="button" class="tonal" :disabled="packagesLoading" @click="loadSystemPackages">
              <AppIcon name="refresh" :size="14" />
              {{ packagesLoading ? '读取中…' : '刷新' }}
            </button>
          </div>

          <ErrorNotice :error="packagesError" title="读取系统包状态失败" />
          <p v-if="packagesLoading && systemPackages.length === 0" class="empty-state">正在读取核心系统包…</p>
          <p v-else-if="systemPackages.length === 0" class="empty-state">未发现系统核心包记录。</p>
          <div v-else class="table-scroll">
            <table>
              <thead>
                <tr>
                  <th scope="col">系统模块</th>
                  <th scope="col">归属领域</th>
                  <th scope="col">版本</th>
                  <th scope="col">重载方式</th>
                  <th scope="col">状态</th>
                  <th scope="col">入口</th>
                </tr>
              </thead>
              <tbody>
                <tr v-for="pkg in systemPackages" :key="pkg.id" :data-testid="'syspkg-row-' + pkg.id">
                  <td>
                    <strong>{{ pkg.title }}</strong>
                    <span class="cell-sub mono"><BreakableId :value="pkg.id" /></span>
                    <span class="cell-sub">{{ pkg.summary }}</span>
                  </td>
                  <td>
                    <span class="domain-pill" :data-domain="pkg.domain">{{ domainLabel(pkg.domain) }}</span>
                  </td>
                  <td class="mono">{{ pkg.version ? 'v' + pkg.version : '—' }}</td>
                  <td>{{ pkg.adoption === 'cold' ? '冷重载' : '热应用' }}</td>
                  <td>
                    <StatusBadge
                      :tone="pkg.restartRequired ? 'warn' : runtimeTone(pkg.runtimeState)"
                      :label="pkg.restartRequired ? '需重启' : runtimeLabel(pkg.runtimeState)"
                      dot
                    />
                  </td>
                  <td>
                    <button type="button" class="link-button" :data-testid="'syspkg-jump-' + pkg.id" @click="jumpFromSystemPackage(pkg)">
                      前往管理
                    </button>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>
      </div>
    </div>
  </section>
</template>

<style scoped>
.sysconfig-layout {
  display: grid;
  grid-template-columns: 224px minmax(0, 1fr);
  gap: 20px;
  align-items: start;
}
.sysconfig-nav {
  position: sticky;
  top: 76px;
  display: grid;
  gap: 6px;
}
.sysconfig-nav-item {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  width: 100%;
  padding: 10px 12px;
  border: 1px solid transparent;
  border-radius: var(--radius);
  background: transparent;
  color: var(--text-muted);
  text-align: left;
}
.sysconfig-nav-item:hover:not(:disabled) {
  background: var(--surface);
  border-color: var(--border);
  color: var(--text);
}
.sysconfig-nav-item.active {
  background: var(--accent-soft);
  border-color: var(--accent-strong);
  color: var(--accent-deep);
}
.sysconfig-nav-item.active .sysconfig-nav-copy small {
  color: var(--accent-deep);
}
.sysconfig-nav-copy {
  display: grid;
  gap: 2px;
  min-width: 0;
  flex: 1;
}
.sysconfig-nav-copy strong {
  font-size: var(--text-sm);
  font-weight: 600;
}
.sysconfig-nav-copy small {
  font-size: var(--text-xs);
  color: var(--text-soft);
  font-weight: 400;
  line-height: 1.4;
}
.sysconfig-content {
  min-width: 0;
}
@media (max-width: 960px) {
  .sysconfig-layout {
    grid-template-columns: 1fr;
  }
  .sysconfig-nav {
    position: static;
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .sysconfig-nav-item {
    width: auto;
    flex: 1 1 160px;
  }
  .sysconfig-nav-copy small {
    display: none;
  }
}
</style>
