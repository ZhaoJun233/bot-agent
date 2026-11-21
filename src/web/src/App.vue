<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import AuthGate from './features/auth/AuthGate.vue'
import OverviewPage from './features/overview/OverviewPage.vue'
import ProvidersPage from './features/providers/ProvidersPage.vue'
import PlatformsPage from './features/platforms/PlatformsPage.vue'
import PluginsPage from './features/plugins/PluginsPage.vue'
import SystemConfigurationPage from './features/configuration/SystemConfigurationPage.vue'
import TasksPage from './features/tasks/TasksPage.vue'
import TracesPage from './features/traces/TracesPage.vue'
import RuntimeLogsPage from './features/logs/RuntimeLogsPage.vue'
import AppIcon, { type AppIconName } from './shared/ui/AppIcon.vue'
import BrandMark from './shared/ui/BrandMark.vue'
import ThemeToggle from './shared/ui/ThemeToggle.vue'
import { evaluateCompatibility } from './api/compat'
import {
  ZbaClient,
  ZbaRequestError,
  type ZbaHealth,
  type ZbaMeta,
  type ZbaStatus
} from './api/zba'
import { toUiError, type UiError } from './shared/ui/ui-error'
import { acquireScheduler } from './shared/sync/event-scheduler'
import { registerAppResources, resetAppResourceSignals } from './shared/sync/app-resources'

export type AppView =
  | 'overview'
  | 'providers'
  | 'platforms'
  | 'plugins'
  | 'configuration'
  | 'tasks'
  | 'traces'
  | 'logs'

const token = ref('')
const mode = ref<'read' | 'admin'>('read')
const health = ref<ZbaHealth | null>(null)
const meta = ref<ZbaMeta | null>(null)
const status = ref<ZbaStatus | null>(null)
const error = ref<UiError | null>(null)
const authError = ref<UiError | null>(null)
const adminError = ref<UiError | null>(null)
const loading = ref(false)
const subviewBusy = ref(false)
const connecting = ref(false)
const adminPending = ref(false)
const lastSuccessAt = ref<string | null>(null)
const view = ref<AppView>('overview')

const RAIL_KEY = 'botagent.sidebar.rail'
function readRail(): boolean {
  try { return globalThis.localStorage?.getItem(RAIL_KEY) === '1' } catch { return false }
}
const rail = ref(readRail())
function toggleRail() {
  try {
    const storage = globalThis.localStorage
    if (!storage) {
      rail.value = false
      return
    }
    const nextRail = !rail.value
    storage.setItem(RAIL_KEY, nextRail ? '1' : '0')
    rail.value = nextRail
  } catch {
    rail.value = false
  }
}

const helpDetails = ref<HTMLDetailsElement | null>(null)
function closeHelpIfOutside(event: MouseEvent): void {
  const el = helpDetails.value
  if (!el || !el.open) return
  if (event.target instanceof Node && !el.contains(event.target)) el.open = false
}
function closeHelpOnEscape(event: KeyboardEvent): void {
  if (event.key === 'Escape' && helpDetails.value?.open) helpDetails.value.open = false
}

// 首次进入后保持挂载（v-show），使草稿与待核对操作键在跨页切换时保留；锁定/换令牌时卸载。
const providersPage = ref<InstanceType<typeof ProvidersPage> | null>(null)
const providersMounted = ref(false)
const platformsPage = ref<InstanceType<typeof PlatformsPage> | null>(null)
const platformsMounted = ref(false)
const pluginsPage = ref<InstanceType<typeof PluginsPage> | null>(null)
const pluginsMounted = ref(false)
const configurationPage = ref<InstanceType<typeof SystemConfigurationPage> | null>(null)
const configurationMounted = ref(false)
const tasksPage = ref<InstanceType<typeof TasksPage> | null>(null)
const tasksMounted = ref(false)
const tracesPage = ref<InstanceType<typeof TracesPage> | null>(null)
const tracesMounted = ref(false)
const logsPage = ref<InstanceType<typeof RuntimeLogsPage> | null>(null)
const logsMounted = ref(false)

