<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, useId, watch } from 'vue'
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
import { isRestorable, restoreFocus } from '../../shared/ui/focus-restore'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { acquireScheduler } from '../../shared/sync/event-scheduler'
import {
  applyProviderNames,
  baselineRaw,
  buildPatch,
  buildSecrets,
  conflictingPaths,
  conflictingProviderNames,
  createOperationKey,
  draftRaw,
  editableField,
  fieldPath,
  isDirty,
  isEditable,
  newNonce,
  nowSeconds,
  providerNameBaseline,
  valueKind,
  type DraftMap,
  type ProviderNameDrafts,
  type SecretAction,
  type SecretDrafts
} from './draft'
import { fieldLabel } from './field-labels'
import { configurationFieldErrors, localFieldErrors, providerErrorPath, type ConfigurationFieldError } from './field-errors'
import { beginOperation, getPendingOperation, markOperation, pendingOperations } from '../../shared/sync/pending-operations'
import { configurationFor, ownsConfiguration } from '../capabilities/configuration-ownership'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  scope?: string
  active?: boolean
}>(), { scope: 'basic', active: true })

const editorId = useId()
const domId = (key: string): string => editorId + '-' + key

const emit = defineEmits<{
  'enable-admin': []
  'auth-expired': [error: UiError]
  'open-capabilities': []
}>()

const sectionMeta = [
  { key: 'behavior', label: '行为', hint: '触发、人格与冷却', guide: '决定机器人什么时候开口、用什么人设说话，以及两次回复之间要冷却多久。改动保存后对新消息立即生效。' },
  { key: 'model', label: '模型', hint: '模型、采样与思考预算', guide: '选择聊天用的模型与采样参数。温度越高回复越发散；思考预算越大越慢但更周到。' },
  { key: 'agent', label: 'Agent', hint: '服务器 Agent 与工具', guide: '控制服务器内置 Agent：能否接 // 指令或自然对话任务、最多走几步、能用哪些工具。' },
  { key: 'platform', label: '平台', hint: '通道、白名单与策略', guide: '接入哪些聊天通道、谁能和机器人说话（白名单），以及群聊与私聊的处理策略。' },
  { key: 'voice', label: '语音', hint: 'TTS 与语音参数', guide: '云端语音合成的开关、音色与语速。' },
  { key: 'music', label: '音乐', hint: '点歌、解析与缓存', guide: '点歌后端地址、解析方式与缓存策略。' },
  { key: 'reporting', label: '报告', hint: '健康日报', guide: '每天自动生成健康日报的时间与接收方式。' },
  { key: 'registered', label: '模型供应商', hint: '已登记的模型供应商', guide: '模型供应商的名称与密钥槽位。密钥只能替换或删除，不会回显。' }
] as const

/** 插件相关设置挪到「能力」页的插件卡片上；整个分区归插件的不再出现在导航里。 */
const capability = computed(() => configurationFor(props.scope))
const editorTitle = computed(() => props.scope === 'basic' ? '基础配置' : capability.value?.title ?? '能力配置')
const visibleSections = computed(() => sectionMeta.filter(section => section.key === 'registered'
  ? capability.value?.providers === true
  : props.scope === 'basic' ? section.key === 'behavior' : capability.value?.sections.includes(section.key) === true))
const visibleSecrets = computed(() => (snapshot.value?.secrets ?? []).filter(secret => capability.value?.secrets.some(slot => slot === secret.slot)))
function ownedByPlugin(section: string, key: string): boolean {
  return !ownsConfiguration(props.scope, section, key)
}

type SectionKey = typeof sectionMeta[number]['key']
type SavePhase = 'idle' | 'saving' | 'unconfirmed' | 'saved' | 'conflict' | 'error'

const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const loading = ref(false)
const error = ref<UiError | null>(null)
const loadedAt = ref<string | null>(null)
const active = ref<SectionKey>((visibleSections.value[0]?.key ?? 'behavior') as SectionKey)

const draft = ref<DraftMap>({})
/**
 * 草稿起草时所依据的快照（CAS 基准）。
 *
 * 后台补读 / 切页重读会替换 `snapshot`，但草稿是在旧快照上写的：保存与校验
 * 必须带**起草时**的版本作为 expectedVersion，否则别人刚保存的同一字段会被
 * 静默覆盖（409 永远不会触发）。只有用户在冲突后显式"读取最新"才会改基。
 */
const draftBase = ref<ZbaConfigurationReadResult | null>(null)
const secretDrafts = ref<SecretDrafts>({})
const secretReconfirm = ref<Record<number, boolean>>({})
const secretConfirmationBlocked = computed(() => Object.values(secretReconfirm.value).some(confirmed => !confirmed)
  || Object.entries(secretReconfirm.value).some(([slot]) => {
    const action = secretDrafts.value[Number(slot) as 0 | 1 | 2 | 3 | 4 | 5]
    return action?.kind === 'replace' && action.value.length === 0
  }))
/**
 * 供应商改名草稿（S4-L1）。
 *
 * 单独一张表：供应商的 name 字段是 visibility=2 的「可改名投影」，
 * 它不是标量分区里的普通字段，塞进 DraftMap 会污染 fieldPath 的语义。
 */
const providerNames = ref<ProviderNameDrafts>({})
/** 本次操作确认的名称意图，包含已与基线同值、无需序列化的名称。 */
const pendingNames = ref<ProviderNameDrafts>({})
let findSequence = 0
const phase = ref<SavePhase>('idle')
const saveError = ref<UiError | null>(null)
const receipt = ref<ZbaConfigurationReceipt | null>(null)
const fieldErrors = ref<ConfigurationFieldError[]>([])
const validationMessage = ref<string | null>(null)
const invalidPaths = computed(() => new Set(fieldErrors.value.flatMap(item => item.path ? [item.path] : [])))
function fieldInvalid(section: string, key: string): boolean {
  return invalidPaths.value.has(`${section}.${key}`)
}
const conflictCurrentVersion = ref<string | null>(null)
const conflictKeys = ref<string[]>([])
const pendingKey = ref<string | null>(null)
const findBusy = ref(false)
const operationTarget = computed(() => 'config:' + props.scope)
const registeredOperation = computed(() => getPendingOperation(props.client, 'configuration', operationTarget.value))
const restoredOperation = ref(false)
let sessionRevision = 0
watch(() => pendingOperations(props.client, 'configuration', operationTarget.value).map(item => item.key + ':' + item.state).join('|'), () => {
  const record = registeredOperation.value
  if (record && pendingKey.value === null) {
    pendingKey.value = record.key
    restoredOperation.value = true
    phase.value = 'unconfirmed'
    validationMessage.value = '已恢复原操作键；只核对回执，不会重新提交。秘密输入已清除。'
  } else if (!record && (restoredOperation.value || (pendingKey.value !== null
    && externallySettled(props.client, pendingKey.value, operationTarget.value)))) {
    pendingKey.value = null
    restoredOperation.value = false
    phase.value = 'conflict'
    validationMessage.value = '先前操作已结束，请读取最新快照后继续。'
  }
}, { immediate: true })

