<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaActionCapability,
  type ZbaManagedAction,
  type ZbaManagedDescriptor,
  type ZbaManagedInspectResult,
  type ZbaManagedKind,
  type ZbaManagedObjectKey,
  type ZbaManagedOperationResult,
  type ZbaManagedRuntimeState
} from '../../api/zba'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { acquireScheduler } from '../../shared/sync/event-scheduler'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { isRestorable, restoreFocus } from '../../shared/ui/focus-restore'
import { createOperationKey, newNonce, nowSeconds } from '../configuration/draft'
import PluginSettingsDialog from './PluginSettingsDialog.vue'
import { settingsFor, type PluginSettingsSpec } from './plugin-settings'
import { confirmToggle, readSwitches, setPluginEnabled, type PluginSwitchState } from './plugin-toggle'
import { reasonLabel } from './reason-labels'
import { capabilityTitle } from '../../shared/friendly'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  /** 视图是否在前台；离开视图后不允许把在途动作继续发出去。 */
  active?: boolean
}>(), { active: true })

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const kinds: Array<{ value: ZbaManagedKind | 'all'; label: string }> = [
  { value: 'all', label: '全部' },
  { value: 0, label: '平台驱动' },
  { value: 1, label: '插件' },
  { value: 2, label: '模型提供商' },
  { value: 3, label: '设备' },
  { value: 4, label: 'MCP' }
]

const runtimeLabels: Record<number, string> = {
  0: '未知', 1: '未加载', 2: '已停用', 3: '启动中',
  4: '运行中', 5: '停止中', 6: '已断开', 7: '故障'
}
const runtimeTones: Record<number, string> = {
  0: 'warn', 1: 'neutral', 2: 'neutral', 3: 'warn',
  4: 'ok', 5: 'warn', 6: 'bad', 7: 'bad'
}
const actionLabels: Record<number, string> = {
  0: '查看', 1: '校验配置', 2: '配置', 3: '启动', 4: '停止',
  5: '测试连接', 6: '连接', 7: '断开', 8: '取消'
}
const stateLabels: Record<number, string> = {
  0: '未开始', 1: '执行中', 2: '已完成', 3: '失败',
  4: '已请求取消', 5: '已取消', 6: '未知'
}
const stateTones: Record<number, string> = {
  0: 'neutral', 1: 'warn', 2: 'ok', 3: 'bad', 4: 'warn', 5: 'neutral', 6: 'warn'
}
const resultMessages: Record<number, string> = {
  1: '当前令牌没有该操作权限。',
  2: '没有找到这个能力。',
  3: '登记身份有歧义，需要人工核对。',
  4: '这个能力不支持该动作。',
  5: '描述符版本已变化，请重新读取后再试。',
  6: '字段校验失败。',
  7: '游标已过期，请重新加载列表。',
  8: '游标无效，请重新加载列表。',
  9: '同一操作键带了不同内容，已被拒绝。',
  10: '操作键已过期。',
  11: '操作键代际不匹配。',
  12: '操作容量已满，请稍后再试。',
  13: '管理存储暂不可用。',
  14: '结果未知，需要核对。',
  15: '服务器时钟回退。',
  16: '契约版本不受支持。'
}

/** 终态：只有这三个状态才不需要继续核对（与服务端 ManagedTerminal 一致）。 */
const terminalStates: number[] = [2, 3, 5]

/**
 * 动作需要一个配置代际（generation）和契约版本。它们只能从 configuration/read
 * 拿到——meta 里的 contractVersion 是 zba-1.0，不是 cfg 契约，不能拿来拼操作键。
 */
const context = ref<{ generation: string; contractRevision: string } | null>(null)
const contextError = ref<UiError | null>(null)
const items = ref<ZbaManagedDescriptor[]>([])
const nextCursor = ref<string | null>(null)
const kindFilter = ref<ZbaManagedKind | 'all'>('all')
const loading = ref(false)
const error = ref<UiError | null>(null)
const pageError = ref<UiError | null>(null)
const lastLoadedAt = ref<string | null>(null)
const selected = ref<ZbaManagedDescriptor | null>(null)
const selectedBusy = ref(false)
const selectedError = ref<UiError | null>(null)
const abortedNotice = ref<string | null>(null)
const dismissNotice = ref<string | null>(null)

type PendingAction = { descriptor: ZbaManagedDescriptor; capability: ZbaActionCapability; reuseKey: string | null }
const pendingAction = ref<PendingAction | null>(null)
const actionBusy = ref(false)
const actionError = ref<UiError | null>(null)
const confirmDialog = ref<HTMLElement | null>(null)
const confirmButton = ref<HTMLButtonElement | null>(null)
const confirmCancelButton = ref<HTMLButtonElement | null>(null)
/** 页面标题：触发器已经不在了时的安全落点（tabindex="-1"，不进 Tab 顺序）。 */
const pageHeading = ref<HTMLElement | null>(null)
/**
 * 打开确认框之前焦点落在哪个元素上（S4-M1）。
 *
 * 关闭确认框后必须把焦点还给它：键盘用户按下 Enter 打开、按 Escape 取消，
 * 如果焦点掉到 body，下一次 Tab 就要从页面开头重新走一遍。
 * WebKit 点按钮不会给按钮焦点，所以这里同时接受事件目标作为兜底。
 */
const actionTrigger = ref<HTMLElement | null>(null)

/**
 * 一次动作的完整上下文，独立于「回执」保存。
 *
 * 关键约束（S23-R2）：确认之后，命令/目标/操作键立刻写进这条记录，并且
 * 记录比确认框、比回执都活得久。回执只是它的一个字段——以回执为容器的
 * 写法会让「响应丢了」和「核对入口消失」变成同一件事。
 */
type OperationPhase = 'preparing' | 'submitting' | 'unknown' | 'accepted' | 'rejected'
interface OperationRecord {
  key: string
  target: ZbaManagedObjectKey
  identity: string
  label: string
  kindLabel: string
  action: ZbaManagedAction
  command: unknown
  descriptorRevision: string
  phase: OperationPhase
  result: ZbaManagedOperationResult | null
  cancelledState: number | null
  error: UiError | null
  busy: boolean
  /** 正在为「结果未知」做只读预检；预检不改变这次操作本身的已知信息。 */
  checking: boolean
  /**
   * 这条操作曾经有过「结果未知」的历史（可能已经在服务端产生了副作用）。
   * 一旦为 true，本次重发收到的任何无回执拒绝都不能把它降级成「确认没执行」。
   */
  priorUnknown: boolean
}

const operations = ref<OperationRecord[]>([])
const activeKey = ref<string | null>(null)
const activeRecord = computed<OperationRecord | null>(
  () => operations.value.find((record) => record.key === activeKey.value) ?? null
)
const waitingRecords = computed<OperationRecord[]>(
  () => operations.value.filter((record) => record.key !== activeKey.value && unsettled(record))
)

/**
 * 页面所有权代数。卸载、离开视图、降级都会让在途提交失效——每个 await
 * 之后、真正发出写请求之前都要重新核对，否则会出现「已经离开页面/已经
 * 撤销写权限，还把 execute 发出去」。
 */
let ownership = 0
let unmounted = false

function ownedBy(epoch: number, client: ZbaClient): boolean {
  return !unmounted && epoch === ownership && props.client === client
    && props.active !== false && props.mode === 'admin' && props.writesEnabled
}

const formatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium', timeStyle: 'medium', hour12: false
})

// ── 插件设置弹窗 ───────────────────────────────────────────────
const settingsTarget = ref<{ spec: PluginSettingsSpec; displayName: string } | null>(null)
let settingsTrigger: HTMLElement | null = null

function pluginSpec(descriptor: ZbaManagedDescriptor): PluginSettingsSpec | undefined {
  return descriptor.key.kind === 'plugin' ? settingsFor(descriptor.key.key) : undefined
}

function openSettings(descriptor: ZbaManagedDescriptor, event?: MouseEvent): void {
  const spec = pluginSpec(descriptor)
  if (!spec) return
  settingsTrigger = eventTrigger(event)
  settingsTarget.value = { spec, displayName: capabilityTitle(descriptor.displayName, descriptor.key) || spec.title }
}