const sessionNotice = ref<string | null>(null)
const configDraftCount = computed(() => configurationPage.value?.draftCount ?? 0)
const providersDraftCount = computed(() => providersPage.value?.draftCount ?? 0)
const platformsDraftCount = computed(() => platformsPage.value?.draftCount ?? 0)
const pluginsDraftCount = computed(() => pluginsPage.value?.draftCount ?? 0)
const totalDraftCount = computed(() =>
  configDraftCount.value + providersDraftCount.value + platformsDraftCount.value + pluginsDraftCount.value
)

const pageGuides: Record<AppView, { title: string; group: string; icon: AppIconName; summary: string; tips: string[] }> = {
  overview: {
    title: '系统概览',
    group: '工作台',
    icon: 'overview',
    summary: '查看运行健康状态、组件在线情况与近期异常，点击任意异常项可直达对应页面排查。',
    tips: [
      '顶部状态栏展示总体健康结论与备份健康告警。',
      '点击组件状态表中的条目可直接跳转至对应的模型供应商、消息平台或插件页面。',
      '右上角顶栏支持在任意页面随时切入或退出管理员模式。'
    ]
  },
  providers: {
    title: '模型供应商',
    group: '接入与扩展',
    icon: 'provider',
    summary: '集中管理 OpenAI / Anthropic / Gemini 等模型供应商、默认主/快模型路由及连接测试。',
    tips: [
      '点击「新增模型供应商」通过品牌预设快速创建，默认保存为停用，测连通过后再启用。',
      '点击「路由与默认模型」调整主模型、快速回复模型与全局凭据。',
      '供应商改名提交真实名称（nameRaw），凭据只写不回显。'
    ]
  },
  platforms: {
    title: '消息平台',
    group: '接入与扩展',
    icon: 'platform',
    summary: '管理 QQ 私域、QQ 官方开放平台、飞书与本地调试通道的驱动启停、接入凭据与访问策略。',
    tips: [
      '每张平台卡已整合驱动状态、运行启停与对应底层驱动包版本。',
      '点击「接入与策略」编辑白名单/黑名单、动作规则与平台密钥。',
      'QQ 官方开放平台提供专属「消息能力与限制」视图。'
    ]
  },
  plugins: {
    title: '插件中心',
    group: '接入与扩展',
    icon: 'plugin',
    summary: '管理内置功能插件与第三方扩展的开关、参数、受控运行界面及市场安装/导入。',
    tips: [
      '使用顶部页签在「已安装插件」「插件市场」「仓库与导入」之间无缝切换。',
      '声明了沙箱界面的插件（如 Token 配额、表情包）可直接从卡片点「打开界面」。',
      '系统底层驱动包与模型引擎已归入对应专题页，不在此处混排。'
    ]
  },
  configuration: {
    title: '系统配置',
    group: '接入与扩展',
    icon: 'config',
    summary: '管理机器人基础行为与人设、对话编排、内置 Agent 工具、健康日报与核心系统包状态。',
    tips: [
      '通过左侧分区切换基础行为、对话编排、Agent 与健康日报，改动先进入草稿。',
      '底部保存条支持先「校验」再「保存」，字段错误会直接定位在对应输入框下方。',
      '「高级与系统包」汇总展示全部 system.* 引擎包的版本与需重启状态。'
    ]
  },
  tasks: {
    title: 'Agent 任务',
    group: '运行与观测',
    icon: 'tasks',
    summary: '提交后台 Agent 任务、处理待审批工具请求，并在右侧抽屉查看安全任务结果。',
    tips: [
      '待审批队列支持就地「重新读取队列」并显示剩余有效时间。',
      '同一幂等键提交不会重复执行；点击「查看结果」从右侧抽屉浏览摘要。',
      '低频的原操作键核对与诊断统计已收拢至页头工具入口。'
    ]
  },
  traces: {
    title: '轨迹与缓存',
    group: '运行与观测',
    icon: 'traces',
    summary: '只读观测最近轮次的耗时、token 用量、缓存命中率与节点耗时明细（不含会话正文）。',
    tips: [
      '点击任意轮次可展开节点级耗时、工具与状态明细。',
      '「未知」表示供应商未上报缓存用量，与真实 0 命中严格区分。',
      '本页仅展示形状指标，不包含会话内容或工具载荷。'
    ]
  },
  logs: {
    title: '运行日志',
    group: '运行与观测',
    icon: 'logs',
    summary: '实时查看后端脱敏后的运行事件摘要，按级别、来源或关键词快速排查告警。',
    tips: [
      '默认每秒自动拉取增量日志，切页或最小化窗口时自动暂停并中止在途请求。',
      '支持一键级别筛选、自动换行切换与视口范围安全复制。',
      '后端不返回聊天正文或凭据原文。'
    ]
  }
}

