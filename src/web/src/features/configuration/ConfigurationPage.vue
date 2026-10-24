<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref } from 'vue'
import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaConfigurationReceipt,
  type ZbaConfigurationReadResult,
  type ZbaConfigurationView,
  type ZbaFieldDescriptor,
  type ZbaReadField
} from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { acquireScheduler } from '../../shared/sync/event-scheduler'
import {
  baselineRaw,
  buildPatch,
  buildSecrets,
  conflictingPaths,
  createOperationKey,
  draftRaw,
  editableField,
  fieldPath,
  isDirty,
  isEditable,
  newNonce,
  nowSeconds,
  valueKind,
  type DraftMap,
  type SecretAction,
  type SecretDrafts
} from './draft'
import { fieldLabel } from './field-labels'

const props = defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
}>()

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const sectionMeta = [
  { key: 'behavior', label: '行为', hint: '触发、人格与冷却' },
  { key: 'model', label: '模型', hint: '模型、采样与思考预算' },
  { key: 'agent', label: 'Agent', hint: '服务器 Agent 与工具' },
  { key: 'platform', label: '平台', hint: '通道、白名单与策略' },
  { key: 'voice', label: '语音', hint: 'TTS 与语音参数' },
  { key: 'music', label: '音乐', hint: '点歌、解析与缓存' },
  { key: 'reporting', label: '报告', hint: '健康日报' },
  { key: 'registered', label: '已登记', hint: '插件与模型供应商' }
] as const

type SectionKey = typeof sectionMeta[number]['key']
type SavePhase = 'idle' | 'saving' | 'unconfirmed' | 'saved' | 'conflict' | 'error'

const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)
const loadedAt = ref<string | null>(null)
const active = ref<SectionKey>('behavior')

const draft = ref<DraftMap>({})
const secretDrafts = ref<SecretDrafts>({})
const phase = ref<SavePhase>('idle')
const saveError = ref<UiError | null>(null)
const receipt = ref<ZbaConfigurationReceipt | null>(null)
const fieldErrors = ref<string[]>([])
const validationMessage = ref<string | null>(null)
const conflictCurrentVersion = ref<string | null>(null)
const conflictKeys = ref<string[]>([])
const pendingKey = ref<string | null>(null)
const findBusy = ref(false)

type LeaveReason = 'lock' | 'downgrade'
const leavePrompt = ref<LeaveReason | null>(null)
const leaveMessage = ref<string | null>(null)
const leaveBusy = ref(false)
const leaveSafeButton = ref<HTMLButtonElement | null>(null)
const leaveDialog = ref<HTMLElement | null>(null)
let leaveResolve: ((allowed: boolean) => void) | null = null
let leavePromise: Promise<boolean> | null = null

let loadSequence = 0

const counts = computed<Record<SectionKey, number>>(() => {
  const result: Record<SectionKey, number> = {
    behavior: 0, model: 0, agent: 0, platform: 0, voice: 0, music: 0, reporting: 0, registered: 0
  }
  const view = snapshot.value?.view
  if (!view) return result
  for (const key of ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting'] as const) {
    result[key] = Object.keys(view[key]).length
  }
  result.registered = view.registered.plugins.length + view.registered.providers.length
  return result
})

const fieldCount = computed(() => snapshot.value?.fields.length ?? 0)

const registered = computed(() => snapshot.value?.view?.registered ?? null)

const activeMeta = computed(() => sectionMeta.find((section) => section.key === active.value) ?? sectionMeta[0])

const activeFields = computed<Array<{ key: string; field: ZbaReadField }>>(() => {
  const view: ZbaConfigurationView | undefined = snapshot.value?.view
  if (!view || active.value === 'registered') return []
  const section = view[active.value] as Record<string, ZbaReadField>
  return Object.entries(section).map(([key, field]) => ({ key, field }))
})

const dirtyCount = computed(() =>
  Object.keys(draft.value).length
  + Object.values(secretDrafts.value).filter((action) => action !== undefined && action.kind !== 'keep').length)

const canWrite = computed(() => props.mode === 'admin' && props.writesEnabled)
const busy = computed(() => loading.value || phase.value === 'saving')
/** 提交结果未知时冻结整批内容：既不能改草稿，也不能放弃核对入口。 */
const unconfirmed = computed(() => phase.value === 'unconfirmed')
const inputsLocked = computed(() => busy.value || unconfirmed.value || phase.value === 'conflict')
const saveBlocked = computed(() => busy.value || unconfirmed.value || phase.value === 'conflict')
const hasDraft = computed(() => dirtyCount.value > 0)

const leaveTitle = computed(() => leavePrompt.value === 'lock'
  ? '锁定前确认未保存修改'
  : '退出管理员模式前确认未保存修改')
const leaveCopy = computed(() => leavePrompt.value === 'lock'
  ? '锁定会结束当前会话并清除内存中的草稿与秘密输入。'
  : '退出管理员模式后写入口关闭；草稿会保留，可以重新进入管理员模式后继续。')
const leaveSaveLabel = computed(() => leavePrompt.value === 'lock' ? '保存并锁定' : '保存并退出管理员')
const leaveDiscardLabel = computed(() => leavePrompt.value === 'lock' ? '放弃并锁定' : '放弃并退出管理员')

const formatter = new Intl.DateTimeFormat('zh-CN', {
  dateStyle: 'medium',
  timeStyle: 'medium',
  hour12: false
})

