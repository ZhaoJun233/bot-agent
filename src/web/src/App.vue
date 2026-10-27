<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import AuthGate from './features/auth/AuthGate.vue'
import OverviewPage from './features/overview/OverviewPage.vue'
import ConfigurationPage from './features/configuration/ConfigurationPage.vue'
import CapabilitiesPage from './features/capabilities/CapabilitiesPage.vue'
import TasksPage from './features/tasks/TasksPage.vue'
import AppIcon from './shared/ui/AppIcon.vue'
import BrandMark from './shared/ui/BrandMark.vue'
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

const token = ref('')
const mode = ref<'read' | 'admin'>('read')
const health = ref<ZbaHealth | null>(null)
const meta = ref<ZbaMeta | null>(null)
const status = ref<ZbaStatus | null>(null)
const error = ref<UiError | null>(null)
const authError = ref<UiError | null>(null)
const adminError = ref<UiError | null>(null)
const loading = ref(false)
const connecting = ref(false)
const lastSuccessAt = ref<string | null>(null)
const view = ref<'overview' | 'configuration' | 'capabilities' | 'tasks'>('overview')
const configurationPage = ref<InstanceType<typeof ConfigurationPage> | null>(null)
// 配置页首次进入后保持挂载（v-show），草稿因此跨页保留；锁定/换令牌时才卸载。
const configurationMounted = ref(false)
const capabilitiesPage = ref<InstanceType<typeof CapabilitiesPage> | null>(null)
// 能力页同样首次进入后保持挂载（v-show）：待核对的操作键要能跨视图存活。
const capabilitiesMounted = ref(false)
const tasksPage = ref<InstanceType<typeof TasksPage> | null>(null)
// 任务页同样常驻：提交记录与幂等键要能跨视图存活。
const tasksMounted = ref(false)
const sessionNotice = ref<string | null>(null)
const configDraftCount = computed(() => configurationPage.value?.draftCount ?? 0)

// 每次连接/锁定都推进 sessionGeneration，使迟到的异步结果失效。
let sessionGeneration = 0
// 连接尝试单独计数：旧连接不能清掉新连接的 busy，但会话失效时自己的 busy 必须释放。
let connectSequence = 0
// 刷新可能重叠；只有最后一次 load 可以写回状态和 loading。
let loadSequence = 0

const client = computed(() => new ZbaClient({ token: token.value }))
const compatibility = computed(() => evaluateCompatibility(meta.value, health.value))
const connected = computed(() => token.value.length > 0 && status.value !== null)
const stale = computed(() => error.value !== null && status.value !== null)

function clearSession(): void {
  // 401/会话失效时没有交互机会：立即清除内存草稿与秘密输入，并在重新连接页说明。
  if (configDraftCount.value > 0) {
    sessionNotice.value = '会话已结束，未保存的配置草稿与秘密输入已清除。'
  }
  configurationPage.value?.endSession()
  configurationMounted.value = false
  // 会话结束 = 旧操作键作废：卸载能力页，连同未完成的动作一起丢弃。
  capabilitiesMounted.value = false
  tasksMounted.value = false
  sessionGeneration += 1
  token.value = ''
  mode.value = 'read'
  meta.value = null
  status.value = null
  view.value = 'overview'
}