const currentGuide = computed(() => pageGuides[view.value])
const isRefreshing = computed(() => loading.value || subviewBusy.value)

let sessionGeneration = 0
let connectSequence = 0
let loadSequence = 0

const client = computed(() => new ZbaClient({ token: token.value }))
const compatibility = computed(() => evaluateCompatibility(meta.value, health.value))
const connected = computed(() => token.value.length > 0 && status.value !== null)
const stale = computed(() => error.value !== null && status.value !== null)

function clearSession(noticeOverride?: string): void {
  const hadDrafts = totalDraftCount.value > 0
  if (noticeOverride) {
    sessionNotice.value = noticeOverride
  } else if (hadDrafts) {
    sessionNotice.value = '会话已结束，未保存的配置草稿与秘密输入已清除。'
  }
  configurationPage.value?.endSession()
  providersMounted.value = false
  platformsMounted.value = false
  pluginsMounted.value = false
  configurationMounted.value = false
  tasksMounted.value = false
  tracesMounted.value = false
  logsMounted.value = false
  sessionGeneration += 1
  token.value = ''
  mode.value = 'read'
  meta.value = null
  status.value = null
  view.value = 'overview'
}

function onAuthExpired(ui: UiError): void {
  if (token.value.length === 0) return
  if (ui.status === 403) {
    // 403 表示当前身份缺少管理写/核对权限：安全降级为只读并清理秘密输入，不误踢出已连接的只读会话。
    mode.value = 'read'
    discardActiveSecrets()
    adminError.value = ui
    return
  }
  const hadDrafts = totalDraftCount.value > 0
  clearSession(hadDrafts ? '会话已失效，未保存的配置草稿与秘密输入已清除。' : '管理会话已失效，请重新连接。')
  authError.value = ui
}

function beginSession(value: string): number {
  sessionGeneration += 1
  token.value = value
  mode.value = 'read'
  meta.value = null
  status.value = null
  lastSuccessAt.value = null
  error.value = null
  adminError.value = null
  sessionNotice.value = null
  return sessionGeneration
}

async function load(): Promise<void> {
  const session = sessionGeneration
  const sequence = ++loadSequence
  const activeClient = client.value
  loading.value = true
  error.value = null

  const current = (): boolean => session === sessionGeneration && sequence === loadSequence

  try {
    const nextHealth = await activeClient.health()
    if (!current()) return
    health.value = nextHealth
    if (token.value.length === 0) return

    const nextMeta = await activeClient.meta()
    if (!current()) return
    const nextStatus = await activeClient.status()
    if (!current()) return
    meta.value = nextMeta
    status.value = nextStatus
    lastSuccessAt.value = new Date().toISOString()
    adminError.value = null
    if (mode.value === 'admin' && !compatibility.value.writesEnabled) mode.value = 'read'
  } catch (cause) {
    if (!current()) return
    const ui = toUiError(cause)
    error.value = ui

    if (token.value.length === 0) {
      authError.value = ui
    } else if (cause instanceof ZbaRequestError && (cause.status === 401 || cause.code === 'zba_not_configured')) {
      clearSession()
      authError.value = ui
    }
  } finally {
    if (sequence === loadSequence) loading.value = false
  }
}

async function reloadStatus(): Promise<void> {
  const session = sessionGeneration
  const activeClient = client.value
  const current = (): boolean => session === sessionGeneration && activeClient === client.value
  try {
    const nextMeta = await activeClient.meta()
    if (!current()) return
    const nextStatus = await activeClient.status()
    if (!current()) return
    meta.value = nextMeta
    status.value = nextStatus
    lastSuccessAt.value = new Date().toISOString()
    error.value = null
    adminError.value = null
    if (mode.value === 'admin' && !compatibility.value.writesEnabled) mode.value = 'read'
  } catch (cause) {
    if (!current()) return
    const ui = toUiError(cause)
    error.value = ui
    if (cause instanceof ZbaRequestError && (cause.status === 401 || cause.code === 'zba_not_configured')) {
      onAuthExpired(ui)
    }
    throw cause
  }
}