function closeSettings(saved: boolean): void {
  settingsTarget.value = null
  const trigger = settingsTrigger
  settingsTrigger = null
  void nextTick(() => restoreFocus(trigger, pageHeading.value))
  // 保存过：重读目录，让卡片上的运行状态/开关跟上。
  if (saved) void load(undefined, 'background')
}

// ── 插件「启用」开关 ─────────────────────────────────────────
const switches = ref<Record<string, PluginSwitchState>>({})
const switchBusy = ref<string | null>(null)
const switchNotice = ref<{ pluginKey: string; tone: 'ok' | 'warn' | 'bad'; text: string } | null>(null)

function switchOf(descriptor: ZbaManagedDescriptor): PluginSwitchState | null {
  if (descriptor.key.kind !== 'plugin' || !pluginSpec(descriptor)?.enableField) return null
  const state = switches.value[descriptor.key.key]
  return state && state.enabled !== null ? state : null
}

/** 开关状态来自配置快照（read kind 1）；读不到就不显示开关，不影响目录本身。 */
async function loadSwitches(): Promise<void> {
  try {
    const snapshot = await props.client.readConfiguration(1)
    switches.value = snapshot.view ? readSwitches(snapshot.view) : {}
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    switches.value = {}
  }
}

async function toggleSwitch(descriptor: ZbaManagedDescriptor, event: Event): Promise<void> {
  const input = event.target as HTMLInputElement
  const spec = pluginSpec(descriptor)
  const state = switchOf(descriptor)
  if (!spec || !state || !canWrite.value || switchBusy.value !== null) {
    if (state) input.checked = state.enabled === true
    return
  }
  const target = input.checked
  const pluginKey = spec.pluginKey
  switchBusy.value = pluginKey
  switchNotice.value = null
  try {
    let result = await setPluginEnabled(props.client, spec, target)
    if (result.kind === 'unconfirmed') {
      const confirmed = await confirmToggle(props.client, result.key)
      if (confirmed === true) result = { kind: 'saved', enabled: target }
      else if (confirmed === false) {
        result = { kind: 'error', error: { code: 'not_applied', message: '服务端没有这次提交的记录，开关没有变。' } }
      }
    }
    const name = capabilityTitle(descriptor.displayName, descriptor.key) || spec.title
    switch (result.kind) {
      case 'saved':
      case 'noop':
        switchNotice.value = { pluginKey, tone: 'ok', text: name + (result.enabled ? ' 已启用。' : ' 已停用。') }
        break
      case 'conflict':
        switchNotice.value = { pluginKey, tone: 'warn', text: '设置刚被其他人改过，已读取最新状态，确认后再切一次。' }
        break
      case 'unconfirmed':
        switchNotice.value = { pluginKey, tone: 'warn', text: '没收到服务端确认，不确定有没有生效；已重新读取当前状态。' }
        break
      case 'auth':
        emit('auth-expired', result.error)
        break
      case 'error':
        switchNotice.value = { pluginKey, tone: 'bad', text: result.error.message }
        break
    }
  } finally {
    await loadSwitches()
    const latest = switchOf(descriptor)
    input.checked = latest?.enabled === true
    switchBusy.value = null
    void load(undefined, 'background')
  }
}

/** 卡片上的开关状态用人话：「期望 启用」对用户没有意义。 */
function desiredLabel(value: boolean | null | undefined): string {
  if (value === null || value === undefined) return '—'
  return value ? '已启用' : '已停用'
}

/** 详情/回执面板出现在页面底部：打开后滚过去，否则用户以为按钮没反应。 */
function revealPanel(testid: string): void {
  void nextTick(() => {
    const node = document.querySelector('[data-testid="' + testid + '"]')
    if (node instanceof HTMLElement && typeof node.scrollIntoView === 'function') {
      node.scrollIntoView({ behavior: 'smooth', block: 'nearest' })
    }
  })
}

const canWrite = computed(() => props.mode === 'admin' && props.writesEnabled)
const filterCount = computed(() => items.value.length)
const counts = computed(() => {
  const result = { driver: 0, plugin: 0, provider: 0, device: 0, mcp: 0 }
  for (const item of items.value) result[item.key.kind] += 1
  return result
})

function formatTime(value: string | null): string {
  if (!value) return '—'
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : formatter.format(date)
}

const SEP = String.fromCharCode(0)

/**
 * 结构化身份：kind + 完整键字段。标签只用于显示，绝不能当主键——
 * 同名插件与供应商可以合法共存，驱动还可能同 platformId 不同 accountScope。
 */
function keyIdentity(key: ZbaManagedObjectKey): string {
  switch (key.kind) {
    case 'driver':
      return 'driver' + SEP + key.key.platformId + SEP + key.key.accountScope + SEP + key.key.instanceId
    case 'device':
      return 'device' + SEP + key.key.deviceId
    default:
      return key.kind + SEP + key.key
  }
}

function keyLabel(key: ZbaManagedObjectKey): string {
  switch (key.kind) {
    case 'driver':
      return key.key.instanceId
        ? key.key.platformId + ' / ' + key.key.instanceId
        : key.key.platformId + ' / ' + key.key.accountScope
    case 'device':
      return key.key.deviceId
    default:
      return key.key
  }
}

/**
 * DOM/测试用的稳定标识：带上 kind。标签本身不是主键——同名插件与供应商
 * 可以合法共存，只用标签会让两个卡片的 data-testid 撞在一起。
 */
function keySlug(key: ZbaManagedObjectKey): string {
  return key.kind + ':' + keyLabel(key)
}

function kindLabel(kind: ZbaManagedObjectKey['kind']): string {
  return ({ driver: '驱动', plugin: '插件', provider: '供应商', device: '设备', mcp: 'MCP' } as Record<string, string>)[kind] ?? kind
}

function runtimeLabel(state: ZbaManagedRuntimeState): string {
  return runtimeLabels[state] ?? ('未知状态 ' + state)
}

function runtimeTone(state: ZbaManagedRuntimeState): string {
  return runtimeTones[state] ?? 'warn'
}

function actionLabel(action: ZbaManagedAction): string {
  return actionLabels[action] ?? ('动作 ' + action)
}

function stateLabel(state: number): string {
  return stateLabels[state] ?? ('状态 ' + state)
}

function stateTone(state: number): string {
  return stateTones[state] ?? 'warn'
}

function supportedActions(descriptor: ZbaManagedDescriptor): ZbaActionCapability[] {
  return descriptor.actions.filter((capability) => capability.supported)
}

function unsupportedActions(descriptor: ZbaManagedDescriptor): ZbaActionCapability[] {
  return descriptor.actions.filter((capability) => !capability.supported)
}

function supportsAction(descriptor: ZbaManagedDescriptor, action: ZbaManagedAction): boolean {
  return descriptor.actions.some((capability) => capability.action === action && capability.supported)
}

function isDangerous(action: ZbaManagedAction): boolean {
  return action === 4 || action === 7
}

function isTerminal(state: number): boolean {
  return terminalStates.includes(state)
}

/**
 * 这条操作还没有结束吗（还会影响服务端，或还有没核对完的结果）。
 *
 * 只有两种「确定结束」：服务端明确拒绝（rejected，一个字节都没生效），
 * 或者拿到了终态回执。accepted 但没有回执是**异常状态**，不能当成已结束——
 * 一旦把它判成结束，关闭面板就会连操作键一起丢掉。
 */
function unsettled(record: OperationRecord): boolean {
  if (record.phase === 'rejected') return false
  if (record.phase === 'accepted') {
    const state = record.result?.receipt?.state
    return state === undefined || !isTerminal(state)
  }
  return true
}

/** 可以在服务端查到东西了：这一步确实发出过请求（preparing 阶段一个字节都没发）。 */
function canQuery(record: OperationRecord): boolean {
  return record.phase === 'accepted' || record.phase === 'unknown'
}

/** 结果未知、需要用户决定是否用同一个键重试。 */
function canRetry(record: OperationRecord): boolean {
  return record.phase === 'unknown'
}