type LeaveReason = 'lock' | 'downgrade' | 'close'
const leavePrompt = ref<LeaveReason | null>(null)
const leaveMessage = ref<string | null>(null)
const leaveBusy = ref(false)
const leaveSafeButton = ref<HTMLButtonElement | null>(null)
const leaveDialog = ref<HTMLElement | null>(null)
/** 页面标题：触发器已经不在了时的安全落点（tabindex="-1"，不进 Tab 顺序）。 */
const pageHeading = ref<HTMLElement | null>(null)
let leaveResolve: ((allowed: boolean) => void) | null = null
let leavePromise: Promise<boolean> | null = null
/** 打开当前这个离开框的按钮（S4-M1）：关闭后要把焦点还回去。 */
let leaveTrigger: HTMLElement | null = null
/**
 * 离开框的代际（S4-M3）。
 *
 * 每次 requestLeave 推进一次。异步的"保存并离开"在 await 之后必须核对代际：
 * 用户可能在保存途中关掉这个框、又开了一个新的离开框，迟到的保存结果
 * 绝不能替用户**批准**那个还没人选择过的新框。
 */
let leaveGeneration = 0

let loadSequence = 0

const counts = computed<Record<SectionKey, number>>(() => {
  const result: Record<SectionKey, number> = {
    behavior: 0, model: 0, agent: 0, platform: 0, voice: 0, music: 0, reporting: 0, registered: 0
  }
  const view = snapshot.value?.view
  if (!view) return result
  for (const key of ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting'] as const) {
    result[key] = Object.keys(view[key]).filter((field) => !ownedByPlugin(key, field)).length
  }
  result.registered = capability.value?.providers ? view.registered.providers.length : 0
  return result
})

const fieldCount = computed(() => Object.values(counts.value).reduce((sum, count) => sum + count, 0))

const registered = computed(() => snapshot.value?.view?.registered ?? null)

const activeMeta = computed(() => sectionMeta.find((section) => section.key === active.value) ?? sectionMeta[0])

const activeFields = computed<Array<{ key: string; field: ZbaReadField }>>(() => {
  const view: ZbaConfigurationView | undefined = snapshot.value?.view
  if (!view || active.value === 'registered') return []
  const section = view[active.value] as Record<string, ZbaReadField>
  return Object.entries(section)
    .filter(([key]) => !ownedByPlugin(active.value, key))
    .map(([key, field]) => ({ key, field }))
})

const dirtyCount = computed(() =>
  Object.keys(draft.value).length
  + Object.keys(providerNames.value).length
  + Object.values(secretDrafts.value).filter((action) => action !== undefined && action.kind !== 'keep').length)

const hasWritePermission = computed(() => props.mode === 'admin' && props.writesEnabled)
const canWrite = computed(() => props.active && hasWritePermission.value)
// Only an explicit leave confirmation may save a retained draft while its editor is hidden.
const canSaveOnLeave = computed(() => leavePrompt.value !== null && hasWritePermission.value)
const busy = computed(() => loading.value || phase.value === 'saving')
/** 提交结果未知时冻结整批内容：既不能改草稿，也不能放弃核对入口。 */
const unconfirmed = computed(() => phase.value === 'unconfirmed')
const inputsLocked = computed(() => busy.value || unconfirmed.value || phase.value === 'conflict')
const saveBlocked = computed(() => busy.value || unconfirmed.value || phase.value === 'conflict' || secretConfirmationBlocked.value)
const hasDraft = computed(() => dirtyCount.value > 0)
// 草稿全部清空后释放起草基线：下一次起草锚定当时的快照。
watch(hasDraft, (value) => { if (!value) draftBase.value = null })

/** 开始（或继续）起草前调用：没有草稿时锚定当前快照作为 CAS 基准。 */
function anchorDraft(): void {
  if (!hasDraft.value || draftBase.value === null) draftBase.value = snapshot.value
}

function verifiedReceipt(value: ZbaConfigurationReceipt | undefined, key: string): value is ZbaConfigurationReceipt {
  return value !== undefined && value.key === key && value.commitState !== 2 && (value.decision === 1 || value.commitState === 1)
}
function externallySettled(client: ZbaClient, key: string, target: string): boolean {
  return pendingOperations(client, 'configuration', target).some(record => record.key === key && record.state === 'settled')
}

/** 保存/校验用的期望版本：起草基线优先。 */
function expectedVersion(): string {
  return (draftBase.value ?? snapshot.value)?.version ?? ''
}

const leaveTitle = computed(() => leavePrompt.value === 'close' ? '关闭前确认未保存修改' : leavePrompt.value === 'lock'
  ? '锁定前确认未保存修改'
  : '退出管理员模式前确认未保存修改')
const leaveCopy = computed(() => leavePrompt.value === 'close' ? '关闭能力设置前，可以保存修改或明确放弃草稿。' : leavePrompt.value === 'lock'
  ? '锁定会结束当前会话并清除内存中的草稿与秘密输入。'
  : '退出管理员模式后写入口关闭；草稿会保留，可以重新进入管理员模式后继续。')