async function connect(value: string): Promise<void> {
  const attempt = ++connectSequence
  const session = beginSession(value)
  authError.value = null
  connecting.value = true
  try {
    await load()
    if (session === sessionGeneration && status.value === null) authError.value = error.value
  } finally {
    if (attempt === connectSequence) connecting.value = false
  }
}

async function refresh(): Promise<void> {
  if (view.value === 'providers') { await providersPage.value?.reload(); return }
  if (view.value === 'platforms') { await platformsPage.value?.reload(); return }
  if (view.value === 'plugins') { await pluginsPage.value?.reload(); return }
  if (view.value === 'configuration') { await configurationPage.value?.reload(); return }
  if (view.value === 'tasks') { await tasksPage.value?.reload(); return }
  if (view.value === 'traces') { await tracesPage.value?.reload(); return }
  if (view.value === 'logs') { await logsPage.value?.reload(); return }
  await load()
}

function navTrigger(event?: MouseEvent): HTMLElement | null {
  const target = event?.currentTarget
  return target instanceof HTMLElement ? target : null
}

async function allowNavigation(event?: MouseEvent): Promise<boolean> {
  const trigger = navTrigger(event)
  if (view.value === 'configuration') return await configurationPage.value?.requestLeave('navigate', trigger) ?? true
  if (view.value === 'providers') return await providersPage.value?.requestLeave('navigate', trigger) ?? true
  if (view.value === 'platforms') return await platformsPage.value?.requestLeave('navigate', trigger) ?? true
  if (view.value === 'plugins') return await pluginsPage.value?.requestLeave('navigate', trigger) ?? true
  return true
}

function discardActiveSecrets(): void {
  providersPage.value?.discardSecretsOnLeave()
  platformsPage.value?.discardSecretsOnLeave()
  pluginsPage.value?.discardSecretsOnLeave()
  configurationPage.value?.discardSecretsOnLeave()
}

async function navigateTo(target: AppView, event?: MouseEvent, ownerId?: string): Promise<void> {
  const session = sessionGeneration
  if (view.value === target && !ownerId) return
  if (!(await allowNavigation(event)) || session !== sessionGeneration) return
  if (helpDetails.value) helpDetails.value.open = false
  discardActiveSecrets()

  if (target === 'overview') {
    view.value = 'overview'
    return
  }
  if (target === 'providers') {
    const first = !providersMounted.value
    providersMounted.value = true
    view.value = 'providers'
    await nextTick()
    if (!first) await providersPage.value?.reload()
    if (ownerId === 'models') providersPage.value?.openRoutingSettings()
    else if (ownerId === 'model-providers') providersPage.value?.openProviderByKey('primary')
    return
  }
  if (target === 'platforms') {
    const first = !platformsMounted.value
    platformsMounted.value = true
    view.value = 'platforms'
    await nextTick()
    if (!first) await platformsPage.value?.reload()
    if (ownerId && view.value === 'platforms' && session === sessionGeneration) {
      platformsPage.value?.openDriverSettingsById(ownerId, navTrigger(event))
    }
    return
  }
  if (target === 'plugins') {
    const first = !pluginsMounted.value
    pluginsMounted.value = true
    view.value = 'plugins'
    await nextTick()
    if (!first) await pluginsPage.value?.reload()
    if (ownerId && view.value === 'plugins' && session === sessionGeneration) {
      pluginsPage.value?.openSettingsById(ownerId, navTrigger(event))
    }
    return
  }
  if (target === 'configuration') {
    const first = !configurationMounted.value
    configurationMounted.value = true
    view.value = 'configuration'
    await nextTick()
    if (!first) await configurationPage.value?.refreshOnEnter()
    return
  }
  if (target === 'tasks') {
    const first = !tasksMounted.value
    tasksMounted.value = true
    view.value = 'tasks'
    await nextTick()
    if (!first) await tasksPage.value?.reload()
    return
  }
  if (target === 'traces') {
    const first = !tracesMounted.value
    tracesMounted.value = true
    view.value = 'traces'
    await nextTick()
    if (!first) await tracesPage.value?.reload()
    return
  }
  if (target === 'logs') {
    logsMounted.value = true
    view.value = 'logs'
  }
}