function reportAuthFailure(cause: unknown): boolean {
  if (cause instanceof ZbaRequestError && cause.status === 401) {
    emit('auth-expired', toUiError(cause))
    return true
  }
  return false
}

async function loadContext(): Promise<void> {
  if (context.value !== null) return
  contextError.value = null
  try {
    const snapshot = await props.client.readConfiguration(0)
    context.value = {
      generation: snapshot.generation,
      contractRevision: snapshot.contractRevision
    }
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    contextError.value = toUiError(cause)
  }
}

let loadSequence = 0

async function load(cursor?: string, mode: 'initial' | 'interactive' | 'background' = 'initial'): Promise<boolean> {
  if (loading.value) return false
  const sequence = ++loadSequence
  // 后台/交互式补读都要保住旧快照：读取失败显示旧目录，而不是假装"没有能力"。
  const snapshot = mode === 'initial' ? null : items.value
  const snapshotCursor = nextCursor.value
  loading.value = true
  error.value = null
  pageError.value = null
  await loadContext()
  try {
    const page = await props.client.listCapabilities({
      kind: kindFilter.value === 'all' ? undefined : kindFilter.value,
      limit: 100,
      cursor
    })
    if (sequence !== loadSequence) return true
    items.value = cursor ? [...items.value, ...page.items] : page.items
    nextCursor.value = page.nextCursor ?? null
    lastLoadedAt.value = new Date().toISOString()
    if (!cursor && switchBusy.value === null) void loadSwitches()
    return true
  } catch (cause) {
    if (sequence !== loadSequence) return true
    if (reportAuthFailure(cause)) return false
    const ui = toUiError(cause)
    if (cursor) {
      // 翻页失败不能丢掉已加载的列表，也不能假装加载成功。
      pageError.value = ui
    } else {
      error.value = ui
      // 读取失败保留旧目录：显示旧数据 + 明确失败提示（与任务页同一语义）。
      items.value = snapshot ?? []
      nextCursor.value = snapshotCursor
    }
    return false
  } finally {
    if (sequence === loadSequence) loading.value = false
  }
}

// ── 共享事件同步（计划 §9.3：能力目录也是五类补读之一） ──────────────
const detachScheduler = ref<(() => void) | null>(null)

function attachScheduler(client: ZbaClient): void {
  detachScheduler.value?.()
  const { scheduler, release } = acquireScheduler(client)
  const off = scheduler.subscribe({
    name: 'capabilities',
    matches: (event) => event.type === 'capability.executed',
    // 失败必须抛给调度器：自己吞掉会让它误签"已追平"并推进游标（R2）。
    reload: async () => {
      if (!(await load(undefined, 'background'))) throw new Error('capabilities_read_failed')
    }
  })
  detachScheduler.value = () => {
    off()
    release()
  }
}

/**
 * 筛选在加载期间禁用：之前是「点了没反应但按钮已经亮起」，
 * 迟到的响应还会覆盖列表，让标签与内容不一致。
 */
function changeFilter(value: ZbaManagedKind | 'all'): void {
  if (loading.value || kindFilter.value === value) return
  kindFilter.value = value
  void load()
}

async function loadMore(): Promise<void> {
  const cursor = nextCursor.value
  if (cursor === null) return
  await load(cursor)
}

async function inspect(descriptor: ZbaManagedDescriptor): Promise<void> {
  if (selectedBusy.value) return
  selected.value = descriptor
  selectedError.value = null
  selectedBusy.value = true
  revealPanel('cap-detail')
  try {
    const result = await props.client.inspectCapability(descriptor.key)
    if (result.descriptor) {
      const identity = keyIdentity(result.descriptor.key)
      selected.value = result.descriptor
      items.value = items.value.map((item) => (keyIdentity(item.key) === identity ? result.descriptor! : item))
    } else {
      selectedError.value = {
        code: 'inspect_' + result.code,
        message: resultMessages[result.code] ?? ('查看失败（code ' + result.code + '）。')
      }
    }
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    selectedError.value = toUiError(cause)
  } finally {
    selectedBusy.value = false
  }
}

/**
 * 动作 → 契约命令。ManagedCommand 只有 4 种 kind（driver/plugin/provider/mcp），
 * 没有 device；命令编号见 ManagedCapability.cs（Driver Start/Stop/Test=0/1/2 等）。
 * 不在表里的组合一律返回 null，界面不发送猜测出来的命令。
 */
function commandFor(descriptor: ZbaManagedDescriptor, capability: ZbaActionCapability): unknown | null {
  const key = descriptor.key
  const table: Record<string, Record<number, number>> = {
    driver: { 3: 0, 4: 1, 5: 2 },
    plugin: { 3: 0, 4: 1 },
    provider: { 5: 0 },
    mcp: { 6: 0, 7: 1, 5: 2 }
  }
  const action = table[key.kind]?.[capability.action]
  if (action === undefined) return null
  if (key.kind === 'driver') return { kind: 'driver', key: { ...key.key }, action }
  if (key.kind === 'plugin') return { kind: 'plugin', key: key.key, action }
  if (key.kind === 'provider') return { kind: 'provider', key: key.key, action }
  if (key.kind === 'mcp') return { kind: 'mcp', key: key.key, action }
  return null
}

function requestAction(
  descriptor: ZbaManagedDescriptor,
  capability: ZbaActionCapability,
  event?: MouseEvent
): void {
  if (!canWrite.value || pendingAction.value !== null) return
  actionError.value = null
  abortedNotice.value = null
  dismissNotice.value = null
  // 同一目标 + 同一动作已有未闭合的操作时，重试必须复用那个键：
  // 服务端按操作键去重，换键等于让它执行第二次。
  const identity = keyIdentity(descriptor.key)
  const open = operations.value.find(
    (record) => record.identity === identity && record.action === capability.action && unsettled(record)
  )
  if (open && open.phase !== 'unknown') {
    // 已经发出、还没终态（提交中 / 已受理）：既不能新建，也不能重放，
    // 只把面板聚焦到那条操作上，让用户核对或请求取消。
    activeKey.value = open.key
    return
  }
  const trigger = eventTrigger(event)
  pendingAction.value = { descriptor, capability, reuseKey: open?.key ?? null }
  // 焦点落点必须**可用**（S4-M2）：读不到配置代际时"确认执行"是禁用的，
  // 对禁用按钮 focus() 是无声空操作，焦点会留在背景卡片上，
  // 于是 Escape 事件根本到不了模态——键盘用户被困住。
  void nextTick(() => {
    const confirm = confirmButton.value
    if (confirm !== null && !confirm.disabled) {
      confirm.focus()
      return
    }
    confirmCancelButton.value?.focus()
  })
  actionTrigger.value = trigger
}

/**
 * 打开模态的那个按钮。
 *
 * 优先取点击目标：WebKit 点按钮**不给**按钮焦点，只读 activeElement 会拿到
 * body，关框后就没地方还焦点了。键盘 Enter 打开时两者是同一个元素。
 */
function eventTrigger(event?: MouseEvent): HTMLElement | null {
  const target = event?.currentTarget
  if (target instanceof HTMLElement && isRestorable(target)) return target
  const active = document.activeElement
  return active instanceof HTMLElement && isRestorable(active) ? active : null
}

/**
 * 取消当前的确认意图（S4-R1）。
 *
 * 取消**永远不发写请求**，所以它不能受"另一个动作正在提交"的限制。上一轮
 * 就是这个 `if (actionBusy) return` 让第二个确认框的两个按钮一起被禁用：
 * 焦点留在背景卡片上，Escape 进不了模态，用户被上一个请求的耗时锁住。
 * 在途动作的忙锁只该锁**写**，不该锁**退**。
 */
function cancelAction(): void {
  pendingAction.value = null
  actionError.value = null
  restoreActionFocus()
}

/** 关闭确认框后把焦点还给打开它的按钮（S4-M1）；触发器不在时退到页面标题。 */
function restoreActionFocus(): void {
  const trigger = actionTrigger.value
  actionTrigger.value = null
  void nextTick(() => {
    restoreFocus(trigger, pageHeading.value)
  })
}