const leaveSaveLabel = computed(() => leavePrompt.value === 'close' ? '保存并关闭' : leavePrompt.value === 'lock' ? '保存并锁定' : '保存并退出管理员')
const leaveDiscardLabel = computed(() => leavePrompt.value === 'close' ? '放弃并关闭' : leavePrompt.value === 'lock' ? '放弃并锁定' : '放弃并退出管理员')

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
  if (!canWrite.value || inputsLocked.value || !ownsConfiguration(props.scope, section, key)) return
  const view = snapshot.value?.view
  if (!view) return
  const field = editableField(view, section as ZbaPatchSectionLike, key)
  if (!field) return
  const path = fieldPath(section as ZbaPatchSectionLike, key)
  anchorDraft()
  const next: DraftMap = { ...draft.value }
  if (raw === baselineRaw(field)) delete next[path]
  else next[path] = { kind: 'set', raw }
  draft.value = next
  phase.value = 'idle'
  validationMessage.value = null
}

function clearField(section: SectionKey, key: string): void {
  if (!canWrite.value || inputsLocked.value || !ownsConfiguration(props.scope, section, key)) return
  anchorDraft()
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
  secretReconfirm.value = {}
  providerNames.value = {}
  fieldErrors.value = []
  validationMessage.value = null
  conflictCurrentVersion.value = null
  conflictKeys.value = []
  pendingKey.value = null
  pendingNames.value = {}
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
  if (!canWrite.value || inputsLocked.value || !capability.value?.secrets.some(owned => owned === slot)) return
  anchorDraft()
  secretDrafts.value = { ...secretDrafts.value, [slot]: action }
  phase.value = 'idle'
  fieldErrors.value = []
}

function cancelSecret(slot: number): void {
  if (inputsLocked.value) return
  const next: SecretDrafts = { ...secretDrafts.value }
  delete next[slot as 0 | 1 | 2 | 3 | 4 | 5]
  secretDrafts.value = next
  const confirmations = { ...secretReconfirm.value }
  delete confirmations[slot]
  secretReconfirm.value = confirmations
}

function onSecretInput(slot: number, raw: string): void {
  setSecretAction(slot, { kind: 'replace', value: raw })
}

function buildRequest(): {
  changes: ReturnType<typeof buildPatch>['patch']
  secrets: ReturnType<typeof buildSecrets>['secrets']
  errors: string[]
  nameIntents: ProviderNameDrafts
} | null {
  const view = snapshot.value?.view
  if (!view) return null
  const patch = buildPatch(draft.value, view)
  const nameErrors = applyProviderNames(patch.patch, providerNames.value, view)
  const secrets = buildSecrets(secretDrafts.value)
  if (secretConfirmationBlocked.value) secrets.errors.push('秘密值无法比较：请明确确认，并重新输入替换值。')
  const errors = [...patch.errors, ...nameErrors, ...secrets.errors]
  // 意图与 patch 分开记账：同值名称不用写入，但本次成功仍应结束该意图。
  const nameIntents = { ...providerNames.value }
  return { changes: patch.patch, secrets: secrets.secrets, errors, nameIntents }
}

/** 供应商改名输入（S4-L1）：只接受真名，不接受脱敏展示串。 */
function providerNameInput(key: string, raw: string): void {
  if (!canWrite.value || inputsLocked.value || !capability.value?.providers) return
  anchorDraft()
  const next: ProviderNameDrafts = { ...providerNames.value }
  if (raw === providerNameBaseline(providerField(key))) delete next[key]
  else next[key] = raw
  providerNames.value = next
  phase.value = 'idle'
  validationMessage.value = null
}

const EMPTY_NAME_FIELD: ZbaReadField = {
  visibility: 0,
  display: '',
  rule: { id: 0, effect: 0, canClear: false, editable: false, legacyNeedsReview: false },
  userMode: 0,
  effectiveSource: 0,
  applyState: 0
}

function providerField(key: string): ZbaReadField {
  const view = snapshot.value?.view
  if (!view) return EMPTY_NAME_FIELD
  const provider = view.registered.providers.find((item) => item.key === key)
  return provider?.name ?? EMPTY_NAME_FIELD
}

function providerNameRaw(key: string): string {
  return providerNames.value[key] ?? providerNameBaseline(providerField(key))
}

function isProviderDirty(key: string): boolean {
  return Object.prototype.hasOwnProperty.call(providerNames.value, key)
}

/** 只结束本次操作所确认且值未改变的意图，不让旧回执删除新的名称。 */
function settleProviderNames(intents: ProviderNameDrafts): void {
  const next: ProviderNameDrafts = { ...providerNames.value }
  for (const [key, raw] of Object.entries(intents)) {
    if (next[key] === raw) delete next[key]
  }
  providerNames.value = next
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
    if (sequence !== loadSequence) return false
    // 代际变化：旧写计划作废（计划 §9.3 第 242 行）。草稿保留，由用户决定。
    // 保存中 / 结果未知 / 冲突是尚未结束的提交上下文：不能悄悄改回 idle，
    // 否则迟到的保存结果被丢弃、核对入口消失、冲突提示被抹掉。
    // 结果未知的旧键交给"核对"去拿权威答复（代际已变化 = code 6）。
    if (mode === 'background' && snapshot.value !== null
      && snapshot.value.generation !== next.generation) {
      const open = phase.value === 'saving' || phase.value === 'unconfirmed' || phase.value === 'conflict'
      if (!open) {
        phase.value = 'idle'
        pendingKey.value = null
        pendingNames.value = {}
      }
      if (hasDraft.value) {
        validationMessage.value = '配置代际已变化（服务重启或重新加载）。草稿已保留；保存时会先按起草时的版本核对，有冲突会提示你读取最新。'
      }
    }
    snapshot.value = next
    loadedAt.value = formatTime(new Date().toISOString())
    return true
  } catch (cause) {
    if (sequence !== loadSequence) return false
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
  if (loading.value) return
  const base = draftBase.value?.view ?? null
  if (!(await reload()) || !snapshot.value?.view) return
  conflictCurrentVersion.value = null
  phase.value = 'idle'
  pendingKey.value = null
  pendingNames.value = {}
  const reconfirm: Record<number, boolean> = {}
  for (const [slot, action] of Object.entries(secretDrafts.value)) {
    if (!action || action.kind === 'keep') continue
    reconfirm[Number(slot)] = false
    if (action.kind === 'replace') action.value = ''
  }
  secretReconfirm.value = reconfirm
  if (snapshot.value?.view) {
    const next = snapshot.value.view
    conflictKeys.value = [
      ...conflictingPaths(draft.value, base, next),
      ...conflictingProviderNames(providerNames.value, base, next).map((key) => `provider:${key}`)
    ]
    // 用户已看到最新快照并逐项确认：从这里起草稿改基到新版本。
    draftBase.value = hasDraft.value ? snapshot.value : null
  }
  if (Object.keys(secretReconfirm.value).length > 0) {
    validationMessage.value = '已读取最新快照；秘密值无法比较，可能存在同槽位修改。请逐项明确确认，并重新输入替换值后再保存。'
  } else if (conflictKeys.value.length > 0) {
    validationMessage.value = `已读取最新快照；${conflictKeys.value.length} 个字段与对方修改重叠，请逐项确认。`
  } else {
    validationMessage.value = '已读取最新快照，草稿与最新值没有重叠字段。'
  }
}