async function openOwnerFromConfiguration(target: 'providers' | 'platforms' | 'plugins', owner?: string): Promise<void> {
  await navigateTo(target, undefined, owner)
}

// 兼容既有会话恢复单测与脚本调用的具名导航方法
const showOverview = (event?: MouseEvent) => navigateTo('overview', event)
const openProviders = (event?: MouseEvent) => navigateTo('providers', event)
const openPlatforms = (event?: MouseEvent) => navigateTo('platforms', event)
const openPlugins = (event?: MouseEvent, owner?: string) => navigateTo('plugins', event, owner)
const openConfiguration = (event?: MouseEvent) => navigateTo('configuration', event)
const openTasks = (event?: MouseEvent) => navigateTo('tasks', event)
const openTraces = (event?: MouseEvent) => navigateTo('traces', event)
const openLogs = (event?: MouseEvent) => navigateTo('logs', event)

defineExpose({
  showOverview,
  openProviders,
  openPlatforms,
  openPlugins,
  openConfiguration,
  openTasks,
  openTraces,
  openLogs,
  lock
})

async function navigateFromOverview(
  target: AppView | 'capabilities',
  event?: MouseEvent,
  ownerId?: string
): Promise<void> {
  if (target === 'capabilities') { await navigateTo('plugins', event, ownerId); return }
  await navigateTo(target, event, ownerId)
}

async function lock(event?: MouseEvent): Promise<void> {
  const trigger = navTrigger(event)
  for (const page of [configurationPage.value, providersPage.value, platformsPage.value, pluginsPage.value]) {
    if (page && !(await page.requestLeave('lock', trigger))) return
  }
  const hadDrafts = totalDraftCount.value > 0
  connectSequence += 1
  loadSequence += 1
  loading.value = false
  subviewBusy.value = false
  connecting.value = false
  authError.value = null
  adminError.value = null
  error.value = null
  lastSuccessAt.value = null
  clearSession(hadDrafts ? '会话已锁定，未保存的配置草稿与秘密输入已清除。' : undefined)
}

async function enableAdmin(): Promise<void> {
  if (adminPending.value) return
  adminError.value = null
  if (!compatibility.value.writesEnabled) {
    adminError.value = { code: 'incompatible_contract', message: '契约不兼容，管理员入口已禁用。' }
    return
  }
  const session = sessionGeneration
  const expectedToken = token.value
  const activeClient = client.value
  if (expectedToken.length === 0) return
  const current = (): boolean => session === sessionGeneration && token.value === expectedToken

  adminPending.value = true
  try {
    const isAdmin = await activeClient.checkAdmin(meta.value?.features.includes('operations.find') === true)
    if (!current()) return
    if (!compatibility.value.writesEnabled) {
      mode.value = 'read'
      adminError.value = { code: 'incompatible_contract', message: '契约已变化，管理员入口已禁用。' }
      return
    }
    if (!isAdmin) {
      mode.value = 'read'
      adminError.value = { code: 'forbidden', message: '当前令牌不能进入管理员模式。', status: 403 }
      return
    }
    mode.value = 'admin'
  } catch (cause) {
    if (!current()) return
    const ui = toUiError(cause)
    adminError.value = ui
    if (cause instanceof ZbaRequestError && cause.status === 401) {
      onAuthExpired(ui)
    }
  } finally {
    if (current()) adminPending.value = false
  }
}

async function disableAdmin(event?: MouseEvent): Promise<void> {
  const trigger = navTrigger(event)
  for (const page of [configurationPage.value, providersPage.value, platformsPage.value, pluginsPage.value]) {
    if (page && !(await page.requestLeave('downgrade', trigger))) return
  }
  mode.value = 'read'
  adminError.value = null
}