/**
 * 确认框开着时，焦点不能掉到框外（S4-M4）。
 *
 * 提交期间 actionBusy 会把两个按钮都禁用，浏览器于是把焦点丢回 body，
 * keydown 落到背景页面上——模态在"正在提交"这个状态里接不住键盘。
 * 与 S4-M2 同类：落点不可用就等于模态失效。
 */
watch([actionBusy, pendingAction], () => {
  if (pendingAction.value === null) return
  const active = document.activeElement
  const root = confirmDialog.value
  if (root !== null && active instanceof HTMLElement && root.contains(active) && isRestorable(active)) return
  void nextTick(() => {
    const confirm = confirmButton.value
    if (confirm !== null && !confirm.disabled) {
      confirm.focus()
      return
    }
    confirmCancelButton.value?.focus()
  })
}, { flush: 'post' })

/**
 * 确认框的键盘退出与焦点循环。
 *
 * 配置页的离开确认框早就有这一套（Escape = 安全动作、Tab 不跑出遮罩），
 * 能力页却没有——同一个产品里两个模态的键盘行为不该不一样（S4-F2）。
 * Escape 在这里等于"取消"，因为它就是安全动作：不发出任何写请求。
 */
function onConfirmKeydown(event: KeyboardEvent): void {
  if (pendingAction.value === null) return
  if (event.key === 'Escape') {
    event.preventDefault()
    cancelAction()
    return
  }
  if (event.key !== 'Tab') return
  const root = confirmDialog.value
  if (!root) return
  const focusable = Array.from(root.querySelectorAll<HTMLElement>('button:not([disabled])'))
  if (focusable.length === 0) return
  const first = focusable[0]
  const last = focusable[focusable.length - 1]
  if (!first || !last) return
  const active = document.activeElement as HTMLElement | null
  const outside = active === null || !root.contains(active)
  if (event.shiftKey && (active === first || outside)) {
    event.preventDefault()
    last.focus()
  } else if (!event.shiftKey && (active === last || outside)) {
    event.preventDefault()
    first.focus()
  }
}

function recordFor(key: string): OperationRecord | undefined {
  return operations.value.find((record) => record.key === key)
}

/** 只丢已经收拾干净的旧记录，绝不因为「条数太多」删掉待核对的操作键。 */
function trimOperations(): void {
  if (operations.value.length <= 12) return
  const settledRecord = [...operations.value].reverse().find((record) => !unsettled(record))
  if (settledRecord === undefined) return
  operations.value = operations.value.filter((record) => record.key !== settledRecord.key)
}

/**
 * 新建一条操作记录。
 *
 * 这里绝不改写同键的既有记录：既有记录里的 command / descriptorRevision
 * 是「那一次提交」的冻结内容，改写它等于让重放变成另一次操作——服务端会
 * 按 IdempotencyConflict 拒绝，或者在没收到第一次请求时执行成第二件事。
 * 复用同一次操作走 resendUnknown()，它一个字段都不改。
 */
function beginRecord(input: {
  key: string
  target: ZbaManagedObjectKey
  action: ZbaManagedAction
  command: unknown
}): OperationRecord {
  // 记录本身用 reactive 包一层：异步流程持有的是这个代理，改字段才会驱动界面。
  const record = reactive<OperationRecord>({
    key: input.key,
    target: input.target,
    identity: keyIdentity(input.target),
    label: keyLabel(input.target),
    kindLabel: kindLabel(input.target.kind),
    action: input.action,
    command: input.command,
    descriptorRevision: '',
    phase: 'preparing',
    result: null,
    cancelledState: null,
    error: null,
    busy: false,
    checking: false,
    priorUnknown: false
  })
  operations.value = [record, ...operations.value]
  trimOperations()
  dismissNotice.value = null
  activeKey.value = record.key
  revealPanel('cap-result')
  return record
}

/**
 * 提交结果分类：
 * - 0 + 回执：服务端确认受理，回执给出动作状态。
 * - 0 但没有回执 / 13 存储暂不可用 / 14 未知：不能证明没提交。
 * - 其它业务码：**这一次请求**被明确拒绝。
 *
 * 注意最后一条的边界（S23-R2-F2）：明确拒绝只说明「这次请求没有生效」，
 * 不说明「那次操作从未执行」。同一次操作的重发可能因为服务端重新授权
 * （回放前会重做权限校验）、操作键过期、存储错误等原因被拒，而最初的
 * 请求其实早已受理并产生了副作用。所以对已经出现过未知结果的记录，
 * 无回执的拒绝只能记成「仍然未知」，绝不能降级成 rejected —— 那会让
 * closeResult() 把还带着操作键的记录删掉，下一次确认就换了新键。
 */
function applyResult(record: OperationRecord, result: ZbaManagedOperationResult): void {
  const unknownBefore = record.priorUnknown || record.phase === 'unknown'
  record.result = result
  record.error = null
  if (result.code === 0 && !result.receipt) {
    record.phase = 'unknown'
    record.priorUnknown = true
    return
  }
  if (result.code === 0) {
    record.phase = 'accepted'
    void refreshDescriptor(result.receipt!.target)
    return
  }
  if (result.code === 13 || result.code === 14) {
    record.phase = 'unknown'
    record.priorUnknown = true
    record.error = {
      code: 'execute_' + result.code,
      message: resultMessages[result.code] ?? ('动作结果未知（code ' + result.code + '）。')
    }
    return
  }
  if (unknownBefore) {
    // 重发被拒，但这次操作此前就可能是已执行的：拒绝不能抹掉那个事实。
    record.phase = 'unknown'
    record.priorUnknown = true
    record.error = {
      code: 'execute_rejected_after_unknown_' + result.code,
      message: (resultMessages[result.code] ?? ('这次重发被拒绝（code ' + result.code + '）。'))
        + ' 但这次操作此前已经出现过结果未知：拒绝只说明本次请求没有生效，'
        + '不能证明最初那次请求没有执行。操作键已保留，请继续核对。'
    }
    return
  }
  record.phase = 'rejected'
  record.error = {
    code: 'execute_' + result.code,
    message: resultMessages[result.code] ?? ('动作失败（code ' + result.code + '）。')
  }
}

function applyCause(record: OperationRecord, cause: unknown): void {
  if (reportAuthFailure(cause)) {
    record.phase = 'unknown'
    record.priorUnknown = true
    return
  }
  // 传输失败，或服务端已回 2xx 但响应体无法解码：都不能证明「没执行」。
  const outcomeUnknown = cause instanceof ZbaNetworkError
    || (cause instanceof ZbaRequestError
      && cause.code === 'invalid_json'
      && cause.status >= 200
      && cause.status < 300)
  const unknownBefore = record.priorUnknown || record.phase === 'unknown'
  record.error = toUiError(cause)
  if (outcomeUnknown || unknownBefore) {
    // 同上：对已有未知历史的记录，任何无终态结论的失败都只能保持未知。
    record.phase = 'unknown'
    record.priorUnknown = true
    return
  }
  record.phase = 'rejected'
}

/**
 * 确认 → 立即进入操作面板（关闭确认框），之后所有状态都留在面板里。
 *
 * 确认框的任务只是「收集用户意图」；它留着会把核对入口挡在遮罩后面
 * （上一轮 S23-R2 就是这样把 query 按钮藏起来，又让重试生成新键）。
 *
 * 两条互斥的提交路径：
 * - submitNew()：全新操作，提交前重新 inspect 取当前描述符版本。
 * - resendUnknown()：同一次操作的重发，命令与版本都用第一次提交时冻结的值。
 */