async function validate(): Promise<void> {
  const request = buildRequest()
  if (!request || !snapshot.value) return
  fieldErrors.value = localFieldErrors(request.errors)
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
      expectedVersion: expectedVersion(),
      changes: request.changes,
      secrets: request.secrets
    })
    if (result.code === 0) {
      validationMessage.value = '校验通过；保存时服务端还会再校验一次。'
      phase.value = 'idle'
      return
    }
    fieldErrors.value = configurationFieldErrors(result.errors, snapshot.value?.view)
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

async function save(): Promise<'unchanged' | void> {
  return commitDraft(false)
}

async function commitDraft(fromLeaveConfirmation: boolean): Promise<'unchanged' | void> {
  if (!(fromLeaveConfirmation ? canSaveOnLeave.value : canWrite.value) || !snapshot.value) return
  // 单飞：保存中不能再发第二个写请求（包括离开对话框的“保存并离开”）。
  if (phase.value === 'saving') return
  // 结果未知时先核对；冲突时先读取最新基线。两者都不允许再发一次写请求。
  if (unconfirmed.value || phase.value === 'conflict') return
  const request = buildRequest()
  if (!request) return
  fieldErrors.value = localFieldErrors(request.errors)
  if (request.errors.length > 0) return
  if (request.changes && Object.keys(request.changes).length === 0 && request.secrets.length === 0) {
    settleProviderNames(request.nameIntents)
    validationMessage.value = '没有需要保存的修改。'
    return 'unchanged'
  }

  // 每次新的用户意图都用新操作键；旧键只属于“结果未知”的那一次提交。
  const client = props.client
  const target = operationTarget.value
  const revision = sessionRevision
  const key = createOperationKey(snapshot.value.generation, nowSeconds(), newNonce())
  try { beginOperation(client, { key, kind: 'configuration', target }) }
  catch { saveError.value = { code: 'pending_operation_exists', message: '此配置仍有未决操作，请先核对原操作键。' }; return }
  restoredOperation.value = false
  pendingKey.value = key
  pendingNames.value = request.nameIntents
  phase.value = 'saving'
  saveError.value = null
  validationMessage.value = null
  conflictCurrentVersion.value = null
  conflictKeys.value = []

  try {
    const result = await client.saveConfiguration({
      contractRevision: snapshot.value.contractRevision,
      expectedVersion: expectedVersion(),
      key,
      changes: request.changes,
      secrets: request.secrets
    })
    if (externallySettled(client, key, target)) return
    const unknown = result.code === 12 || ((result.code === 0 || result.code === 13) && !verifiedReceipt(result.receipt, key))
    markOperation(client, key, unknown ? 'unknown' : 'settled')
    if (revision !== sessionRevision || pendingKey.value !== key) return
    // 服务端明确回报“结果未知”（12）：保留操作键与草稿，冻结后先核对。
    if (result.code === 12) {
      receipt.value = result.receipt ?? null
      phase.value = 'unconfirmed'
      validationMessage.value = null
      return
    }
    // 成功码必须带回执才能证明落盘；缺回执的 2xx 仍属未知，不能清草稿。
    if ((result.code === 0 || result.code === 13) && !verifiedReceipt(result.receipt, key)) {
      phase.value = 'unconfirmed'
      validationMessage.value = '成功码缺少有效回执：无法确认此次提交已落盘。草稿与操作键已保留，请先核对。'
      return
    }
    // 明确的完整响应结束“未知”。
    pendingKey.value = null
    pendingNames.value = {}
    if (result.code === 0 || result.code === 13) {
      receipt.value = result.receipt ?? null
      phase.value = 'saved'
      draft.value = {}
      secretDrafts.value = {}
      secretReconfirm.value = {}
      // SR-NAME-01：名称意图也必须在这里结束，否则保存条一直是"1 项修改"，
      // 切页会弹未保存确认，下一次无关保存还会把已确认的旧名字重发给新基线。
      settleProviderNames(request.nameIntents)
      fieldErrors.value = []
      await reload()
      return
    }
    if (result.code === 3) {
      conflictCurrentVersion.value = result.currentVersion
      phase.value = 'conflict'
      return
    }
    fieldErrors.value = configurationFieldErrors(result.errors, snapshot.value?.view)
    phase.value = 'error'
    saveError.value = {
      code: `save_${result.code}`,
      message: saveCodeMessages[result.code] ?? `保存失败（code ${result.code}）。`
    }
  } catch (cause) {
    // Classify and record even after the editor was unmounted or the session was locked.
    const uncertain = cause instanceof ZbaNetworkError || (cause instanceof ZbaRequestError
      && (cause.status >= 500 || (cause.code === 'invalid_json' && cause.status >= 200 && cause.status < 300)))
    if (externallySettled(client, key, target)) return
    markOperation(client, key, uncertain ? 'unknown' : 'settled')
    if (revision !== sessionRevision || pendingKey.value !== key) return
    if (reportAuthFailure(cause)) return
    // 传输失败，或服务端已回 2xx 但响应体无法解码：都不能证明“没提交”。
    const outcomeUnknown = cause instanceof ZbaNetworkError
      || (cause instanceof ZbaRequestError
        && (cause.status >= 500
          || (cause.code === 'invalid_json' && cause.status >= 200 && cause.status < 300)))
    if (outcomeUnknown) {
      phase.value = 'unconfirmed'
      return
    }
    pendingKey.value = null
    pendingNames.value = {}
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
  if (key === null || findBusy.value || registeredOperation.value?.state === 'in-flight') return
  const client = props.client
  const revision = sessionRevision
  const target = operationTarget.value
  const nameIntents = pendingNames.value
  const sequence = ++findSequence
  findBusy.value = true
  validationMessage.value = '核对中…'
  saveError.value = null
  try {
    const result = await client.findConfiguration(key)
    if (externallySettled(client, key, target)) return
    const terminal = (result.code === 0 && verifiedReceipt(result.receipt, key)) || [14, 6, 5].includes(result.code)
    if (terminal) markOperation(client, key, 'settled')
    if (revision !== sessionRevision || pendingKey.value !== key) return
    if (terminal) restoredOperation.value = false
    if (result.code === 0 && verifiedReceipt(result.receipt, key)) {
      receipt.value = result.receipt
      phase.value = 'saved'
      pendingKey.value = null
      pendingNames.value = {}
      draft.value = {}
      secretDrafts.value = {}
      secretReconfirm.value = {}
      // 核对确认本次操作已落盘，包括无需重复写入的同值名称。
      settleProviderNames(nameIntents)
      validationMessage.value = null
      await reload()
      return
    }
    if (result.code === 14) {
      pendingKey.value = null
      pendingNames.value = {}
      phase.value = 'idle'
      validationMessage.value = '没有找到这次提交的回执：修改没有落盘。草稿已保留，请确认内容后重新保存（会使用新的操作键）。'
      return
    }
    if (result.code === 6 || result.code === 5) {
      pendingKey.value = null
      pendingNames.value = {}
      phase.value = 'conflict'
      validationMessage.value = result.code === 6
        ? '配置代际已变化，旧操作键无法再核对；请先读取最新快照。'
        : '操作键已过期（超过 24 小时），无法再核对回执；请先读取最新快照。'
      const recoveryMessage = validationMessage.value
      await reloadAfterConflict()
      validationMessage.value = recoveryMessage + (validationMessage.value ? ' ' + validationMessage.value : '')
      return
    }
    saveError.value = {
      code: `find_${result.code}`,
      message: `核对失败（code ${result.code}）；提交结果仍然未知，操作键与草稿已保留。`
    }
  } catch (cause) {
    if (externallySettled(client, key, target)) return
    if (revision !== sessionRevision || pendingKey.value !== key) return
    if (reportAuthFailure(cause)) return
    saveError.value = toUiError(cause)
    validationMessage.value = '核对未完成：这次响应不能当作权威结果（HTTP 状态与结果码矛盾，或请求本身失败）。'
      + '操作键与草稿继续保留，可以稍后用同一个键再次核对。'
  } finally {
    if (sequence !== findSequence) return
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
function requestLeave(reason: LeaveReason, trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') return Promise.resolve(true)
  if (!hasDraft.value && pendingKey.value === null) return Promise.resolve(true)
  if (leavePromise) return leavePromise
  leavePrompt.value = reason
  leaveMessage.value = null
  leaveBusy.value = false
  leaveGeneration += 1
  leaveTrigger = currentTrigger(trigger)
  leavePromise = new Promise<boolean>((resolve) => {
    leaveResolve = resolve
  })
  void nextTick(() => leaveSafeButton.value?.focus())
  return leavePromise
}

/**
 * 打开离开框的那个元素。
 *
 * 优先用调用方传下来的点击目标：WebKit 点按钮**不给**按钮焦点，
 * 只读 activeElement 会拿到上一个位置甚至 body，关框后就没地方还焦点。
 * 没传（例如概览页发出的"退出管理员"事件没有事件对象）才退回 activeElement。
 */
function currentTrigger(trigger?: HTMLElement | null): HTMLElement | null {
  if (trigger !== undefined && trigger !== null && isRestorable(trigger)) return trigger
  const active = document.activeElement
  return active instanceof HTMLElement && isRestorable(active) ? active : null
}

function closeLeave(allowed: boolean): void {
  const trigger = leaveTrigger
  leaveTrigger = null
  // 关闭即推进代际：任何还在 await 里的"保存并离开"从此不属于任何对话框。
  leaveGeneration += 1
  leavePrompt.value = null
  leaveMessage.value = null
  leaveBusy.value = false
  leaveResolve?.(allowed)
  leaveResolve = null
  leavePromise = null
  // 关闭后把焦点还给触发器（S4-M1）：否则键盘用户下一次 Tab 要从页面开头重走。
  // 页面可能正要切走（allowed=true），此时触发器仍在 DOM 里但马上会隐藏，
  // 恢复焦点只是给它一个确定的落点，不影响导航。
  void nextTick(() => {
    restoreFocus(trigger, pageHeading.value)
  })
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

/**
 * 保存并离开（S4-M3）。
 *
 * 关键约束：await 之后这个对话框可能已经不是当初那个了。
 * 用户可以在保存途中按"继续编辑"关掉它，再从导航打开一个新的离开框；
 * 迟到的保存结果如果直接调用 closeLeave()，就会替用户**批准**那个
 * 一个选择都没做过的新框，并把页面跳到别处。
 *
 * 所以进入时冻结代际，await 之后先核对：代际变了就只更新保存状态，
 * 一个新框的 resolver 都不许碰。
 */
async function leaveSave(): Promise<void> {
  if (leaveBusy.value || busy.value || unconfirmed.value || !canSaveOnLeave.value) return
  const generation = leaveGeneration
  leaveBusy.value = true
  const outcome = await commitDraft(true)
  // 代际变了 = 这个框已经被关掉、甚至被换成了新的一个。
  // 忙碌标记归新框自己管（它可能正在保存），保存结果也不许替它做决定。
  if (generation !== leaveGeneration) return
  leaveBusy.value = false
  if ((outcome === 'unchanged' || phase.value === 'saved') && !hasDraft.value && pendingKey.value === null) {
    closeLeave(true)
    return
  }
  leaveMessage.value = unconfirmed.value
    ? '保存结果未知：请先核对回执，再决定是否离开。'
    : '保存没有完成；已留在本页，请先处理页面上的提示。'
}

/**
 * 模态开着的时候，焦点必须一直留在框里（S4-M4）。
 *
 * 禁用当前焦点元素（例如「保存并锁定」提交期间按钮变 disabled）会让浏览器
 * 把焦点**直接丢回 body**，keydown 于是落在背景页面上，Escape 再也到不了模态——
 * 用户被卡在一个既不能保存也不能退出的框里。这和 S4-M2 是同一类缺陷：
 * 落点不可用，就等于模态接不住键盘。
 *
 * 所以只要框还在，焦点一旦跑到框外就把它拉回安全按钮。
 */
// flush: 'post' 不能省：默认的 pre 时机在**DOM 更新之前**跑，
// 那时按钮还没被禁用、焦点也还在它身上，判断会得出"一切正常"；
// 真正的失焦发生在紧随其后的 DOM 更新里，而 watcher 已经返回了。
watch(leavePrompt, (prompt) => {
  if (prompt === null) return
  void nextTick(() => leaveSafeButton.value?.focus())
}, { flush: 'post' })

watch([busy, unconfirmed, leaveBusy], () => {
  if (leavePrompt.value === null) return
  const active = document.activeElement
  const root = leaveDialog.value
  if (root !== null && active instanceof HTMLElement && root.contains(active) && isRestorable(active)) return
  void nextTick(() => leaveSafeButton.value?.focus())
}, { flush: 'post' })

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
  secretReconfirm.value = {}
}

/** 会话结束（锁定、401、换令牌）时立即清空内存草稿与秘密输入。 */
function endSession(): void {
  sessionRevision += 1
  restoredOperation.value = false
  loadSequence += 1
  findSequence += 1
  loading.value = false
  findBusy.value = false
  detachScheduler.value?.()
  detachScheduler.value = null
  if (leaveResolve !== null) closeLeave(false)
  draft.value = {}
  secretDrafts.value = {}
  secretReconfirm.value = {}
  providerNames.value = {}
  pendingKey.value = null
  pendingNames.value = {}
  phase.value = 'idle'
  receipt.value = null
  fieldErrors.value = []
  validationMessage.value = null
  conflictCurrentVersion.value = null
  conflictKeys.value = []
  saveError.value = null
  snapshot.value = null
  draftBase.value = null
  loadedAt.value = null
  error.value = null
}

watch(() => props.active, active => { if (!active) discardSecretsOnLeave() })
onMounted(() => {
  window.addEventListener('beforeunload', handleBeforeUnload)
  attachScheduler(props.client)
  void reloadBaseline()
})

onBeforeUnmount(() => {
  endSession()
  window.removeEventListener('beforeunload', handleBeforeUnload)
  detachScheduler.value?.()
  detachScheduler.value = null
  secretDrafts.value = {}
  secretReconfirm.value = {}
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
  draftCount: dirtyCount,
  hasUnresolvedIntent: computed(() => phase.value === 'saving' || phase.value === 'unconfirmed')
})
</script>

<template>
  <section class="configuration" :class="{ 'capability-editor': props.scope !== 'basic' }" data-testid="configuration" :aria-labelledby="domId('configuration-title')">
    <header class="page-heading">
      <div>
        <p class="eyebrow">系统管理 / 配置</p>
        <h1 :id="domId('configuration-title')" ref="pageHeading" tabindex="-1">{{ editorTitle }}</h1>
        <p class="page-description">{{ capability?.summary ?? '只保留对话总开关、人设、冷却、上下文与并发等基础设置。其它设置在对应能力中管理。' }}</p>
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
      <button type="button" class="tonal inline-action" data-testid="config-reload-conflict" :disabled="loading" @click="reloadAfterConflict">
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
        :disabled="findBusy || registeredOperation?.state === 'in-flight'"
        @click="checkPending"
      >
        {{ findBusy ? '核对中…' : '核对结果' }}
      </button>
    </p>

    <p v-if="props.scope === 'basic'" class="inline-notice plugin-notice" data-testid="config-plugin-moved">
      <span><strong>按能力管理其它设置</strong><br />模型、平台、Agent、报告与插件设置均在对应能力中管理，密钥也随能力配置。</span>
      <button type="button" class="tonal inline-action" data-testid="config-open-capabilities" @click="emit('open-capabilities')">
        去能力页
      </button>
    </p>

    <ErrorNotice :error="error" title="读取配置失败" />
    <ErrorNotice :error="saveError" title="保存配置失败" />
    <ul v-if="fieldErrors.length > 0" :id="domId('config-field-errors')" class="field-errors" data-testid="config-field-errors" role="alert">
      <li v-for="(item, index) in fieldErrors" :key="index">{{ item.message }}</li>
    </ul>
    <p v-if="validationMessage" class="inline-notice" data-testid="config-validation" role="status">{{ validationMessage }}</p>

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
            v-for="section in visibleSections"
            :key="section.key"
            type="button"
            class="config-nav-item"
            :class="{ active: active === section.key }"
            :aria-current="active === section.key ? 'true' : undefined"
            :data-testid="`config-nav-${section.key}`"
            @click="active = section.key"
          >
            <span class="config-nav-copy"><span>{{ section.label }}</span><small>{{ section.hint }}</small></span>
            <span class="config-count">{{ counts[section.key] }}</span>
          </button>
        </nav>

        <section
          v-if="active !== 'registered'"
          class="panel config-panel"
          :aria-labelledby="`${editorId}-config-${active}-title`"
        >
          <div class="panel-heading">
            <h2 :id="`${editorId}-config-${active}-title`">{{ editorTitle }}</h2>
            <p>{{ activeMeta.hint }} · {{ activeFields.length }} 个字段</p>
          </div>
          <p class="config-guide">{{ capability?.summary ?? '基础设置保存后对新消息生效；具体能力的行为与接入参数请在能力页调整。' }}</p>
          <p v-if="activeFields.length === 0" class="empty-state">该分区没有字段。</p>
          <div v-else class="settings-container">
            <table class="settings-table" role="table" data-testid="config-fields">
              <caption class="sr-only">{{ activeMeta.label }}字段</caption>
              <thead class="sr-only">
                <tr>
                  <th scope="col">字段</th>
                  <th scope="col">当前值 / 编辑</th>
                  <th scope="col">来源</th>
                  <th scope="col">生效</th>
                  <th scope="col">规则</th>
                </tr>
              </thead>
              <tbody role="rowgroup">
                <tr
                  role="row"
                  v-for="item in activeFields"
                  :key="item.key"
                  :class="{ dirty: isFieldDirty(active, item.key, item.field) }"
                  :data-testid="`config-field-${item.key}`"
                >
                  <td class="setting-identity" role="cell">
                    <label v-if="canWrite && isEditable(item.field)" class="config-label" :for="`${editorId}-config-input-${active}-${item.key}`">{{ fieldLabel(item.key) }}</label>
                    <strong v-else class="config-label">{{ fieldLabel(item.key) }}</strong>
                    <span class="config-key">{{ item.key }}</span>
                    <span v-if="isFieldDirty(active, item.key, item.field)" class="cell-sub">已修改</span>
                  </td>
                  <td class="config-value setting-editor" role="cell">
                    <template v-if="canWrite && isEditable(item.field)">
                      <select
                        v-if="valueKind(item.field) === 'boolean'"
                        :id="`${editorId}-config-input-${active}-${item.key}`"
                        class="config-input"
                        :value="currentDraftRaw(active, item.key, item.field)"
                        :aria-label="`${fieldLabel(item.key)}（${item.key}）`"
                        :aria-invalid="fieldInvalid(active, item.key) ? 'true' : undefined"
                        :aria-describedby="fieldInvalid(active, item.key) ? domId('config-field-errors') : `${editorId}-config-rule-${active}-${item.key}`"
                        :disabled="inputsLocked"
                        @change="onFieldInput(active, item.key, inputValue($event))"
                      >
                        <option value="true">是</option>
                        <option value="false">否</option>
                      </select>
                      <input
                        v-else
                        :id="`${editorId}-config-input-${active}-${item.key}`"
                        class="config-input"
                        :type="valueKind(item.field) === 'number' ? 'number' : 'text'"
                        :value="currentDraftRaw(active, item.key, item.field)"
                        :aria-label="`${fieldLabel(item.key)}（${item.key}）`"
                        :aria-invalid="fieldInvalid(active, item.key) ? 'true' : undefined"
                        :aria-describedby="fieldInvalid(active, item.key) ? domId('config-field-errors') : `${editorId}-config-rule-${active}-${item.key}`"
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
                  <td class="setting-source setting-meta" role="cell">
                    <span class="meta-label">来源</span>
                    <span>{{ sourceLabel(item.field.effectiveSource) }}</span>
                    <span class="cell-sub">{{ userModeLabel(item.field.userMode) }}</span>
                  </td>
                  <td class="setting-apply setting-meta" role="cell">
                    <span class="meta-label">生效</span>
                    <span class="status-badge" :data-tone="applyTone(item.field.applyState)">
                      {{ applyLabel(item.field.applyState) }}
                    </span>
                  </td>
                  <td :id="`${editorId}-config-rule-${active}-${item.key}`" class="setting-rules setting-meta" role="cell">
                    <span class="meta-label">规则</span>
                    <span>{{ fieldRule(item.field.rule) }}</span>
                    <span class="cell-sub">{{ item.field.visibility === 0 ? '仅概览' : '可读' }}</span>
                  </td>
                </tr>
              </tbody>
            </table>
          </div>
        </section>

        <section v-else class="panel config-panel" :aria-labelledby="domId('config-registered-title')">
          <div class="panel-heading">
            <h2 :id="domId('config-registered-title')">模型供应商</h2>
            <p v-if="registered">{{ registered.providers.length }} 个已登记供应商</p>
          </div>
          <p class="config-guide">{{ capability?.summary }}</p>

          <p v-if="!registered" class="empty-state" data-testid="config-registered-empty">
            快照没有返回已登记信息。
          </p>
          <template v-else>
            <p v-if="registered.providers.length === 0" class="empty-state">没有已登记供应商。</p>
            <div v-else class="settings-container">
              <table class="provider-table" role="table" data-testid="config-providers">
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
                  <tr v-for="provider in registered.providers" :key="provider.key" role="row">
                    <td class="provider-identity" role="cell"><strong>{{ provider.key }}</strong></td>
                    <td class="config-value provider-name" role="cell">
                      <label v-if="canWrite && provider.name.visibility === 2" class="meta-label" :for="`${editorId}-config-provider-input-${provider.key}`">名称</label>
                      <template v-if="canWrite && provider.name.visibility === 2">
                        <input
                          :id="`${editorId}-config-provider-input-${provider.key}`"
                          class="config-input"
                          type="text"
                          :value="providerNameRaw(provider.key)"
                          :aria-label="`${provider.key} 的真名（显示用 ${provider.name.display}）`"
                          :placeholder="provider.name.display"
                          :aria-invalid="invalidPaths.has(providerErrorPath(provider.key)) ? 'true' : undefined"
                          :aria-describedby="invalidPaths.has(providerErrorPath(provider.key)) ? domId('config-field-errors') : undefined"
                          :disabled="inputsLocked"
                          :data-testid="`config-provider-name-${provider.key}`"
                          @input="providerNameInput(provider.key, inputValue($event))"
                        />
                        <span v-if="isProviderDirty(provider.key)" class="cell-sub">已改名</span>
                      </template>
                      <span v-else :title="valueTitle(provider.name)">{{ formatValue(provider.name) }}</span>
                    </td>
                    <td class="config-value provider-url"><span class="meta-label">Base URL</span>{{ formatValue(provider.baseUrl) }}</td>
                    <td class="provider-model"><span class="meta-label">模型</span>{{ formatValue(provider.modelName) }}</td>
                    <td class="provider-meta"><span class="meta-label">优先级</span>{{ provider.priority }}</td>
                    <td class="provider-meta"><span class="meta-label">凭据</span>{{ credentialLabels[provider.credential] ?? provider.credential }}</td>
                    <td class="provider-meta"><span class="meta-label">运行时</span>
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

      <section v-if="visibleSecrets.length" class="panel secrets-panel" :aria-labelledby="domId('config-secrets-title')">
        <div class="panel-heading">
          <h2 :id="domId('config-secrets-title')">秘密槽位</h2>
          <p>只显示是否存在与来源，后端不返回秘密值。</p>
        </div>
        <ul class="secret-list" data-testid="config-secrets">
          <li v-for="secret in visibleSecrets" :key="secret.slot">
            <div class="secret-head">
              <label v-if="secretAction(secret.slot)?.kind === 'replace' && canWrite" class="secret-slot" :for="`${editorId}-config-secret-input-${secret.slot}`">{{ secretSlotLabel(secret.slot) }}</label>
              <span v-else class="secret-slot">{{ secretSlotLabel(secret.slot) }}</span>
              <span class="status-badge" :data-tone="secret.configured ? 'ok' : 'neutral'">
                {{ secret.configured ? '已配置' : '未配置' }}
              </span>
              <span class="secret-source">{{ secretSourceLabel(secret.source) }}</span>
            </div>
            <label v-if="canWrite && secretReconfirm[secret.slot] !== undefined" class="inline-notice warn">
              <input type="checkbox" v-model="secretReconfirm[secret.slot]" :data-testid="'config-secret-reconfirm-' + secret.slot" />
              秘密值无法比较；我确认仍要修改此槽位，并重新输入替换值。
            </label>
            <div v-if="canWrite" class="secret-actions">
              <template v-if="secretAction(secret.slot)?.kind === 'replace'">
                <input
                  class="config-input"
                  :id="`${editorId}-config-secret-input-${secret.slot}`"
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

      <section v-if="receipt" class="panel" :aria-labelledby="domId('config-receipt-title')" data-testid="config-receipt">
        <div class="panel-heading">
          <h2 :id="domId('config-receipt-title')">最近一次提交</h2>
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

      <div
        class="save-bar"
        :class="{ 'save-bar--floating': hasDraft || busy || unconfirmed }"
        data-testid="config-save-bar"
      >
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
            :disabled="saveBlocked || dirtyCount === 0 || !canWrite"
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
            :aria-labelledby="domId('config-leave-title')"
            data-testid="config-leave-dialog"
            @keydown="onLeaveKeydown"
          >
            <h2 :id="domId('config-leave-title')">{{ leaveTitle }}</h2>
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
            <p v-else-if="!hasWritePermission && hasDraft" class="inline-notice">
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
                :disabled="!canSaveOnLeave || leaveBusy || busy || unconfirmed"
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

<style scoped>
.configuration { display: grid; gap: 20px; min-width: 0; }
.config-body { min-width: 0; }
.capability-editor .page-heading .eyebrow, .capability-editor .page-heading h1, .capability-editor .page-description { display: none; }
.capability-editor .page-heading { margin: 0; border-bottom: 0; padding-bottom: 0; }
.capability-editor .config-body { display: block; }
.capability-editor .config-nav { display: flex; flex-wrap: wrap; position: static; gap: 8px; margin-bottom: 16px; }
.capability-editor .config-nav-item { flex: 1 1 160px; }
.capability-editor .config-summary { display: none; }
.config-panel, .settings-container { min-width: 0; }
.config-nav-copy { display: grid; gap: 5px; }
.config-nav-copy small { font-size: 11px; color: var(--text-muted); }
.plugin-notice { display: flex; flex-wrap: wrap; justify-content: space-between; align-items: center; gap: 12px; }
.settings-table, .provider-table { display: block; width: 100%; border: 0; }
.settings-table tbody, .provider-table tbody { display: block; }
.settings-table tr { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 10px 20px; padding: 14px 0; border-bottom: 1px solid var(--border); }
.settings-table tr:first-child { padding-top: 4px; }
.settings-table tbody tr:hover td, .settings-table tbody tr.dirty td { background: transparent; }
.settings-table tr.dirty { background: var(--accent-tint); box-shadow: inset 2px 0 var(--accent-deep); }
.settings-table tr:last-child, .provider-table tr:last-child { border-bottom: 0; }
.settings-table td, .provider-table td { display: block; border: 0; padding: 0; min-width: 0; }
.setting-identity { flex: 0 0 200px; }
.setting-editor { flex: 1 1 calc(100% - 220px); }
.settings-table .setting-meta { display: flex; flex-wrap: wrap; align-items: center; gap: 4px 8px; color: var(--text-muted); font-size: 12px; }
.setting-meta .meta-label, .setting-meta .cell-sub { display: inline; margin: 0; }
.setting-editor .config-input { max-width: 640px; }
.setting-editor select.config-input, .setting-editor input[type="number"] { max-width: 220px; }
.meta-label { display: block; color: var(--text-soft); font-size: 11px; margin-bottom: 5px; }
.config-input { font-size: 14px; height: 40px; min-height: 40px; padding-block: 6px; }
.config-label { font-size: 14px; font-weight: 600; }
.config-key { font-size: 11px; margin-top: 3px; }
.edit-actions { display: flex; gap: 12px; flex-wrap: wrap; margin-top: 8px; }
.edit-actions button { min-height: 32px; }
.edit-actions:empty { display: none; }
.provider-table thead { position: absolute; width: 1px; height: 1px; overflow: hidden; clip-path: inset(50%); }
.provider-table tr { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 10px 16px; padding: 14px 0; border-bottom: 1px solid var(--border); }
.provider-identity { grid-column: 1 / -1; }
.provider-name { grid-column: 1 / -1; }
.provider-name .config-input { max-width: 440px; }
.provider-url { grid-column: 1 / span 2; }
.provider-model, .provider-url { overflow-wrap: anywhere; }
.secret-list { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.secret-list li { display: grid; min-width: 0; align-content: start; padding: 16px; border: 1px solid var(--border); border-radius: 12px; }
.secret-list li:last-child { border: 1px solid var(--border); }
.secret-head { gap: 8px; }
.secret-slot { font-weight: 600; }
.secret-actions { flex-wrap: nowrap; gap: 8px; }
.secret-actions .config-input { flex: 1; width: 0; max-width: none; }
.secret-actions button { flex-shrink: 0; }
.receipt-grid { display: grid; gap: 12px; grid-template-columns: repeat(3, minmax(0,1fr)); }
.receipt-grid dd { margin: 6px 0 0; overflow-wrap: anywhere; }
.field-errors { color: var(--bad-text); }
.conflict-flag { color: var(--bad-text); }
@media (max-width: 900px) { .config-body { grid-template-columns: minmax(0,1fr); } .config-nav { position: static; display: flex; flex-wrap: wrap; } }
@media (max-width: 600px) {
  .configuration { gap: 16px; }
  .settings-table tr { gap: 8px 14px; }
  .setting-identity, .setting-editor { flex-basis: 100%; }
  .config-input { height: 44px; min-height: 44px; }
  .setting-meta .meta-label { display: inline; margin-right: 8px; }
  .secret-list, .receipt-grid { grid-template-columns: minmax(0,1fr); }
  .provider-table tr { grid-template-columns: repeat(2,minmax(0,1fr)); }
  .provider-url, .provider-name, .provider-identity { grid-column: 1 / -1; }
}
</style>