const applyLabels: Record<number, string> = {
  0: '未开始', 1: '已生效', 2: '部分生效', 3: '需重启', 4: '失败', 5: '未知'
}
const applyTones: Record<number, string> = {
  0: 'neutral', 1: 'ok', 2: 'warn', 3: 'warn', 4: 'bad', 5: 'warn'
}
const effectLabels: Record<number, string> = {
  0: '热更新', 1: '需重启', 2: '外部投影', 3: '只读字段'
}
const sourceLabels: Record<number, string> = {
  0: '默认', 1: '用户配置', 2: '环境变量', 3: '旧配置兼容', 4: '未分类'
}
const userModeLabels: Record<number, string> = {
  0: '未设置', 1: '继承', 2: '显式'
}
const credentialLabels: Record<number, string> = {
  0: '未配置', 1: '主模型凭据', 2: '已登记凭据', 3: '未分类'
}
const secretSourceLabels: Record<number, string> = {
  0: '未配置', 1: '数据库', 2: '环境变量'
}
const secretSlotLabels: Record<number, string> = {
  0: '模型 API Key', 1: 'Agent 服务器密钥', 2: 'TTS API Key',
  3: '飞书 App Secret', 4: '飞书 Encrypt Key', 5: '飞书验证 Token'
}
const commitStateLabels: Record<number, string> = {
  0: '未提交', 1: '已提交', 2: '提交结果未知'
}
const stepLabels: Record<number, string> = {
  0: '发布快照', 1: '运行时策略', 2: '模型投影', 3: '并发门',
  4: '上下文窗口', 5: '调度器', 6: 'TTS 文件投影', 7: '已登记配置'
}
const saveCodeMessages: Record<number, string> = {
  1: '当前令牌没有保存权限。',
  2: '服务端校验失败，请查看字段错误。',
  3: '配置已被其他人修改，需要先读取最新快照。',
  4: '同一操作键携带了不同内容，提交被拒绝。',
  5: '操作键已过期，请重新提交。',
  6: '代际已变化，请重新读取配置后再提交。',
  7: '提交容量已满，请稍后再试。',
  8: '服务器时钟回退，提交被拒绝。',
  9: '配置存储暂不可用。',
  10: '提交失败，未确认写入。',
  11: '提交在写入前被取消。',
  12: '提交结果未知，请核对。',
  13: '没有实际变更。',
  14: '没有找到该操作回执。',
  15: '旧配置需要人工复核。',
  16: '契约版本不受支持。'
}

function applyLabel(value: number): string {
  return applyLabels[value] ?? `未知状态 ${value}`
}

function applyTone(value: number): string {
  return applyTones[value] ?? 'warn'
}

function effectLabel(value: number): string {
  return effectLabels[value] ?? `未知影响 ${value}`
}

function sourceLabel(value: number): string {
  return sourceLabels[value] ?? `未知来源 ${value}`
}

function userModeLabel(value: number): string {
  return userModeLabels[value] ?? `未知模式 ${value}`
}

function secretSourceLabel(value: number): string {
  return secretSourceLabels[value] ?? `未知来源 ${value}`
}

function secretSlotLabel(value: number): string {
  return secretSlotLabels[value] ?? `槽位 ${value}`
}

function commitStateLabel(value: number): string {
  return commitStateLabels[value] ?? `未知提交状态 ${value}`
}