async function confirmAction(): Promise<void> {
  const pending = pendingAction.value
  if (!pending || actionBusy.value) return
  if (context.value === null) {
    actionError.value = contextError.value ?? { code: 'missing_generation', message: '还没有读到配置代际，暂时不能提交动作。' }
    return
  }
  const command = commandFor(pending.descriptor, pending.capability)
  if (command === null) {
    actionError.value = {
      code: 'unsupported_command',
      message: '这个动作没有对应的契约命令，界面不会发送猜测请求。'
    }
    return
  }
  const resume = pending.reuseKey === null ? undefined : recordFor(pending.reuseKey)
  actionBusy.value = true
  actionError.value = null
  // 先关掉确认框再提交：遮罩留着会挡住页面上的锁定/导航和核对入口，
  // 用户在最需要离开的时候反而点不到任何东西。
  pendingAction.value = null
  restoreActionFocus()
  try {
    if (resume) await resendUnknown(resume)
    else await submitNew(pending, command)
  } finally {
    actionBusy.value = false
  }
}

/** 全新操作：预检通过才提交；这一次的命令与版本从此冻结在这条记录里。 */
async function submitNew(pending: PendingAction, command: unknown): Promise<void> {
  const client = props.client
  const epoch = ownership
  const generation = context.value!.generation
  const contractRevision = context.value!.contractRevision
  const key = createOperationKey(generation, nowSeconds(), newNonce()).replace(/^cfg1:/, 'mg1:')
  const record = beginRecord({
    key,
    target: pending.descriptor.key,
    action: pending.capability.action,
    command
  })
  try {
    // 提交前重新 inspect：用当前描述符版本和当前支持矩阵，避免拿过期快照去写。
    // 注意：条目级 descriptorRevision 与目录级 descriptorRevision 不是同一个值，
    // 只能与同一目标的描述符比较。
    const fresh = await client.inspectCapability(pending.descriptor.key)
    if (!ownedBy(epoch, client)) {
      // 已经离开页面 / 降级 / 换会话：一个字节都不能发出去。
      record.phase = 'rejected'
      record.error = {
        code: 'aborted_client',
        message: '已经离开能力页或权限发生变化，这次动作没有发送；操作键已作废。'
      }
      abortedNotice.value = '这次动作在提交前被取消（页面已离开或权限已变化），服务端没有收到请求。'
      return
    }
    if (!fresh.descriptor) {
      record.phase = 'rejected'
      record.error = {
        code: 'inspect_' + fresh.code,
        message: resultMessages[fresh.code] ?? ('提交前核对失败（code ' + fresh.code + '）。')
      }
      return
    }
    if (!supportsAction(fresh.descriptor, pending.capability.action)) {
      record.phase = 'rejected'
      record.error = {
        code: 'descriptor_changed',
        message: '这个动作已不再受支持，描述符已更新；请重新读取目录。'
      }
      return
    }
    record.descriptorRevision = fresh.descriptor.descriptorRevision
    record.phase = 'submitting'
    const result = await client.executeCapability({
      contractRevision,
      expectedDescriptorRevision: record.descriptorRevision,
      key,
      command
    })
    applyResult(record, result)
  } catch (cause) {
    if (record.phase === 'preparing') {
      // 还在 inspect 阶段：这次确实什么也没发，记录标成未发送的拒绝态。
      record.phase = 'rejected'
      if (!reportAuthFailure(cause)) record.error = toUiError(cause)
    } else {
      applyCause(record, cause)
    }
  }
}

/**
 * 重发「结果未知」的那一次操作（S23-R2-F1）。
 *
 * 关键区别：这不是第二次操作。命令与 expectedDescriptorRevision 都是第一次
 * 提交时冻结下来的，重发必须逐字一致——服务端按操作键去重，同键不同内容会被
 * 判成 IdempotencyConflict；而重新 inspect 拿到的版本只是「现在」的描述符，
 * 与那次请求无关。
 *
 * 所以预检只做只读核对（目标还在、动作仍受支持），并且**预检失败不能否定
 * 先前那次提交**：结果仍然是未知，操作键继续保留，用户随时可以再核对或再重发。
 */
async function resendUnknown(record: OperationRecord): Promise<void> {
  const client = props.client
  const epoch = ownership
  const contractRevision = context.value!.contractRevision
  record.busy = true
  record.checking = true
  record.error = null
  // 进入重发时，这次操作就已经有「未知历史」了——本次重发无论收到什么，
  // 都不能反过来证明最初那次请求没有执行（S23-R2-F2）。
  record.priorUnknown = true
  activeKey.value = record.key
  try {
    let fresh: ZbaManagedInspectResult
    try {
      fresh = await client.inspectCapability(record.target)
    } catch (cause) {
      if (reportAuthFailure(cause)) return
      record.phase = 'unknown'
      record.error = preflightFailure(toUiError(cause))
      return
    }
    if (!ownedBy(epoch, client)) {
      // 离页 / 降级 / 换会话：不重发，但这条操作**仍然是未知**，键必须留着。
      record.phase = 'unknown'
      record.error = {
        code: 'aborted_client',
        message: '已经离开能力页或权限发生变化，这次没有重发；上一次提交的结果仍然是未知，操作键已保留。'
      }
      abortedNotice.value = '这次重发在提交前被取消（页面已离开或权限已变化），服务端没有收到新请求。'
      return
    }
    if (!fresh.descriptor) {
      record.error = preflightFailure({
        code: 'inspect_' + fresh.code,
        message: resultMessages[fresh.code] ?? ('提交前核对失败（code ' + fresh.code + '）。')
      })
      return
    }
    if (!supportsAction(fresh.descriptor, record.action)) {
      record.error = preflightFailure({
        code: 'descriptor_changed',
        message: '这个动作已不再受支持，描述符已更新。'
      })
      return
    }
    record.phase = 'submitting'
    const result = await client.executeCapability({
      contractRevision,
      // 冻结值优先；只有从未提交过的记录才会退回当前版本。
      expectedDescriptorRevision: record.descriptorRevision || fresh.descriptor.descriptorRevision,
      key: record.key,
      command: record.command
    })
    applyResult(record, result)
  } catch (cause) {
    if (record.phase === 'submitting') applyCause(record, cause)
    else if (!reportAuthFailure(cause)) record.error = preflightFailure(toUiError(cause))
  } finally {
    record.busy = false
    record.checking = false
  }
}

/** 预检失败的文案：必须说明「上一次提交仍然未知」，否则会被读成否定。 */
function preflightFailure(ui: UiError): UiError {
  return {
    ...ui,
    code: 'preflight_' + ui.code,
    message: '提交前核对失败：' + ui.message + ' 上一次提交的结果仍然是未知，操作键已保留，可以稍后再核对或重发。'
  }
}

/**
 * 用同一个操作键重试提交。
 *
 * 这不是「再执行一次」，而是把**同一次操作**的请求重发一遍：
 * - 服务端已经受理过 → 按键去重，返回原回执（replayed=true），不会产生第二次副作用；
 * - 请求其实没到 → 现在执行，且仍带着最初那份 expectedDescriptorRevision，
 *   描述符若已变化服务端会拒绝（code 5），同样不会误执行。
 *
 * 因此重试**不需要**重新 inspect：拿新版本号去拼新内容才是危险的做法
 * （同一个键带不同内容会被服务端判成 IdempotencyConflict）。
 */
async function retryRecord(record: OperationRecord): Promise<void> {
  if (!canRetry(record) || record.busy) return
  if (!canWrite.value) {
    record.error = { code: 'read_only', message: '当前不是管理员模式，不能重试提交；操作键仍保留用于核对。' }
    return
  }
  const generation = context.value?.generation
  if (generation === undefined) {
    record.error = { code: 'missing_generation', message: '还没有读到配置代际，暂时不能重试提交。' }
    return
  }
  const keyGeneration = record.key.split(':')[1] ?? ''
  if (keyGeneration.toLowerCase() !== generation.toLowerCase()) {
    record.error = {
      code: 'key_generation_mismatch',
      message: '配置代际已变化，这个操作键无法再提交；请读取最新目录后重新发起。'
    }
    return
  }
  const client = props.client
  const epoch = ownership
  const contractRevision = context.value!.contractRevision
  if (!ownedBy(epoch, client)) {
    record.error = { code: 'aborted_client', message: '已经离开能力页或权限发生变化，这次没有重发；操作键仍然保留。' }
    return
  }
  // 专用重试入口同样是「同一次操作的重发」：先记住未知历史，本次拒绝不能抹掉它。
  record.priorUnknown = true
  record.busy = true
  record.phase = 'submitting'
  activeKey.value = record.key
  try {
    const result = await client.executeCapability({
      contractRevision,
      expectedDescriptorRevision: record.descriptorRevision,
      key: record.key,
      command: record.command
    })
    if (!ownedBy(epoch, client) && result.code === 0) {
      // 已经离开页面：结果照实记下来，但不再做后续刷新。
      record.result = result
      record.phase = 'unknown'
      return
    }
    applyResult(record, result)
  } catch (cause) {
    applyCause(record, cause)
  } finally {
    record.busy = false
  }
}