function toggleAdminMode(event?: MouseEvent): void {
  if (mode.value === 'admin') void disableAdmin(event)
  else void enableAdmin()
}

// 会话级共享同步调度器
const sessionScheduler = ref<ReturnType<typeof acquireScheduler> | null>(null)
let detachSessionAuth: (() => void) | null = null

function attachSessionSync(activeClient: ZbaClient, enabled: boolean): void {
  detachSessionAuth?.()
  detachSessionAuth = null
  sessionScheduler.value?.release()
  sessionScheduler.value = null
  if (!enabled) return
  const shared = acquireScheduler(activeClient)
  detachSessionAuth = shared.scheduler.onAuthFailure((cause) => {
    if (activeClient !== client.value || token.value.length === 0) return
    const ui = toUiError(cause)
    if (cause.status === 401) onAuthExpired(ui)
    else {
      mode.value = 'read'
      discardActiveSecrets()
      error.value = ui
      adminError.value = ui
    }
  })
  registerAppResources(shared.scheduler, activeClient, { status: reloadStatus })
  sessionScheduler.value = shared
}

watch(() => token.value, () => {
  resetAppResourceSignals()
  attachSessionSync(client.value, token.value.length > 0)
}, { immediate: true })

onMounted(() => {
  void load()
  if (typeof document !== 'undefined') {
    document.addEventListener('click', closeHelpIfOutside)
    document.addEventListener('keydown', closeHelpOnEscape)
  }
})

onBeforeUnmount(() => {
  if (typeof document !== 'undefined') {
    document.removeEventListener('click', closeHelpIfOutside)
    document.removeEventListener('keydown', closeHelpOnEscape)
  }
  detachSessionAuth?.()
  detachSessionAuth = null
  sessionScheduler.value?.release()
  sessionScheduler.value = null
})
</script>