/** 配置链路的 401：管理会话已失效，统一锁门并清掉草稿与秘密输入。 */
function onAuthExpired(ui: UiError): void {
  if (token.value.length === 0) return
  clearSession()
  authError.value = ui
  if (sessionNotice.value === null) sessionNotice.value = '管理会话已失效，请重新连接。'
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
    // 与 reloadStatus 同一条规则：meta 落地后先复核归属，会话在两次 await 之间失效时
    // 不该再向旧 client 发第二次读取（LIFE-04）。
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

/**
 * 重读概览所需的两份资源（status + meta）。
 *
 * 这是共享调度器在 resync 时调用的「status」补读：它必须走**真实的概览读取**，
 * 而不是只更新一个信号量——否则计划要求的「resync 覆盖 status」在界面上是空的。
 *
 * 后台读取与手动读取走**同一套会话归属、错误呈现与 401 失效规则**（M1）：
 *
 * - 失败：保留旧数据（status 不清空），置 error → 概览显示 stale 横幅；
 * - 成功：清错误、刷新 lastSuccessAt；
 * - 401 / 未配置：与手动读取一样 clearSession() 并锁门，不留"已连接/管理员模式"的假象；
 * - 失败照常抛出：调度器据此不签"已追平"（R2）。
 */
async function reloadStatus(): Promise<void> {
  const session = sessionGeneration
  const activeClient = client.value
  const current = (): boolean => session === sessionGeneration && activeClient === client.value
  try {
    const nextMeta = await activeClient.meta()
    // meta 落地后先复核归属：会话在此期间失效时，不该再向旧 client 发第二次读取。
    if (!current()) return
    const nextStatus = await activeClient.status()
    if (!current()) return
    meta.value = nextMeta
    status.value = nextStatus
    lastSuccessAt.value = new Date().toISOString()
    // 成功即恢复：清掉上一次后台失败留下的错误与 stale 横幅。
    error.value = null
    adminError.value = null
    if (mode.value === 'admin' && !compatibility.value.writesEnabled) mode.value = 'read'
  } catch (cause) {
    if (!current()) return
    const ui = toUiError(cause)
    // 旧数据保留在屏幕上，但必须明确"它可能过期"。
    error.value = ui
    if (cause instanceof ZbaRequestError && (cause.status === 401 || cause.code === 'zba_not_configured')) {
      // 后台也要锁门：认证已失效时不能继续显示已连接与管理员模式。
      clearSession()
      authError.value = ui
      if (sessionNotice.value === null) sessionNotice.value = '管理会话已失效，请重新连接。'
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
    // load() 在 401/503 时会 clearSession() 并推进 sessionGeneration；
    // busy 的所有权属于这次连接尝试，不能因会话失效而永远卡住。
    if (attempt === connectSequence) connecting.value = false
  }
}

async function refresh(): Promise<void> {
  if (view.value === 'configuration') {
    await configurationPage.value?.reload()
    return
  }
  if (view.value === 'capabilities') {
    await capabilitiesPage.value?.reload()
    return
  }
  if (view.value === 'tasks') {
    await tasksPage.value?.reload()
    return
  }
  await load()
}

/** 配置页首次进入时挂载；之后只切换显隐，草稿随组件保留。 */
async function openConfiguration(): Promise<void> {
  if (view.value === 'configuration') return
  const firstVisit = !configurationMounted.value
  configurationMounted.value = true
  view.value = 'configuration'
  if (!firstVisit) {
    await nextTick()
    // 重读基线但不丢草稿：外部修改会在这里体现为新的脏值或重叠字段。
    await configurationPage.value?.refreshOnEnter()
  }
}

/**
 * 点击事件里的那个按钮。
 *
 * 三个引擎对"点按钮要不要给按钮焦点"并不一致（WebKit 不给），
 * 所以"关闭模态后把焦点还给触发器"不能只靠读 activeElement。
 */
function navTrigger(event?: MouseEvent): HTMLElement | null {
  const target = event?.currentTarget
  return target instanceof HTMLElement ? target : null
}

/** 切回概览不销毁配置页，字段草稿保留；秘密输入在离开视图时丢弃。 */
function showOverview(): void {
  configurationPage.value?.discardSecretsOnLeave()
  view.value = 'overview'
}

async function openCapabilities(event?: MouseEvent): Promise<void> {
  if (view.value === 'configuration') {
    // 配置页有草稿时先走同一个三选一，避免切页绕过未保存保护。
    // 把点击的按钮传下去：WebKit 点按钮不给焦点，只靠 activeElement 还原不了。
    const allowed = await configurationPage.value?.requestLeave('downgrade', navTrigger(event)) ?? true
    if (!allowed) return
  }
  if (view.value === 'capabilities') return
  const firstVisit = !capabilitiesMounted.value
  capabilitiesMounted.value = true
  view.value = 'capabilities'
  await nextTick()
  // 首次进入由 onMounted 读取；再次进入只重读目录，待核对的操作键保留。
  if (!firstVisit) await capabilitiesPage.value?.reload()
}

async function openTasks(event?: MouseEvent): Promise<void> {
  if (view.value === 'configuration') {
    const allowed = await configurationPage.value?.requestLeave('downgrade', navTrigger(event)) ?? true
    if (!allowed) return
  }
  if (view.value === 'tasks') return
  const firstVisit = !tasksMounted.value
  tasksMounted.value = true
  view.value = 'tasks'
  await nextTick()
  // 首次进入由 onMounted 读取；再次进入只重读，提交记录与幂等键保留。
  if (!firstVisit) await tasksPage.value?.reload()
}

async function lock(event?: MouseEvent): Promise<void> {
  const allowed = await configurationPage.value?.requestLeave('lock', navTrigger(event)) ?? true
  if (!allowed) return
  configurationPage.value?.endSession()
  configurationMounted.value = false
  capabilitiesMounted.value = false
  tasksMounted.value = false
  connectSequence += 1
  clearSession()
  loadSequence += 1
  loading.value = false
  connecting.value = false
  authError.value = null
  adminError.value = null
  error.value = null
  lastSuccessAt.value = null
}

async function enableAdmin(): Promise<void> {
  adminError.value = null
  if (!compatibility.value.writesEnabled) {
    adminError.value = { code: 'incompatible_contract', message: '契约不兼容，管理员入口已禁用。' }
    return
  }

  // 探测期间可能锁定或换令牌；返回后必须仍属于同一会话才允许改 mode。
  const session = sessionGeneration
  const expectedToken = token.value
  const activeClient = client.value
  if (expectedToken.length === 0) return

  const current = (): boolean => session === sessionGeneration && token.value === expectedToken

  try {
    const isAdmin = await activeClient.checkAdmin()
    if (!current()) return
    // 探测期间 meta/health 可能已经刷新为不兼容；模式展示不能越过当前兼容性判定。
    if (!compatibility.value.writesEnabled) {
      mode.value = 'read'
      adminError.value = {
        code: 'incompatible_contract',
        message: '契约已变化，管理员入口已禁用。'
      }
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
      clearSession()
      authError.value = ui
    }
  }
}

async function disableAdmin(): Promise<void> {
  // 退出管理员模式后写入口关闭；有草稿时先让用户保存/继续编辑/明确放弃。
  if (configDraftCount.value > 0) {
    const allowed = await configurationPage.value?.requestLeave('downgrade') ?? true
    if (!allowed) return
  }
  mode.value = 'read'
  adminError.value = null
}

// ── 会话级共享同步（计划 §9.3：五类资源一个调度器） ─────────────────
const sessionScheduler = ref<ReturnType<typeof acquireScheduler> | null>(null)

function attachSessionSync(activeClient: ZbaClient, enabled: boolean): void {
  sessionScheduler.value?.release()
  sessionScheduler.value = null
  if (!enabled) return
  const shared = acquireScheduler(activeClient)
  // status 走真实重读；配置与能力由 app-resources 自己探测。
  registerAppResources(shared.scheduler, activeClient, { status: reloadStatus })
  sessionScheduler.value = shared
}

/**
 * 连接建立后挂上会话级同步，锁定/换令牌时释放。
 *
 * 只监听令牌：`client` 完全由令牌派生，多一个监听会在同一次刷新里二次
 * attach（先 release 再 acquire，游标被无谓清零）。
 *
 * 释放会 stop() 调度器并丢弃游标：旧身份的游标绝不带给下一个会话。
 * 任务页自己也 acquire 同一个 client 上的调度器（引用计数），所以两边共享一条
 * events 轮询链，不会各开一个定时器。
 */
watch(() => token.value, () => {
  // 令牌变化 = 新身份：资源快照与游标都属于旧身份。
  // 这里**不推进** sessionGeneration：beginSession() 已经推进过，重复推进会
  // 让紧随其后的 load() 全部判为过期（current() 失配），页面永远读不到东西。
  // 在途读取的作废靠 reloadStatus() 自己的 (session, client) 双重比对。
  resetAppResourceSignals()
  attachSessionSync(client.value, token.value.length > 0)
}, { immediate: true })

onBeforeUnmount(() => {
  sessionScheduler.value?.release()
  sessionScheduler.value = null
})

onMounted(load)
</script>

<template>
  <div class="app-shell" :class="connected ? 'app-shell--console' : 'app-shell--auth'">
    <template v-if="connected">
      <aside class="sidebar">
        <div class="sidebar-brand">
          <span class="brand-badge"><BrandMark :size="26" /></span>
          <div class="brand-text">
            <strong>BotAgent</strong>
            <span>ZBA 工作台</span>
          </div>
        </div>

        <nav class="side-nav" aria-label="主导航">
          <button
            type="button"
            class="side-nav-item"
            :class="{ active: view === 'overview' }"
            :aria-current="view === 'overview' ? 'page' : undefined"
            data-testid="nav-overview"
            @click="showOverview"
          >
            <AppIcon name="overview" />
            <span>概览</span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :class="{ active: view === 'capabilities' }"
            :aria-current="view === 'capabilities' ? 'page' : undefined"
            data-testid="nav-capabilities"
            @click="openCapabilities($event)"
          >
            <AppIcon name="capabilities" />
            <span>能力</span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :class="{ active: view === 'configuration' }"
            :aria-current="view === 'configuration' ? 'page' : undefined"
            data-testid="nav-configuration"
            @click="openConfiguration"
          >
            <AppIcon name="config" />
            <span>配置</span>
            <span v-if="configDraftCount > 0" class="nav-badge" data-testid="nav-config-draft">
              {{ configDraftCount }}
            </span>
          </button>
          <button
            type="button"
            class="side-nav-item"
            :class="{ active: view === 'tasks' }"
            :aria-current="view === 'tasks' ? 'page' : undefined"
            data-testid="nav-tasks"
            @click="openTasks($event)"
          >
            <AppIcon name="tasks" />
            <span>任务</span>
          </button>
        </nav>

        <div class="sidebar-note">
          <p class="sidebar-note-title">当前会话</p>
          <p class="sidebar-note-state"><span class="dot" aria-hidden="true"></span>已连接{{ mode === 'admin' ? ' · 管理员' : '' }}</p>
          <p class="sidebar-note-copy">令牌仅保存在此页面内存；锁定后需要重新输入。</p>
        </div>

        <div class="sidebar-footer">
          <span>BotAgent</span>
          <span>zba-1.0</span>
        </div>
      </aside>

      <div class="app-frame">
        <header class="topbar">
          <div class="crumbs" aria-label="面包屑">
            <span>工作台</span>
            <span class="crumb-sep" aria-hidden="true">/</span>
            <strong>{{ view === 'configuration' ? '配置' : view === 'capabilities' ? '能力' : view === 'tasks' ? '任务' : '概览' }}</strong>
          </div>
          <div class="topbar-actions">
            <span class="connection-chip" data-testid="connection-state">
              <span class="dot" aria-hidden="true"></span>
              已连接
            </span>
            <button
              v-if="view !== 'overview'"
              type="button"
              class="tonal"
              data-testid="header-refresh"
              :disabled="loading"
              @click="refresh"
            >
              <AppIcon name="refresh" :size="16" />
              刷新
            </button>
            <button type="button" class="tonal" data-testid="lock" @click="lock($event)">
              <AppIcon name="lock" :size="16" />
              锁定
            </button>
          </div>
        </header>

        <main class="app-main">
          <OverviewPage
            v-if="view === 'overview' && health && meta && status"
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
          />
          <CapabilitiesPage
            v-if="capabilitiesMounted"
            v-show="view === 'capabilities'"
            ref="capabilitiesPage"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            :active="view === 'capabilities'"
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
          <ConfigurationPage
            v-if="configurationMounted"
            v-show="view === 'configuration'"
            ref="configurationPage"
            :client="client"
            :mode="mode"
            :writes-enabled="compatibility.writesEnabled"
            @enable-admin="enableAdmin"
            @auth-expired="onAuthExpired"
          />
        </main>
      </div>
    </template>

    <template v-else>
      <main class="app-main auth-main">
        <AuthGate
          :configured="health?.configured ?? null"
          :contract-version="health?.contractVersion ?? null"
          :error="authError"
          :notice="sessionNotice"
          :busy="connecting || (loading && token.length === 0)"
          @connect="connect"
        />
      </main>
    </template>
  </div>
</template>