async function refreshDescriptor(target: ZbaManagedObjectKey): Promise<void> {
  try {
    const fresh = await props.client.inspectCapability(target)
    if (!fresh.descriptor) return
    const identity = keyIdentity(fresh.descriptor.key)
    items.value = items.value.map((item) => (keyIdentity(item.key) === identity ? fresh.descriptor! : item))
    if (selected.value && keyIdentity(selected.value.key) === identity) selected.value = fresh.descriptor
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    // 刷新描述符失败不影响已展示的回执；页面保留旧描述符并等待用户重读。
  }
}

/**
 * 核对。响应只写回它自己那条记录，迟到的结果不会串到别的操作上。
 *
 * 回执是「最后一次可信状态」，不能被没有回执的失败覆盖：一次 503 / 传输失败
 * 只能说明这次核对没成功，不能说明那个还在运行的操作已经结束。否则 unsettled()
 * 会把运行中的记录误判成已结束，关闭面板时连操作键一起丢掉。
 */
async function queryRecord(record: OperationRecord): Promise<void> {
  if (record.busy) return
  record.busy = true
  record.error = null
  activeKey.value = record.key
  const trusted = record.result?.receipt ? record.result : null
  try {
    const result = await props.client.queryCapability(record.key)
    if (!recordFor(record.key)) return
    if (result.receipt) {
      record.result = result
      record.phase = 'accepted'
      // 拿到权威回执：这次操作终于有可信结论，未知历史到此结束。
      record.priorUnknown = !isTerminal(result.receipt.state)
      if (isTerminal(result.receipt.state)) void refreshDescriptor(result.receipt.target)
      return
    }
    if (result.code === 0) {
      // 服务端说「没有这次操作的记录」：不能证明没执行，仍然是未知。
      // 已经有可信回执时不能把它抹掉——那会把「运行中」误判成「已结束」。
      if (trusted === null) record.result = result
      record.phase = 'unknown'
      record.priorUnknown = true
      return
    }
    record.error = {
      code: 'query_' + result.code,
      message: resultMessages[result.code] ?? ('核对失败（code ' + result.code + '）。')
    }
    record.phase = 'unknown'
    record.priorUnknown = true
  } catch (cause) {
    if (!reportAuthFailure(cause)) record.error = toUiError(cause)
    record.phase = 'unknown'
    record.priorUnknown = true
  } finally {
    record.busy = false
  }
}

async function cancelRecord(record: OperationRecord): Promise<void> {
  if (record.busy) return
  record.busy = true
  record.error = null
  activeKey.value = record.key
  try {
    const result = await props.client.cancelCapability(record.key)
    if (!recordFor(record.key)) return
    // 取消接口只回 { code, state }，没有回执；界面显示服务端真实状态，
    // 不能把「已请求取消」写成「已取消」。
    record.cancelledState = result.state
    if (result.code !== 0) {
      record.error = {
        code: 'cancel_' + result.code,
        message: resultMessages[result.code] ?? ('取消失败（code ' + result.code + '）；服务端状态为 ' + stateLabel(result.state) + '。')
      }
    }
  } catch (cause) {
    if (!reportAuthFailure(cause)) record.error = toUiError(cause)
  } finally {
    record.busy = false
  }
}

/**
 * 关闭回执面板。
 *
 * 只有**确定结束**的记录才能关掉并删除。未终态的记录既不能删、也不能收起：
 * 面板是这个操作键唯一带着「核对 / 同键重发」入口的地方，收起之后用户就只剩
 * 记忆里的一个键（S23-R2-F1 的丢键路径正是从这里开始的）。
 */
function closeResult(): void {
  const record = activeRecord.value
  if (!record) return
  if (unsettled(record)) {
    dismissNotice.value = '结果还没有终态，面板会保留；操作键不会因此丢失，请先核对或用同一个操作键重发。'
    return
  }
  dismissNotice.value = null
  operations.value = operations.value.filter((item) => item.key !== record.key)
  activeKey.value = operations.value[0]?.key ?? null
  // 关闭按钮随面板一起被卸载，焦点不能掉回 body。
  void nextTick(() => { restoreFocus(null, pageHeading.value) })
}

/** 收起描述符：收起按钮随区块卸载，焦点退到页面标题。 */
function closeDetail(): void {
  selected.value = null
  void nextTick(() => { restoreFocus(null, pageHeading.value) })
}

function focusRecord(record: OperationRecord): void {
  dismissNotice.value = null
  activeKey.value = record.key
}

/**
 * 会话变化（锁定、换令牌、401）时，旧的操作键属于已经不存在的会话：
 * 它们既不能提交也不能核对，必须整体丢弃，而不是留着一个永远核对不通的键。
 */
function resetForSession(): void {
  ownership += 1
  pendingAction.value = null
  actionBusy.value = false
  actionError.value = null
  abortedNotice.value = null
  dismissNotice.value = null
  operations.value = []
  activeKey.value = null
  items.value = []
  nextCursor.value = null
  context.value = null
  contextError.value = null
  error.value = null
  pageError.value = null
  selected.value = null
  selectedError.value = null
  lastLoadedAt.value = null
  void load()
}

let boundClient = props.client
watch(() => props.client, (value) => {
  if (value === boundClient) return
  boundClient = value
  resetForSession()
})

/**
 * 离开视图、退出管理员模式、写能力失效，都会让「这次确认」永久失效。
 *
 * 只比对布尔值是不够的：切到概览再切回来，active 又是 true，迟到的 inspect
 * 就会带着旧意图把写请求发出去（S23-OWN-F1）。所以这些变化必须推进所有权
 * 代数——代数一旦推进，那次确认里的 epoch 就永远对不上。
 */
function abandonInFlight(): void {
  ownership += 1
  if (props.active === false) settingsTarget.value = null
  const hadDialog = pendingAction.value !== null
  pendingAction.value = null
  actionError.value = null
  // 确认框被收走时焦点在框里；页面仍可见就把焦点还回去，不让它掉到 body。
  if (hadDialog && props.active !== false) restoreActionFocus()
  else actionTrigger.value = null
}

watch(() => props.client, (value, previous) => {
  if (value === previous) return
  // 换身份：先释放旧调度器（丢弃旧游标），再挂新的。
  attachScheduler(value)
})

watch(() => props.active, (value) => {
  if (value === false) abandonInFlight()
})

watch(() => props.mode, (value) => {
  if (value !== 'admin') abandonInFlight()
})

watch(() => props.writesEnabled, (value) => {
  if (!value) abandonInFlight()
})

onMounted(() => {
  attachScheduler(props.client)
  void load()
})

onBeforeUnmount(() => {
  unmounted = true
  ownership += 1
  detachScheduler.value?.()
  detachScheduler.value = null
  pendingAction.value = null
  actionBusy.value = false
  operations.value = []
  activeKey.value = null
})

defineExpose({
  reload: () => load(),
  describe: () => ({ items: items.value.length, operations: operations.value.length })
})
</script>