<template>
  <div class="app-shell" :class="[connected ? 'app-shell--console' : 'app-shell--auth', { 'app-shell--rail': connected && rail }]">
    <template v-if="connected">
      <aside class="sidebar">
        <div class="sidebar-brand">
          <span class="brand-badge"><BrandMark :size="26" /></span>
          <div class="brand-text">
            <strong>BotAgent</strong>
            <span>Agent Console</span>
          </div>
          <button
            type="button"
            class="sidebar-rail-toggle"
            data-testid="sidebar-rail-toggle"
            :aria-label="rail ? '展开侧栏' : '收起侧栏'"
            :title="rail ? '展开侧栏' : '收起侧栏'"
            :aria-expanded="!rail"
            @click="toggleRail"
          >
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <rect x="3" y="4" width="18" height="16" rx="2" />
              <path d="M9 4v16" />
              <path :d="rail ? 'M13 10l2 2-2 2' : 'M16 10l-2 2 2 2'" />
            </svg>
          </button>
        </div>

        <nav class="side-nav" aria-label="主导航">
          <p class="side-nav-group" aria-hidden="true">工作台</p>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '系统概览' : undefined"
            :class="{ active: view === 'overview' }"
            :aria-current="view === 'overview' ? 'page' : undefined"
            data-testid="nav-overview"
            @click="navigateTo('overview', $event)"
          >
            <AppIcon name="overview" />
            <span class="side-nav-text"><span>系统概览</span></span>
          </button>

          <p class="side-nav-group" aria-hidden="true">接入与扩展</p>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '模型供应商' : undefined"
            :class="{ active: view === 'providers' }"
            :aria-current="view === 'providers' ? 'page' : undefined"
            data-testid="nav-providers"
            @click="navigateTo('providers', $event)"
          >
            <AppIcon name="provider" />
            <span class="side-nav-text"><span>模型供应商</span></span>
            <span v-if="providersDraftCount > 0" class="nav-badge" data-testid="nav-providers-draft">{{ providersDraftCount }}</span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '消息平台' : undefined"
            :class="{ active: view === 'platforms' }"
            :aria-current="view === 'platforms' ? 'page' : undefined"
            data-testid="nav-platforms"
            @click="navigateTo('platforms', $event)"
          >
            <AppIcon name="platform" />
            <span class="side-nav-text"><span>消息平台</span></span>
            <span v-if="platformsDraftCount > 0" class="nav-badge" data-testid="nav-platforms-draft">{{ platformsDraftCount }}</span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '插件中心' : undefined"
            :class="{ active: view === 'plugins' }"
            :aria-current="view === 'plugins' ? 'page' : undefined"
            data-testid="nav-plugins"
            @click="navigateTo('plugins', $event)"
          >
            <AppIcon name="plugin" />
            <span class="side-nav-text"><span>插件中心</span></span>
            <span v-if="pluginsDraftCount > 0" class="nav-badge" data-testid="nav-plugins-draft">{{ pluginsDraftCount }}</span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '系统配置' : undefined"
            :class="{ active: view === 'configuration' }"
            :aria-current="view === 'configuration' ? 'page' : undefined"
            data-testid="nav-configuration"
            @click="navigateTo('configuration', $event)"
          >
            <AppIcon name="config" />
            <span class="side-nav-text"><span>系统配置</span></span>
            <span v-if="configDraftCount > 0" class="nav-badge" data-testid="nav-config-draft">{{ configDraftCount }}</span>
          </button>

          <p class="side-nav-group" aria-hidden="true">运行与观测</p>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? 'Agent 任务' : undefined"
            :class="{ active: view === 'tasks' }"
            :aria-current="view === 'tasks' ? 'page' : undefined"
            data-testid="nav-tasks"
            @click="navigateTo('tasks', $event)"
          >
            <AppIcon name="tasks" />
            <span class="side-nav-text"><span>Agent 任务</span></span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '轨迹与缓存' : undefined"
            :class="{ active: view === 'traces' }"
            :aria-current="view === 'traces' ? 'page' : undefined"
            data-testid="nav-traces"
            @click="navigateTo('traces', $event)"
          >
            <AppIcon name="traces" />
            <span class="side-nav-text"><span>轨迹与缓存</span></span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :title="rail ? '运行日志' : undefined"
            :class="{ active: view === 'logs' }"
            :aria-current="view === 'logs' ? 'page' : undefined"
            data-testid="nav-logs"
            @click="navigateTo('logs', $event)"
          >
            <AppIcon name="logs" />
            <span class="side-nav-text"><span>运行日志</span></span>
          </button>
        </nav>

        <div class="sidebar-note">
          <p class="sidebar-note-title">当前会话</p>
          <p class="sidebar-note-state">
            <span class="dot" :class="{ 'dot--warn': stale }" aria-hidden="true" />
            {{ stale ? '已连接 · 待同步' : '已连接' }}{{ mode === 'admin' ? ' · 管理员' : ' · 只读' }}
          </p>
          <p class="sidebar-note-copy">令牌仅保留于当前页面内存；锁定或关闭页面即清除。</p>
        </div>

        <div class="sidebar-footer">
          <span>BotAgent</span>
          <span>{{ meta?.contractVersion || 'zba-1.0' }}</span>
        </div>
      </aside>

      <div class="app-frame">
        <header class="topbar">
          <div class="crumbs" aria-label="面包屑">
            <span>{{ currentGuide.group }}</span>
            <span class="crumb-sep" aria-hidden="true">/</span>
            <strong>{{ currentGuide.title }}</strong>
          </div>
          <div class="topbar-actions">
            <button
              type="button"
              class="admin-mode-btn"
              :data-mode="mode"
              data-testid="global-admin-toggle"
              :disabled="adminPending || (mode !== 'admin' && !compatibility.writesEnabled)"
              :title="mode === 'admin' ? '点击退出管理员模式，恢复标准只读' : '点击进入管理员模式以修改配置'"
              @click="toggleAdminMode($event)"
            >
              <AppIcon name="shield" :size="14" />
              {{ adminPending ? '校验权限…' : mode === 'admin' ? '管理员模式 · 退出' : '标准只读 · 切换管理' }}
            </button>

            <span class="connection-chip" :class="{ 'connection-chip--stale': stale }" data-testid="connection-state">
              <span class="dot" :class="{ 'dot--warn': stale }" aria-hidden="true" />
              {{ stale ? '同步过期' : '已连接' }}
            </span>

            <button
              type="button"
              class="tonal"
              data-testid="header-refresh"
              :disabled="isRefreshing"
              @click="refresh"
            >
              <AppIcon name="refresh" :size="15" />
              {{ isRefreshing ? '刷新中…' : '刷新' }}
            </button>

            <details ref="helpDetails" :key="view" class="page-help" data-testid="page-guide">
              <summary aria-label="本页说明" title="本页说明"><AppIcon name="help" :size="18" /></summary>
              <div class="page-help-content">
                <strong>{{ currentGuide.title }}</strong>
                <p>{{ currentGuide.summary }}</p>
                <ul><li v-for="tip in currentGuide.tips" :key="tip">{{ tip }}</li></ul>
              </div>
            </details>

            <ThemeToggle />

            <button type="button" class="tonal" data-testid="lock" @click="lock($event)">
              <AppIcon name="lock" :size="15" />
              锁定
            </button>
          </div>
        </header>

        <main class="app-main">
          <p v-if="adminError && view !== 'overview'" class="inline-notice bad top-admin-err" role="alert" data-testid="global-admin-error">
            {{ adminError.message }}
          </p>

          <OverviewPage
            v-if="view === 'overview' && health && meta && status"
            :client="client"
            :health="health"
            :meta="meta"
            :status="status"
            :compatibility="compatibility"
            :stale="stale"
            :error="error"
            :last-success-at="lastSuccessAt"
            :mode="mode"
            :admin-error="adminError"
            :busy="loading"
            @refresh="refresh"
            @enable-admin="enableAdmin"
            @disable-admin="disableAdmin"
            @navigate="navigateFromOverview"
            @auth-expired="onAuthExpired"
          />
          <ProvidersPage
            v-if="providersMounted"
            v-show="view === 'providers'"
            ref="providersPage"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            :features="meta?.features ?? []"
            :active="view === 'providers'"
            @enable-admin="enableAdmin"
            @auth-expired="onAuthExpired"
            @busy="value => { if (view === 'providers') subviewBusy = value }"
          />
          <PlatformsPage
            v-if="platformsMounted"
            v-show="view === 'platforms'"
            ref="platformsPage"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            :features="meta?.features ?? []"
            :active="view === 'platforms'"
            @enable-admin="enableAdmin"
            @auth-expired="onAuthExpired"
            @busy="value => { if (view === 'platforms') subviewBusy = value }"
          />
          <PluginsPage
            v-if="pluginsMounted"
            v-show="view === 'plugins'"
            ref="pluginsPage"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            :features="meta?.features ?? []"
            :active="view === 'plugins'"
            @enable-admin="enableAdmin"
            @auth-expired="onAuthExpired"
            @busy="value => { if (view === 'plugins') subviewBusy = value }"
          />
          <SystemConfigurationPage
            v-if="configurationMounted"
            v-show="view === 'configuration'"
            ref="configurationPage"
            :active="view === 'configuration'"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            @open-domain="openOwnerFromConfiguration"
            @enable-admin="enableAdmin"
            @auth-expired="onAuthExpired"
          />
          <TasksPage
            v-if="tasksMounted"
            v-show="view === 'tasks'"
            ref="tasksPage"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            :active="view === 'tasks'"
            @enable-admin="enableAdmin"
            @auth-expired="onAuthExpired"
          />
          <TracesPage
            v-if="tracesMounted"
            v-show="view === 'traces'"
            ref="tracesPage"
            :client="client"
            :active="view === 'traces'"
            @auth-expired="onAuthExpired"
          />
          <RuntimeLogsPage
            v-if="logsMounted"
            v-show="view === 'logs'"
            ref="logsPage"
            :client="client"
            :active="view === 'logs'"
            @auth-expired="onAuthExpired"
          />
        </main>
      </div>
    </template>

    <template v-else>
      <div class="auth-appearance"><ThemeToggle /></div>
      <main class="app-main auth-main">
        <AuthGate
          :configured="health?.configured ?? null"
          :contract-version="health?.contractVersion ?? null"
          :error="authError"
          :notice="sessionNotice"
          :busy="connecting"
          :probing="loading && token.length === 0 && !connecting"
          @connect="connect"
        />
      </main>
    </template>
  </div>
</template>

<style scoped>
.top-admin-err {
  margin-bottom: 16px;
}
</style>