function formatRaw(value: unknown): string {
  if (value === null || value === undefined) return '—'
  if (typeof value === 'string') return value.length === 0 ? '（空）' : value
  if (typeof value === 'number') return Number.isFinite(value) ? String(value) : '—'
  if (typeof value === 'boolean') return value ? '是' : '否'
  if (Array.isArray(value)) {
    if (value.length === 0) return '[]'
    if (value.length <= 2) return value.map((item) => formatRaw(item)).join('、')
    return `${value.length} 项`
  }
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function formatValue(field: ZbaReadField): string {
  if (field.visibility === 0) return '已隐藏'
  if (field.visibility === 2) return field.nameRaw ?? '—'
  return formatRaw(field.value)
}

function valueTitle(field: ZbaReadField): string | undefined {
  if (field.visibility !== 1 || field.value === undefined) return undefined
  try {
    const text = JSON.stringify(field.value)
    return text && text.length > 400 ? `${text.slice(0, 400)}…` : text
  } catch {
    return undefined
  }
}

function formatTime(value: string): string {
  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? value : formatter.format(date)
}

function fieldRule(rule: ZbaFieldDescriptor): string {
  const parts = [effectLabel(rule.effect), rule.canClear ? '可清除' : '不可清除']
  if (rule.legacyNeedsReview) parts.push('待复核')
  return parts.join(' · ')
}

function inputValue(event: Event): string {
  return (event.target as HTMLInputElement | HTMLSelectElement).value
}

function currentDraftRaw(section: SectionKey, key: string, field: ZbaReadField): string {
  return draftRaw(draft.value, fieldPath(section as ZbaPatchSectionLike, key), field)
}

type ZbaPatchSectionLike = 'behavior' | 'model' | 'agent' | 'platform' | 'voice' | 'music' | 'reporting'

function isFieldDirty(section: SectionKey, key: string, field: ZbaReadField): boolean {
  return isDirty(draft.value, fieldPath(section as ZbaPatchSectionLike, key), field)
}

function onFieldInput(section: SectionKey, key: string, raw: string): void {
  if (inputsLocked.value) return
  const view = snapshot.value?.view
  if (!view) return
  const field = editableField(view, section as ZbaPatchSectionLike, key)
  if (!field) return
  const path = fieldPath(section as ZbaPatchSectionLike, key)
  const next: DraftMap = { ...draft.value }
  if (raw === baselineRaw(field)) delete next[path]
  else next[path] = { kind: 'set', raw }
  draft.value = next
  phase.value = 'idle'
  validationMessage.value = null
}

function clearField(section: SectionKey, key: string): void {
  if (inputsLocked.value) return
  draft.value = { ...draft.value, [fieldPath(section as ZbaPatchSectionLike, key)]: { kind: 'clear' } }
  phase.value = 'idle'
  validationMessage.value = null
}

function resetField(section: SectionKey, key: string): void {
  if (inputsLocked.value) return
  const next: DraftMap = { ...draft.value }
  delete next[fieldPath(section as ZbaPatchSectionLike, key)]
  draft.value = next
}

function resetAll(): void {
  // “提交结果未知”时先核对：丢弃草稿会同时丢掉唯一的回执查询入口。
  if (unconfirmed.value) return
  draft.value = {}
  secretDrafts.value = {}
  fieldErrors.value = []
  validationMessage.value = null
  conflictCurrentVersion.value = null
  conflictKeys.value = []
  pendingKey.value = null
  phase.value = 'idle'
  saveError.value = null
}

function secretAction(slot: number): SecretAction | undefined {
  return secretDrafts.value[slot as 0 | 1 | 2 | 3 | 4 | 5]
}

function secretInputValue(slot: number): string {
  const action = secretAction(slot)
  return action?.kind === 'replace' ? action.value : ''
}

function setSecretAction(slot: number, action: SecretAction): void {
  if (inputsLocked.value) return
  secretDrafts.value = { ...secretDrafts.value, [slot]: action }
  phase.value = 'idle'
  fieldErrors.value = []
}

function cancelSecret(slot: number): void {
  if (inputsLocked.value) return
  const next: SecretDrafts = { ...secretDrafts.value }
  delete next[slot as 0 | 1 | 2 | 3 | 4 | 5]
  secretDrafts.value = next
}

function onSecretInput(slot: number, raw: string): void {
  setSecretAction(slot, { kind: 'replace', value: raw })
}

function buildRequest(): { changes: ReturnType<typeof buildPatch>['patch']; secrets: ReturnType<typeof buildSecrets>['secrets']; errors: string[] } | null {
  const view = snapshot.value?.view
  if (!view) return null
  const patch = buildPatch(draft.value, view)
  const secrets = buildSecrets(secretDrafts.value)
  const errors = [...patch.errors, ...secrets.errors]
  return { changes: patch.patch, secrets: secrets.secrets, errors }
}

/**
 * 配置链路上的 401 表示整个管理会话失效，而不是单次请求失败。
 * 交给父级统一锁门，避免本页继续显示管理员模式并留着秘密输入。
 */
function reportAuthFailure(cause: unknown): boolean {
  if (cause instanceof ZbaRequestError && cause.status === 401) {
    emit('auth-expired', toUiError(cause))
    return true
  }
  return false
}

/**
 * 读取配置基线。
 *
 * `mode='background'` 是事件驱动的补读：**只更新基线快照，绝不碰草稿**
 * （计划要求后台把 status/配置重读一遍，同时保留各自的草稿与错误语义）。
 * 返回 false 表示这次读取确实失败，调用方（调度器）据此不签"已追平"。
 */
async function reloadBaseline(mode: 'interactive' | 'background' = 'interactive'): Promise<boolean> {
  const sequence = ++loadSequence
  loading.value = true
  error.value = null
  try {
    const next = await props.client.readConfiguration(1)
    if (sequence !== loadSequence) return true
    // 代际变化：旧写计划作废（计划 §9.3 第 242 行）。草稿保留，由用户决定。
    if (mode === 'background' && snapshot.value !== null
      && snapshot.value.generation !== next.generation) {
      phase.value = 'idle'
      pendingKey.value = null
    }
    snapshot.value = next
    loadedAt.value = formatTime(new Date().toISOString())
    return true
  } catch (cause) {
    if (sequence !== loadSequence) return true
    // 认证失效也是"没读到"：不能签已追平。
    if (reportAuthFailure(cause)) return false
    error.value = toUiError(cause)
    return false
  } finally {
    if (sequence === loadSequence) loading.value = false
  }
}

// ── 共享事件同步（计划 §9.3：配置也是五类补读之一） ──────────────────
const detachScheduler = ref<(() => void) | null>(null)

function attachScheduler(client: ZbaClient): void {
  detachScheduler.value?.()
  const { scheduler, release } = acquireScheduler(client)
  const off = scheduler.subscribe({
    name: 'configuration',
    matches: (event) => event.type === 'configuration.saved',
    reload: async () => {
      if (!(await reloadBaseline('background'))) throw new Error('configuration_read_failed')
    }
  })
  detachScheduler.value = () => {
    off()
    release()
  }
}

/** 重新读取基线但保留草稿；用于 409 冲突或代际变化后的显式“读取最新”。 */
async function reloadAfterConflict(): Promise<void> {
  conflictCurrentVersion.value = null
  phase.value = 'idle'
  pendingKey.value = null
  await reload()
  if (snapshot.value?.view) {
    conflictKeys.value = conflictingPaths(draft.value, snapshot.value.view)
  }
  if (conflictKeys.value.length > 0) {
    validationMessage.value = `已读取最新快照；${conflictKeys.value.length} 个字段与对方修改重叠，请逐项确认。`
  } else {
    validationMessage.value = '已读取最新快照，草稿与最新值没有重叠字段。'
  }
}

async function validate(): Promise<void> {
  const request = buildRequest()
  if (!request || !snapshot.value) return
  fieldErrors.value = request.errors
  if (request.errors.length > 0) {
    validationMessage.value = null
    return
  }
  phase.value = 'saving'
  saveError.value = null
  validationMessage.value = '校验中…'
  try {
    const result = await props.client.validateConfiguration({
      contractRevision: snapshot.value.contractRevision,
      expectedVersion: snapshot.value.version,
      changes: request.changes,
      secrets: request.secrets
    })
    if (result.code === 0) {
      validationMessage.value = '校验通过；保存时服务端还会再校验一次。'
      phase.value = 'idle'
      return
    }
    fieldErrors.value = result.errors.map((item) => `字段 ${item.address.field}：${item.code}`)
    validationMessage.value = result.code === 5
      ? '版本已变化，请先读取最新快照。'
      : `校验未通过（code ${result.code}）。`
    phase.value = 'idle'
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    phase.value = 'error'
    saveError.value = toUiError(cause)
  }
}

async function save(): Promise<void> {
  if (!canWrite.value || !snapshot.value) return
  // 单飞：保存中不能再发第二个写请求（包括离开对话框的“保存并离开”）。
  if (phase.value === 'saving') return
  // 结果未知时先核对；冲突时先读取最新基线。两者都不允许再发一次写请求。
  if (unconfirmed.value || phase.value === 'conflict') return
  const request = buildRequest()
  if (!request) return
  fieldErrors.value = request.errors
  if (request.errors.length > 0) return
  if (request.changes && Object.keys(request.changes).length === 0 && request.secrets.length === 0) {
    validationMessage.value = '没有需要保存的修改。'
    return
  }

  // 每次新的用户意图都用新操作键；旧键只属于“结果未知”的那一次提交。
  const key = createOperationKey(snapshot.value.generation, nowSeconds(), newNonce())
  pendingKey.value = key
  phase.value = 'saving'
  saveError.value = null
  validationMessage.value = null
  conflictCurrentVersion.value = null
  conflictKeys.value = []

  try {
    const result = await props.client.saveConfiguration({
      contractRevision: snapshot.value.contractRevision,
      expectedVersion: snapshot.value.version,
      key,
      changes: request.changes,
      secrets: request.secrets
    })
    // 服务端明确回报“结果未知”（12）：保留操作键与草稿，冻结后先核对。
    if (result.code === 12) {
      receipt.value = result.receipt ?? null
      phase.value = 'unconfirmed'
      validationMessage.value = null
      return
    }
    // 成功码必须带回执才能证明落盘；缺回执的 2xx 仍属未知，不能清草稿。
    if ((result.code === 0 || result.code === 13) && result.receipt === undefined) {
      phase.value = 'unconfirmed'
      validationMessage.value = '成功码缺少回执：无法确认此次提交已落盘。草稿与操作键已保留，请先核对。'
      return
    }
    // 明确的完整响应结束“未知”。
    pendingKey.value = null
    if (result.code === 0 || result.code === 13) {
      receipt.value = result.receipt ?? null
      phase.value = 'saved'
      draft.value = {}
      secretDrafts.value = {}
      fieldErrors.value = []
      await reload()
      return
    }
    if (result.code === 3) {
      conflictCurrentVersion.value = result.currentVersion
      phase.value = 'conflict'
      return
    }
    fieldErrors.value = result.errors.map((item) => `字段 ${item.address.field}：${item.code}`)
    phase.value = 'error'
    saveError.value = {
      code: `save_${result.code}`,
      message: saveCodeMessages[result.code] ?? `保存失败（code ${result.code}）。`
    }
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    // 传输失败，或服务端已回 2xx 但响应体无法解码：都不能证明“没提交”。
    const outcomeUnknown = cause instanceof ZbaNetworkError
      || (cause instanceof ZbaRequestError
        && cause.code === 'invalid_json'
        && cause.status >= 200
        && cause.status < 300)
    if (outcomeUnknown) {
      phase.value = 'unconfirmed'
      return
    }
    pendingKey.value = null
    phase.value = 'error'
    saveError.value = toUiError(cause)
  }
}

/**
 * 响应丢失后只核对、不重发。
 *
 * 只有状态码与业务码构成契约配对的响应才会改变状态机。
 *
 * - 找到回执：确认已提交，清空草稿。
 * - NotFound：这次提交没有落盘；清掉旧键，编辑与保存恢复，下次保存使用新键。
 * - 代际已变化：旧键无法再核对；清掉旧键并重读快照，让用户按实际值确认。
 * - 其它错误/网络异常：结果仍然未知，保留旧键和横幅。
 */
async function checkPending(): Promise<void> {
  const key = pendingKey.value
  if (key === null || findBusy.value) return
  findBusy.value = true
  validationMessage.value = '核对中…'
  saveError.value = null
  try {
    const result = await props.client.findConfiguration(key)
    if (result.code === 0 && result.receipt) {
      receipt.value = result.receipt
      phase.value = 'saved'
      pendingKey.value = null
      draft.value = {}
      secretDrafts.value = {}
      validationMessage.value = null
      await reload()
      return
    }
    if (result.code === 14) {
      pendingKey.value = null
      phase.value = 'idle'
      validationMessage.value = '没有找到这次提交的回执：修改没有落盘。草稿已保留，请确认内容后重新保存（会使用新的操作键）。'
      return
    }
    if (result.code === 6) {
      pendingKey.value = null
      phase.value = 'idle'
      validationMessage.value = '配置代际已变化，旧操作键无法再核对；已重新读取快照，请确认字段实际值后再保存。'
      await reload()
      return
    }
    if (result.code === 5) {
      // 操作键 24 小时后过期，服务端不再接受核对；留着旧键只会把页面永久锁死。
      pendingKey.value = null
      phase.value = 'idle'
      validationMessage.value = '操作键已过期（超过 24 小时），无法再核对回执；已重新读取快照，请按实际值确认后重新保存（会使用新的操作键）。'
      await reload()
      return
    }
    saveError.value = {
      code: `find_${result.code}`,
      message: `核对失败（code ${result.code}）；提交结果仍然未知，操作键与草稿已保留。`
    }
  } catch (cause) {
    if (reportAuthFailure(cause)) return
    saveError.value = toUiError(cause)
    validationMessage.value = '核对未完成：这次响应不能当作权威结果（HTTP 状态与结果码矛盾，或请求本身失败）。'
      + '操作键与草稿继续保留，可以稍后用同一个键再次核对。'
  } finally {
    findBusy.value = false
    if (validationMessage.value === '核对中…') validationMessage.value = null
  }
}

/** 从其他页返回时重读基线；草稿与待核对操作键都保留。 */
async function refreshOnEnter(): Promise<void> {
  await reload()
}

/**
 * 离开配置页或锁定会话前的守卫。
 *
 * 返回 true 表示允许离开；false 表示用户选择继续编辑。同一时间只有一个
 * 待决对话框，重复调用复用同一个 Promise，避免出现两个遮罩。
 */
function requestLeave(reason: LeaveReason): Promise<boolean> {
  if (!hasDraft.value && pendingKey.value === null) return Promise.resolve(true)
  if (leavePromise) return leavePromise
  leavePrompt.value = reason
  leaveMessage.value = null
  leaveBusy.value = false
  leavePromise = new Promise<boolean>((resolve) => {
    leaveResolve = resolve
  })
  void nextTick(() => leaveSafeButton.value?.focus())
  return leavePromise
}

function closeLeave(allowed: boolean): void {
  leavePrompt.value = null
  leaveMessage.value = null
  leaveBusy.value = false
  leaveResolve?.(allowed)
  leaveResolve = null
  leavePromise = null
}

function leaveStay(): void {
  closeLeave(false)
}

function leaveDiscard(): void {
  // 未知状态不允许丢下核对入口；保存中丢弃草稿也会让在途提交失去上下文。
  if (unconfirmed.value || busy.value) return
  resetAll()
  closeLeave(true)
}

async function leaveSave(): Promise<void> {
  if (leaveBusy.value || busy.value || unconfirmed.value || !canWrite.value) return
  leaveBusy.value = true
  await save()
  leaveBusy.value = false
  if (phase.value === 'saved') {
    closeLeave(true)
    return
  }
  leaveMessage.value = unconfirmed.value
    ? '保存结果未知：请先核对回执，再决定是否离开。'
    : '保存没有完成；已留在本页，请先处理页面上的提示。'
}

/** 模态内的焦点循环；Esc 等于“继续编辑”（安全动作）。 */
function onLeaveKeydown(event: KeyboardEvent): void {
  if (leavePrompt.value === null) return
  if (event.key === 'Escape') {
    event.preventDefault()
    leaveStay()
    return
  }
  if (event.key !== 'Tab') return
  const root = leaveDialog.value
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

function handleBeforeUnload(event: BeforeUnloadEvent): void {
  if (!hasDraft.value) return
  event.preventDefault()
  event.returnValue = ''
}

/**
 * 离开配置视图时丢弃内存中的秘密输入，普通字段草稿继续保留。
 *
 * 未确认 / 冲突 / 保存中（inputsLocked）时秘密输入属于尚未结束的提交上下文，
 * 清掉会让“核对/重存”少一块内容；这些状态下本就不允许编辑秘密槽位。
 */
function discardSecretsOnLeave(): void {
  if (inputsLocked.value) return
  if (Object.keys(secretDrafts.value).length === 0) return
  secretDrafts.value = {}
}

/** 会话结束（锁定、401、换令牌）时立即清空内存草稿与秘密输入。 */
function endSession(): void {
  detachScheduler.value?.()
  detachScheduler.value = null
  if (leaveResolve !== null) closeLeave(false)
  draft.value = {}
  secretDrafts.value = {}
  pendingKey.value = null
  phase.value = 'idle'
  receipt.value = null
  fieldErrors.value = []
  validationMessage.value = null
  conflictCurrentVersion.value = null
  conflictKeys.value = []
  saveError.value = null
  snapshot.value = null
  loadedAt.value = null
  error.value = null
}

onMounted(() => {
  window.addEventListener('beforeunload', handleBeforeUnload)
  attachScheduler(props.client)
  void reloadBaseline()
})

onBeforeUnmount(() => {
  window.removeEventListener('beforeunload', handleBeforeUnload)
  detachScheduler.value?.()
  detachScheduler.value = null
  secretDrafts.value = {}
})

/** 用户显式「重新读取」：交互式语义（撤下旧基线），不接受参数。 */
function reload(): Promise<boolean> {
  return reloadBaseline('interactive')
}

defineExpose({
  // App 的「刷新」按钮与模板里的按钮都调 reload()：无参、可当点击处理器。
  reload,
  refreshOnEnter,
  requestLeave,
  discardSecretsOnLeave,
  endSession,
  draftCount: dirtyCount
})
</script>

<template>
  <section class="configuration" data-testid="configuration" aria-labelledby="configuration-title">
    <header class="page-heading">
      <div>
        <p class="eyebrow">配置</p>
        <h1 id="configuration-title">配置快照</h1>
        <p class="subtitle">
          版本 <strong>{{ snapshot?.version ?? '—' }}</strong>
          · 契约 {{ snapshot?.contractRevision ?? '—' }}
          <template v-if="loadedAt"> · 读取于 {{ loadedAt }}</template>
        </p>
      </div>
      <div class="heading-actions">
        <span class="mode-badge" :data-mode="props.mode" data-testid="config-mode-badge">
          {{ props.mode === 'admin' ? '管理员模式' : '标准模式' }}
        </span>
        <button type="button" class="tonal" data-testid="config-reload" :disabled="busy" @click="reload()">
          {{ loading ? '读取中…' : '重新读取' }}
        </button>
      </div>
    </header>

    <p v-if="props.mode !== 'admin'" class="inline-notice" data-testid="config-readonly">
      标准模式只能查看快照；保存草稿与秘密编辑需要先进入管理员模式。
      <button
        type="button"
        class="tonal inline-action"
        data-testid="config-enable-admin"
        :disabled="!props.writesEnabled"
        @click="emit('enable-admin')"
      >
        进入管理员模式
      </button>
    </p>
    <p v-else-if="!props.writesEnabled" class="inline-notice bad" data-testid="config-write-disabled">
      契约不兼容，写入口已禁用；本页只展示当前快照。
    </p>

    <p v-if="phase === 'conflict'" class="inline-notice bad" data-testid="config-conflict" role="alert">
      <strong>配置已被其他人修改（当前版本 {{ conflictCurrentVersion ?? '未知' }}）。</strong>
      你的草稿仍然保留，编辑暂时锁定；读取最新快照后才能确认哪些字段重叠。
      <button type="button" class="tonal inline-action" data-testid="config-reload-conflict" @click="reloadAfterConflict">
        读取最新并比较
      </button>
    </p>

    <p v-if="phase === 'unconfirmed'" class="inline-notice warn" data-testid="config-unconfirmed" role="status">
      <strong>提交结果未知（网络中断、超时或服务端未确认）。</strong>
      编辑与保存已锁定，草稿和操作键都保留；先用同一个操作键核对服务端回执，再决定下一步。
      <button
        type="button"
        class="tonal inline-action"
        data-testid="config-check-pending"
        :disabled="findBusy"
        @click="checkPending"
      >
        {{ findBusy ? '核对中…' : '核对结果' }}
      </button>
    </p>

    <ErrorNotice :error="error" title="读取配置失败" />
    <ErrorNotice :error="saveError" title="保存配置失败" />
    <ul v-if="fieldErrors.length > 0" class="field-errors" data-testid="config-field-errors">
      <li v-for="item in fieldErrors" :key="item">{{ item }}</li>
    </ul>
    <p v-if="validationMessage" class="inline-notice" data-testid="config-validation">{{ validationMessage }}</p>

    <p v-if="loading && !snapshot" class="empty-state" data-testid="config-loading">正在读取配置快照…</p>
    <p v-else-if="!snapshot" class="empty-state" data-testid="config-empty">还没有配置快照；请重新读取。</p>

    <template v-else>
      <section class="config-summary" aria-label="配置摘要">
        <div>
          <span>版本</span>
          <strong>{{ snapshot.version }}</strong>
        </div>
        <div>
          <span>代际</span>
          <strong class="mono">{{ snapshot.generation }}</strong>
        </div>
        <div>
          <span>字段</span>
          <strong>{{ fieldCount }}</strong>
        </div>
        <div>
          <span>最近生效</span>
          <span class="status-badge" :data-tone="applyTone(snapshot.latestApplyState)">
            {{ applyLabel(snapshot.latestApplyState) }}
          </span>
        </div>
      </section>

      <div class="config-body">
        <nav class="config-nav" aria-label="配置分区">
          <button
            v-for="section in sectionMeta"
            :key="section.key"
            type="button"
            class="config-nav-item"
            :class="{ active: active === section.key }"
            :aria-current="active === section.key ? 'true' : undefined"
            :data-testid="`config-nav-${section.key}`"
            @click="active = section.key"
          >
            <span>{{ section.label }}</span>
            <span class="config-count">{{ counts[section.key] }}</span>
          </button>
        </nav>

        <section
          v-if="active !== 'registered'"
          class="panel config-panel"
          :aria-labelledby="`config-${active}-title`"
        >
          <div class="panel-heading">
            <h2 :id="`config-${active}-title`">{{ activeMeta.label }}</h2>
            <p>{{ activeMeta.hint }} · {{ activeFields.length }} 个字段</p>
          </div>
          <p v-if="activeFields.length === 0" class="empty-state">该分区没有字段。</p>
          <div v-else class="table-scroll">
            <table data-testid="config-fields">
              <caption class="sr-only">{{ activeMeta.label }}字段</caption>
              <thead>
                <tr>
                  <th scope="col">字段</th>
                  <th scope="col">当前值 / 编辑</th>
                  <th scope="col">来源</th>
                  <th scope="col">生效</th>
                  <th scope="col">规则</th>
                </tr>
              </thead>
              <tbody>
                <tr
                  v-for="item in activeFields"
                  :key="item.key"
                  :class="{ dirty: isFieldDirty(active, item.key, item.field) }"
                  :data-testid="`config-field-${item.key}`"
                >
                  <td>
                    <strong class="config-label">{{ fieldLabel(item.key) }}</strong>
                    <span class="config-key">{{ item.key }}</span>
                    <span v-if="isFieldDirty(active, item.key, item.field)" class="cell-sub">已修改</span>
                  </td>
                  <td class="config-value">
                    <template v-if="canWrite && isEditable(item.field)">
                      <select
                        v-if="valueKind(item.field) === 'boolean'"
                        class="config-input"
                        :value="currentDraftRaw(active, item.key, item.field)"
                        :aria-label="`${fieldLabel(item.key)}（${item.key}）`"
                        :disabled="inputsLocked"
                        @change="onFieldInput(active, item.key, inputValue($event))"
                      >
                        <option value="true">是</option>
                        <option value="false">否</option>
                      </select>
                      <input
                        v-else
                        class="config-input"
                        :type="valueKind(item.field) === 'number' ? 'number' : 'text'"
                        :value="currentDraftRaw(active, item.key, item.field)"
                        :aria-label="`${fieldLabel(item.key)}（${item.key}）`"
                        :disabled="inputsLocked"
                        @input="onFieldInput(active, item.key, inputValue($event))"
                      />
                      <span class="edit-actions">
                        <button
                          v-if="isFieldDirty(active, item.key, item.field)"
                          type="button"
                          class="link-button"
                          :disabled="inputsLocked"
                          @click="resetField(active, item.key)"
                        >
                          还原
                        </button>
                        <button
                          v-if="item.field.rule.canClear"
                          type="button"
                          class="link-button"
                          :disabled="inputsLocked"
                          @click="clearField(active, item.key)"
                        >
                          清除
                        </button>
                      </span>
                    </template>
                    <span v-else :title="valueTitle(item.field)">{{ formatValue(item.field) }}</span>
                    <span v-if="conflictKeys.includes(`${active}.${item.key}`)" class="cell-sub conflict-flag">
                      与对方修改重叠
                    </span>
                  </td>
                  <td>
                    <span>{{ sourceLabel(item.field.effectiveSource) }}</span>
                    <span class="cell-sub">{{ userModeLabel(item.field.userMode) }}</span>
                  </td>
                  <td>
                    <span class="status-badge" :data-tone="applyTone(item.field.applyState)">
                      {{ applyLabel(item.field.applyState) }}
                    </span>
                  </td>
                  <td>
                    <span>{{ fieldRule(item.field.rule) }}</span>
                    <span class="cell-sub">{{ item.field.visibility === 0 ? '仅概览' : '可读' }}</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>

        <section v-else class="panel config-panel" aria-labelledby="config-registered-title">
          <div class="panel-heading">
            <h2 id="config-registered-title">已登记</h2>
            <p v-if="registered">
              {{ registered.plugins.length }} 个插件 ·
              {{ registered.providers.length }} 个供应商
            </p>
          </div>

          <p v-if="!registered" class="empty-state" data-testid="config-registered-empty">
            快照没有返回已登记信息。
          </p>
          <template v-else>
            <h3 class="subhead">插件</h3>
            <p v-if="registered.plugins.length === 0" class="empty-state">没有已登记插件。</p>
            <div v-else class="table-scroll">
              <table data-testid="config-plugins">
                <caption class="sr-only">已登记插件</caption>
                <thead>
                  <tr><th scope="col">插件</th><th scope="col">期望状态</th></tr>
                </thead>
                <tbody>
                  <tr v-for="plugin in registered.plugins" :key="plugin.key">
                    <td><strong>{{ plugin.key }}</strong></td>
                    <td>{{ plugin.desiredEnabled ? '启用' : '停用' }}</td>
                  </tr>
                </tbody>
              </table>
            </div>

            <h3 class="subhead">模型供应商</h3>
            <p v-if="registered.providers.length === 0" class="empty-state">没有已登记供应商。</p>
            <div v-else class="table-scroll">
              <table data-testid="config-providers">
                <caption class="sr-only">已登记模型供应商</caption>
                <thead>
                  <tr>
                    <th scope="col">供应商</th>
                    <th scope="col">名称</th>
                    <th scope="col">Base URL</th>
                    <th scope="col">模型</th>
                    <th scope="col">优先级</th>
                    <th scope="col">凭据</th>
                    <th scope="col">运行时</th>
                  </tr>
                </thead>
                <tbody>
                  <tr v-for="provider in registered.providers" :key="provider.key">
                    <td><strong>{{ provider.key }}</strong></td>
                    <td>{{ formatValue(provider.name) }}</td>
                    <td class="config-value">{{ formatValue(provider.baseUrl) }}</td>
                    <td>{{ formatValue(provider.modelName) }}</td>
                    <td>{{ provider.priority }}</td>
                    <td>{{ credentialLabels[provider.credential] ?? provider.credential }}</td>
                    <td>
                      <span class="status-badge" :data-tone="applyTone(provider.runtimeApplyState)">
                        {{ applyLabel(provider.runtimeApplyState) }}
                      </span>
                    </td>
                  </tr>
                </tbody>
              </table>
            </div>
          </template>
        </section>
      </div>

      <section class="panel" aria-labelledby="config-secrets-title">
        <div class="panel-heading">
          <h2 id="config-secrets-title">秘密槽位</h2>
          <p>只显示是否存在与来源，后端不返回秘密值。</p>
        </div>
        <ul class="secret-list" data-testid="config-secrets">
          <li v-for="secret in snapshot.secrets" :key="secret.slot">
            <div class="secret-head">
              <span class="secret-slot">{{ secretSlotLabel(secret.slot) }}</span>
              <span class="status-badge" :data-tone="secret.configured ? 'ok' : 'neutral'">
                {{ secret.configured ? '已配置' : '未配置' }}
              </span>
              <span class="secret-source">{{ secretSourceLabel(secret.source) }}</span>
            </div>
            <div v-if="canWrite" class="secret-actions">
              <template v-if="secretAction(secret.slot)?.kind === 'replace'">
                <input
                  class="config-input"
                  type="password"
                  autocomplete="off"
                  :aria-label="`替换 ${secretSlotLabel(secret.slot)}`"
                  :value="secretInputValue(secret.slot)"
                  :disabled="inputsLocked"
                  @input="onSecretInput(secret.slot, inputValue($event))"
                />
                <button type="button" class="link-button" :disabled="inputsLocked" @click="cancelSecret(secret.slot)">取消</button>
              </template>
              <template v-else-if="secretAction(secret.slot)?.kind === 'delete'">
                <span class="cell-sub">已标记删除</span>
                <button type="button" class="link-button" :disabled="inputsLocked" @click="cancelSecret(secret.slot)">撤销</button>
              </template>
              <template v-else>
                <button
                  type="button"
                  class="link-button"
                  data-testid="secret-replace"
                  :disabled="inputsLocked"
                  @click="setSecretAction(secret.slot, { kind: 'replace', value: '' })"
                >
                  替换
                </button>
                <button
                  v-if="secret.configured"
                  type="button"
                  class="link-button"
                  data-testid="secret-delete"
                  :disabled="inputsLocked"
                  @click="setSecretAction(secret.slot, { kind: 'delete' })"
                >
                  删除
                </button>
              </template>
            </div>
          </li>
        </ul>
      </section>

      <section v-if="receipt" class="panel" aria-labelledby="config-receipt-title" data-testid="config-receipt">
        <div class="panel-heading">
          <h2 id="config-receipt-title">最近一次提交</h2>
          <p>
            {{ receipt.decision === 0 ? '有变更' : '无变更' }} ·
            持久化版本 {{ receipt.committedVersion ?? '—' }}
          </p>
        </div>
        <dl class="receipt-grid">
          <div>
            <dt>操作键</dt>
            <dd class="mono">{{ receipt.key }}</dd>
          </div>
          <div>
            <dt>提交状态</dt>
            <dd>{{ commitStateLabel(receipt.commitState) }}</dd>
          </div>
          <div>
            <dt>生效状态</dt>
            <dd>{{ applyLabel(receipt.applyState) }}</dd>
          </div>
        </dl>
        <ol v-if="receipt.steps.length > 0" class="receipt-steps" data-testid="config-receipt-steps">
          <li v-for="step in receipt.steps" :key="step.step">
            {{ stepLabels[step.step] ?? `步骤 ${step.step}` }}：{{ applyLabel(step.state) }}
            <span v-if="step.errorCode" class="cell-sub">{{ step.errorCode }}</span>
          </li>
        </ol>
        <p v-if="receipt.applyState === 3" class="inline-notice warn">
          保存已提交，但需要重启后完全生效；首版不提供远程重启按钮。
        </p>
      </section>

      <div class="save-bar" data-testid="config-save-bar">
        <div class="save-bar-info">
          <strong>{{ dirtyCount }} 项修改</strong>
          <span v-if="phase === 'saving'">保存中…</span>
          <span v-else-if="phase === 'saved'">已保存</span>
          <span v-else-if="phase === 'unconfirmed'">提交结果未知 · 待核对</span>
          <span v-else-if="phase === 'conflict'">版本冲突 · 待读取</span>
          <span v-if="hasDraft" class="save-bar-hint">草稿仅在本页内存；刷新或关闭浏览器会先提示</span>
        </div>
        <div class="save-bar-actions">
          <button
            type="button"
            class="tonal"
            data-testid="config-discard"
            :disabled="busy || dirtyCount === 0 || unconfirmed"
            @click="resetAll"
          >
            放弃修改
          </button>
          <button
            type="button"
            data-testid="config-validate"
            :disabled="saveBlocked || dirtyCount === 0"
            @click="validate"
          >
            校验
          </button>
          <button
            type="button"
            class="primary"
            data-testid="config-save"
            :disabled="saveBlocked || dirtyCount === 0 || !canWrite"
            @click="save"
          >
            {{ phase === 'saving' ? '保存中…' : '保存' }}
          </button>
        </div>
      </div>

      <Teleport to="body">
        <div v-if="leavePrompt" class="dialog-backdrop" data-testid="config-leave-backdrop">
          <section
            ref="leaveDialog"
            class="dialog"
            role="dialog"
            aria-modal="true"
            aria-labelledby="config-leave-title"
            data-testid="config-leave-dialog"
            @keydown="onLeaveKeydown"
          >
            <h2 id="config-leave-title">{{ leaveTitle }}</h2>
            <p class="dialog-copy">
              {{ leaveCopy }}
              当前有 <strong>{{ dirtyCount }}</strong> 项修改。
            </p>
            <p v-if="phase === 'saving'" class="inline-notice" data-testid="config-leave-saving">
              保存正在提交；请等本次结果返回后再保存、放弃或离开。
            </p>
            <p v-if="unconfirmed" class="inline-notice warn" data-testid="config-leave-unconfirmed">
              提交结果未知：请先核对回执。核对完成前不能离开或锁定。
            </p>
            <p v-else-if="!canWrite && hasDraft" class="inline-notice">
              标准模式下不能保存；可以继续编辑，或明确放弃后再离开。
            </p>
            <p v-if="leaveMessage" class="inline-notice bad" data-testid="config-leave-message">{{ leaveMessage }}</p>
            <div class="dialog-actions">
              <button
                ref="leaveSafeButton"
                type="button"
                class="tonal"
                data-testid="config-leave-stay"
                @click="leaveStay"
              >
                继续编辑
              </button>
              <button
                type="button"
                data-testid="config-leave-save"
                :disabled="!canWrite || leaveBusy || busy || unconfirmed"
                @click="leaveSave"
              >
                {{ leaveBusy ? '保存中…' : leaveSaveLabel }}
              </button>
              <button
                type="button"
                class="danger-ghost"
                data-testid="config-leave-discard"
                :disabled="unconfirmed || busy"
                @click="leaveDiscard"
              >
                {{ leaveDiscardLabel }}
              </button>
            </div>
          </section>
        </div>
      </Teleport>
    </template>
  </section>
</template>