<template>
  <section class="capabilities" data-testid="capabilities">
    <header class="page-heading">
      <div>
        <p class="eyebrow">插件与平台</p>
        <h1 id="capabilities-title" ref="pageHeading" tabindex="-1">已登记能力</h1>
        <p class="subtitle">
          驱动 {{ counts.driver }} · 插件 {{ counts.plugin }} · 供应商 {{ counts.provider }}
          <template v-if="lastLoadedAt"> · 读取于 {{ formatTime(lastLoadedAt) }}</template>
        </p>
      </div>
      <div class="heading-actions">
        <span class="mode-badge" :data-mode="props.mode" data-testid="cap-mode-badge">
          {{ props.mode === 'admin' ? '管理员模式' : '标准模式' }}
        </span>
        <button type="button" class="tonal" data-testid="cap-reload" :disabled="loading" @click="load()">
          {{ loading ? '读取中…' : '重新读取' }}
        </button>
      </div>
    </header>

    <p v-if="props.mode !== 'admin'" class="inline-notice" data-testid="cap-readonly">
      标准模式可以查看能力与支持矩阵；执行动作需要先进入管理员模式。
      <button
        type="button"
        class="tonal inline-action"
        data-testid="cap-enable-admin"
        :disabled="!props.writesEnabled"
        @click="emit('enable-admin')"
      >
        进入管理员模式
      </button>
    </p>
    <p v-else-if="!props.writesEnabled" class="inline-notice bad" data-testid="cap-write-disabled">
      契约不兼容，动作入口已禁用；本页只展示当前能力。
    </p>

    <div class="cap-toolbar" data-testid="cap-toolbar">
      <div class="cap-filters cap-tabs" role="group" aria-label="按类型筛选">
        <button
          v-for="option in kinds"
          :key="String(option.value)"
          type="button"
          class="chip"
          :class="{ active: kindFilter === option.value }"
          :aria-pressed="kindFilter === option.value"
          :data-testid="'cap-filter-' + option.value"
          :disabled="loading"
          @click="changeFilter(option.value)"
        >
          {{ option.label }}
        </button>
      </div>
      <p class="cap-count" data-testid="cap-count">已加载 {{ filterCount }} 项</p>
    </div>

    <ErrorNotice :error="contextError" title="读取配置代际失败" />
    <p v-if="contextError" class="inline-notice warn" data-testid="cap-context-error">
      没有配置代际就不能提交动作；能力目录仍可浏览。
    </p>
    <ErrorNotice :error="error" title="读取能力失败" />
    <ErrorNotice :error="pageError" title="加载下一页失败" />
    <p v-if="pageError" class="inline-notice warn" data-testid="cap-page-error">
      下一页没有加载：服务端拒绝了游标（原始错误见上）。已加载的 {{ filterCount }} 项仍然有效。
    </p>
    <p v-if="abortedNotice" class="inline-notice warn" data-testid="cap-aborted" role="status">{{ abortedNotice }}</p>

    <p v-if="loading && items.length === 0" class="empty-state" data-testid="cap-loading">正在读取能力目录…</p>
    <p v-else-if="!error && items.length === 0" class="empty-state" data-testid="cap-empty">没有已登记的能力。</p>

    <div v-else class="cap-grid">
      <article
        v-for="item in items"
        :key="keyIdentity(item.key)"
        class="cap-card"
        :data-kind="item.key.kind"
        :data-testid="'cap-item-' + keySlug(item.key)"
        :data-cap-identity="keyIdentity(item.key)"
      >
        <header class="cap-card-head">
          <span class="cap-avatar" aria-hidden="true">{{ capabilityTitle(item.displayName, item.key).slice(0, 1) }}</span>
          <div class="cap-title">
            <p class="cap-kind">{{ kindLabel(item.key.kind) }}</p>
            <h2 class="cap-name">{{ capabilityTitle(item.displayName, item.key) }}</h2>
            <p class="cap-key">{{ keyLabel(item.key) }}</p>
          </div>
          <span class="status-badge" :data-tone="runtimeTone(item.runtimeState)" data-testid="cap-state">
            {{ runtimeLabel(item.runtimeState) }}
          </span>
        </header>

        <div v-if="switchOf(item)" class="cap-switch-row" :data-testid="'cap-switch-row-' + keySlug(item.key)">
          <label class="cap-switch" :class="{ busy: switchBusy === item.key.key }">
            <input
              type="checkbox"
              role="switch"
              :checked="switchOf(item)?.enabled === true"
              :disabled="!canWrite || !switchOf(item)?.editable || switchBusy !== null"
              :aria-label="'启用 ' + capabilityTitle(item.displayName, item.key)"
              :data-testid="'cap-switch-' + keySlug(item.key)"
              @change="toggleSwitch(item, $event)"
            />
            <span class="cap-switch-track" aria-hidden="true"><span class="cap-switch-thumb" /></span>
            <span class="cap-switch-label">
              {{ switchBusy === item.key.key ? '正在保存…' : (switchOf(item)?.enabled ? '功能已开启' : '功能已关闭') }}
            </span>
          </label>
          <span v-if="!canWrite" class="cap-switch-hint">管理员模式下可切换</span>
        </div>
        <p
          v-if="switchNotice && switchNotice.pluginKey === item.key.key"
          class="cap-switch-notice"
          :data-tone="switchNotice.tone"
          role="status"
          :data-testid="'cap-switch-notice-' + keySlug(item.key)"
        >{{ switchNotice.text }}</p>

        <dl class="cap-meta">
          <div>
            <dt>开关</dt>
            <dd>{{ desiredLabel(item.desiredEnabled) }}</dd>
          </div>
          <div v-if="item.key.kind !== 'plugin'">
            <dt>凭据</dt>
            <dd>{{ item.credentialConfigured ? '已配置' : '未配置' }}</dd>
          </div>
          <div v-if="item.errorCode">
            <dt>错误</dt>
            <dd class="cap-error-code">{{ item.errorCode }}</dd>
          </div>
        </dl>

        <div class="cap-actions">
          <template v-if="supportedActions(item).length > 0">
            <button
              v-for="capability in supportedActions(item)"
              :key="capability.action"
              type="button"
              class="tonal cap-action"
              :class="{ danger: isDangerous(capability.action) }"
              :disabled="!canWrite"
              :data-testid="'cap-action-' + keySlug(item.key) + '-' + capability.action"
              @click="requestAction(item, capability, $event)"
            >
              {{ actionLabel(capability.action) }}
            </button>
          </template>
          <p v-else class="cap-none" data-testid="cap-no-action">没有可用动作</p>
          <p
            v-for="capability in unsupportedActions(item)"
            :key="'no-' + capability.action"
            class="cap-reason"
            :data-testid="'cap-reason-' + keySlug(item.key) + '-' + capability.action"
          >
            <span :title="capability.reasonCode">{{ actionLabel(capability.action) }}不可用：{{ reasonLabel(capability.reasonCode) }}</span>
          </p>
        </div>

        <footer class="cap-card-foot">
          <button
            v-if="pluginSpec(item)"
            type="button"
            class="tonal cap-settings"
            :data-testid="'cap-settings-' + keySlug(item.key)"
            :aria-label="'设置 ' + capabilityTitle(item.displayName, item.key)"
            @click="openSettings(item, $event)"
          >
            <span aria-hidden="true">⚙</span> 设置
          </button>
          <button
            type="button"
            class="link-button"
            :data-testid="'cap-inspect-' + keySlug(item.key)"
            @click="inspect(item)"
          >
            详情
          </button>
          <span class="cap-rev" :title="item.descriptorRevision">版本 {{ item.descriptorRevision.slice(0, 8) }}</span>
        </footer>
      </article>
    </div>

    <div v-if="nextCursor" class="cap-more">
      <button type="button" class="tonal" data-testid="cap-load-more" :disabled="loading" @click="loadMore">
        {{ loading ? '加载中…' : '加载下一页' }}
      </button>
      <p class="cap-more-hint">目录超过一页；游标由后端签发。</p>
    </div>

    <section v-if="selected" class="panel cap-detail" data-testid="cap-detail" aria-labelledby="cap-detail-title">
      <div class="panel-heading">
        <h2 id="cap-detail-title">{{ capabilityTitle(selected.displayName, selected.key) }}</h2>
        <p>{{ kindLabel(selected.key.kind) }} · {{ keyLabel(selected.key) }}</p>
      </div>
      <p v-if="selectedBusy" class="empty-state">正在读取描述符…</p>
      <ErrorNotice :error="selectedError" title="查看描述符失败" />
      <dl class="cap-meta">
        <div><dt>运行时</dt><dd>{{ runtimeLabel(selected.runtimeState) }}</dd></div>
        <div><dt>开关</dt><dd>{{ desiredLabel(selected.desiredEnabled) }}</dd></div>
        <div v-if="selected.key.kind !== 'plugin'"><dt>凭据</dt><dd>{{ selected.credentialConfigured ? '已配置' : '未配置' }}</dd></div>
        <div><dt>可执行动作</dt><dd>{{ supportedActions(selected).map((a) => actionLabel(a.action)).join('、') || '无' }}</dd></div>
      </dl>
      <button type="button" class="link-button" data-testid="cap-detail-close" @click="closeDetail">收起</button>
    </section>

    <section
      v-if="activeRecord"
      class="panel cap-result"
      data-testid="cap-result"
      aria-labelledby="cap-result-title"
    >
      <div class="panel-heading">
        <h2 id="cap-result-title">动作回执 · {{ actionLabel(activeRecord.action) }}</h2>
        <p>{{ activeRecord.kindLabel }} · {{ activeRecord.label }}</p>
      </div>
      <p v-if="activeRecord.phase === 'preparing'" class="inline-notice" data-testid="cap-result-preparing">
        正在确认描述符版本，还没有发出动作。
      </p>
      <p v-else-if="activeRecord.phase === 'submitting'" class="inline-notice" data-testid="cap-result-submitting">
        已提交，正在等待服务端受理…
      </p>
      <p v-else-if="activeRecord.checking" class="inline-notice" data-testid="cap-result-checking">
        正在核对描述符；这是同一次操作的重发，命令与版本沿用第一次提交时的值。
      </p>
      <dl class="cap-meta">
        <div><dt>结果码</dt><dd data-testid="cap-result-code">{{ activeRecord.result ? activeRecord.result.code : '—' }}</dd></div>
        <div v-if="activeRecord.result && activeRecord.result.receipt">
          <dt>状态</dt>
          <dd>
            <span
              class="status-badge"
              :data-tone="stateTone(activeRecord.result.receipt.state)"
              data-testid="cap-result-state"
            >
              {{ stateLabel(activeRecord.result.receipt.state) }}
            </span>
          </dd>
        </div>
        <div v-if="activeRecord.result && activeRecord.result.receipt && activeRecord.result.receipt.errorCode">
          <dt>错误</dt><dd>{{ activeRecord.result.receipt.errorCode }}</dd>
        </div>
        <div v-if="activeRecord.cancelledState !== null">
          <dt>取消请求</dt>
          <dd data-testid="cap-cancel-state">{{ stateLabel(activeRecord.cancelledState) }}</dd>
        </div>
      </dl>
      <p
        v-if="activeRecord.phase === 'unknown'"
        class="inline-notice warn"
        data-testid="cap-result-unknown"
      >
        没有回执：动作结果未知，操作键已保留。请先核对；重试会沿用同一个操作键，服务端不会把它当成第二次执行。
      </p>
      <ErrorNotice :error="activeRecord.error" title="动作没有完成" />
      <p v-if="dismissNotice" class="inline-notice warn" data-testid="cap-result-pinned">
        {{ dismissNotice }}
      </p>
      <div class="dialog-actions">
        <button
          v-if="canQuery(activeRecord)"
          type="button"
          class="tonal"
          data-testid="cap-result-query"
          :disabled="activeRecord.busy"
          @click="queryRecord(activeRecord)"
        >
          {{ activeRecord.busy ? '核对中…' : '核对结果' }}
        </button>
        <button
          v-if="canQuery(activeRecord)"
          type="button"
          class="tonal"
          data-testid="cap-result-cancel"
          :disabled="activeRecord.busy || !canWrite"
          @click="cancelRecord(activeRecord)"
        >
          请求取消
        </button>
        <button
          v-if="canRetry(activeRecord) && pendingAction === null"
          type="button"
          class="primary"
          data-testid="cap-result-retry"
          :disabled="activeRecord.busy || !canWrite"
          @click="retryRecord(activeRecord)"
        >
          {{ activeRecord.busy ? '提交中…' : '用同一操作键重试' }}
        </button>
        <button type="button" class="link-button" data-testid="cap-result-close" @click="closeResult">关闭</button>
      </div>
      <p class="cap-op-key">操作键 <code data-testid="cap-op-key">{{ activeRecord.key }}</code></p>
    </section>

    <section v-if="waitingRecords.length > 0" class="panel cap-ops" data-testid="cap-pending-list">
      <div class="panel-heading">
        <h2>其它待核对操作</h2>
        <p>这些操作还没有终态；开始新动作不会丢掉它们的操作键。</p>
      </div>
      <ul class="cap-ops-items">
        <li v-for="(record, index) in waitingRecords" :key="record.key" :data-testid="'cap-pending-' + index">
          <span class="cap-op-desc">{{ actionLabel(record.action) }} · {{ record.kindLabel }} · {{ record.label }}</span>
          <span class="cap-op-state">
            {{ record.phase === 'submitting' || record.phase === 'preparing' ? '提交中' : record.result && record.result.receipt ? stateLabel(record.result.receipt.state) : '结果未知' }}
          </span>
          <button
            type="button"
            class="link-button"
            :data-testid="'cap-pending-open-' + index"
            @click="focusRecord(record)"
          >
            查看
          </button>
        </li>
      </ul>
    </section>

    <PluginSettingsDialog
      v-if="settingsTarget"
      :key="settingsTarget.spec.pluginKey"
      :client="props.client"
      :mode="props.mode"
      :writes-enabled="props.writesEnabled"
      :spec="settingsTarget.spec"
      :display-name="settingsTarget.displayName"
      @close="closeSettings"
      @enable-admin="emit('enable-admin')"
      @auth-expired="(e) => emit('auth-expired', e)"
    />

    <Teleport to="body">
      <div v-if="pendingAction" class="dialog-backdrop" data-testid="cap-confirm">
        <section
          ref="confirmDialog"
          class="dialog"
          role="dialog"
          aria-modal="true"
          aria-labelledby="cap-confirm-title"
          @keydown="onConfirmKeydown"
        >
          <h2 id="cap-confirm-title">确认{{ actionLabel(pendingAction.capability.action) }}</h2>
          <p class="dialog-copy">
            目标：<strong>{{ keyLabel(pendingAction.descriptor.key) }}</strong>
            （{{ kindLabel(pendingAction.descriptor.key.kind) }}）
          </p>
          <p v-if="isDangerous(pendingAction.capability.action)" class="inline-notice warn">
            这是危险动作：会影响正在运行的能力，执行结果以服务端回执为准。
          </p>
          <p v-else class="dialog-copy">动作会提交给服务端，结果未知时会保留操作键供核对。</p>
          <p v-if="pendingAction.reuseKey" class="inline-notice warn" data-testid="cap-confirm-reuse">
            这个动作上次结果未知；确认后会沿用同一个操作键，服务端不会重复执行。
          </p>
          <p v-if="context === null" class="inline-notice warn" data-testid="cap-confirm-no-context">
            还没有读到配置代际，现在不能提交动作；按 Esc 或"取消"退出这个确认框。
          </p>
          <p v-if="actionBusy" class="inline-notice warn" data-testid="cap-confirm-busy">
            上一个动作还在提交，现在不能确认新的动作；按 Esc 或"取消"可以退出这个确认框，
            上一个动作结束后再回来确认即可。
          </p>
          <ErrorNotice :error="actionError" title="动作失败" />
          <div class="dialog-actions">
            <button
              ref="confirmCancelButton"
              type="button"
              class="tonal"
              data-testid="cap-confirm-cancel"
              @click="cancelAction"
            >
              取消
            </button>
            <button
              ref="confirmButton"
              type="button"
              class="primary"
              data-testid="cap-confirm-ok"
              :disabled="actionBusy || !canWrite || context === null"
              @click="confirmAction"
            >
              {{ actionBusy ? '上一个动作提交中…' : '确认执行' }}
            </button>
          </div>
        </section>
      </div>
    </Teleport>
  </section>
</template>
