<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaQuota, type ZbaQuotaSaveRequest } from '../../api/zba'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { newNonce } from '../configuration/draft'
import RegisteredPluginSwitch from './RegisteredPluginSwitch.vue'
import { beginOperation, markOperation, pendingOperations } from '../../shared/sync/pending-operations'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  features: readonly string[]
  active?: boolean
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
  runtimeState?: number
}>(), { active: true, trigger: null, fallback: null })
const emit = defineEmits<{ close: []; saved: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
type Phase = 'idle' | 'saving' | 'unknown' | 'checking' | 'conflict' | 'reload-required'
const phase = ref<Phase>('idle')
const pluginBusy = ref(false)
const items = ref<ZbaQuota[]>([])
const loaded = ref(false)
const loading = ref(false)
const error = ref<UiError | null>(null)
const notice = ref('')
const validation = ref('')
const baseline = ref<ZbaQuota | null>(null)
const draft = ref('')
// Full immutable intent, not just the key: reconciliation must match its source and limit.
const pending = ref<ZbaQuotaSaveRequest | null>(null)
const ownerClient = props.client
const recovery = computed(() => pendingOperations(ownerClient, 'quota').find(item => item.state !== 'settled') ?? null)
const retryAllowed = ref(false)
const retryArmed = ref(false)
const discard = ref<'close' | 'editor' | null>(null)
const editorHeading = ref<HTMLElement | null>(null)
const listHeading = ref<HTMLElement | null>(null)
let alive = true
let readSequence = 0
const owns = (client: ZbaClient): boolean => alive && props.client === client && client === ownerClient
const canList = computed(() => props.features.includes('quotas.list'))
const canWrite = computed(() => props.client === ownerClient && props.active && props.mode === 'admin' && props.writesEnabled
  && canList.value && props.features.includes('quotas.save') && props.features.includes('quotas.find'))
const canQuery = computed(() => props.client === ownerClient && props.active && props.mode === 'admin' && props.writesEnabled && props.features.includes('quotas.find'))
const unsettled = computed(() => pending.value !== null || recovery.value !== null)
const dirty = computed(() => baseline.value !== null && (!/^\d+$/.test(draft.value) || Number(draft.value) !== baseline.value.dailyTokenLimit))
const editable = computed(() => canWrite.value && !loading.value && !pluginBusy.value && !unsettled.value && phase.value === 'idle')
const number = (value: number): string => value.toLocaleString('zh-CN')
const resetTime = (value: string): string => new Date(value).toLocaleString('zh-CN', { hour12: false })
function auth(cause: unknown): boolean {
  if (!(cause instanceof ZbaRequestError) || cause.status !== 401) return false
  emit('auth-expired', toUiError(cause))
  return true
}

async function load(): Promise<boolean> {
  if (props.client !== ownerClient || !props.active || !canList.value || loading.value || unsettled.value) return false
  const client = props.client
  const sequence = ++readSequence
  loading.value = true
  error.value = null
  try {
    const result = await client.listQuotas()
    if (!owns(client) || sequence !== readSequence) return false
    items.value = result.items
    loaded.value = true
    const preserveDraft = dirty.value
    if (baseline.value) {
      const latest = result.items.find((item) => item.sourceKey === baseline.value?.sourceKey)
      if (!latest) {
        phase.value = 'conflict'
        notice.value = '此来源已不在最新列表中。草稿已保留，不能继续保存。'
        return false
      }
      baseline.value = latest
      if (!preserveDraft || phase.value === 'reload-required') draft.value = String(latest.dailyTokenLimit)
    }
    phase.value = 'idle'
    return true
  } catch (cause) {
    if (owns(client) && sequence === readSequence && !auth(cause)) error.value = toUiError(cause)
    return false
  } finally {
    if (owns(client) && sequence === readSequence) loading.value = false
  }
}
function edit(item: ZbaQuota): void {
  if (!editable.value || baseline.value) return
  baseline.value = item
  draft.value = String(item.dailyTokenLimit)
  validation.value = ''
  notice.value = ''
  discard.value = null
  void nextTick(() => editorHeading.value?.focus())
}
function leaveEditor(): void {
  baseline.value = null
  draft.value = ''
  validation.value = ''
  discard.value = null
  if (phase.value === 'conflict') phase.value = 'idle'
  void nextTick(() => listHeading.value?.focus())
}
function requestClose(editor = false): void {
  if (pluginBusy.value || unsettled.value || phase.value === 'saving' || phase.value === 'checking') {
    validation.value = '提交结果尚未确认，请先用原操作键核对。不会重复保存，也不能放弃这次提交。'
    return
  }
  if (dirty.value) { discard.value = editor ? 'editor' : 'close'; return }
  if (editor) leaveEditor()
  else emit('close')
}
function confirmDiscard(): void {
  if (unsettled.value || pluginBusy.value) return
  if (discard.value === 'editor') leaveEditor()
  else emit('close')
}
async function rebase(): Promise<void> {
  if (phase.value !== 'conflict') return
  if (await load()) notice.value = '已读取最新额度，草稿仍保留。请核对差异后再次明确保存。'
}
async function confirmed(quota: ZbaQuota): Promise<void> {
  // Clear the already-committed draft BEFORE reading usage. A failed read must never offer replay.
  pending.value = null
  retryAllowed.value = false
  retryArmed.value = false
  baseline.value = null
  draft.value = ''
  discard.value = null
  validation.value = ''
  items.value = items.value.map((item) => item.sourceKey === quota.sourceKey ? quota : item)
  phase.value = 'reload-required'
  notice.value = '额度已保存。正在读取最新用量；回执中的用量可能不是当前值。'
  emit('saved')
  if (await load()) notice.value = '额度已保存，当前用量已刷新。'
  else notice.value = '额度已确认保存，但最新用量未刷新。已有草稿已清除；请重新读取后再编辑，不要重复提交。'
}
async function save(): Promise<void> {
  const base = baseline.value
  if (!base || !dirty.value || !editable.value) return
  const limit = Number(draft.value)
  if (!/^\d+$/.test(draft.value) || !Number.isSafeInteger(limit)
    || limit < base.minimumDailyTokenLimit || limit > base.maximumDailyTokenLimit) {
    validation.value = '请输入允许范围内的整数 Token 额度。'
    return
  }
  const request: ZbaQuotaSaveRequest = Object.freeze({ sourceKey: base.sourceKey, expectedRevision: base.revision,
    idempotencyKey: newNonce(), dailyTokenLimit: limit })
  try {
    beginOperation(ownerClient, { key: request.idempotencyKey, kind: 'quota', target: request.sourceKey,
      meta: { sourceKey: request.sourceKey, dailyTokenLimit: request.dailyTokenLimit } })
  } catch {
    validation.value = '此来源已有未决操作或登记已满；请先核对，不会生成另一笔写入。'
    return
  }
  pending.value = request
  await send(request)
}
function rejected(cause: unknown): boolean {
  return cause instanceof ZbaRequestError && ((cause.status === 409 && cause.code === 'quota_revision_conflict')
    || (cause.status === 503 && cause.code === 'quota_receipt_capacity')
    || ([400, 401, 403, 404].includes(cause.status) && cause.code !== 'http_error'))
}
let localSettlement: string | null = null
function activeKey(key: string): boolean {
  return pendingOperations(ownerClient, 'quota').some(item => item.key === key && item.state !== 'settled')
}
function settle(key: string): void {
  localSettlement = key
  try { markOperation(ownerClient, key, 'settled') } finally { localSettlement = null }
}
function matches(quota: ZbaQuota, sourceKey: unknown, limit: unknown): boolean {
  return quota.sourceKey === sourceKey && quota.dailyTokenLimit === limit
}
async function send(request: ZbaQuotaSaveRequest): Promise<void> {
  const client = ownerClient
  phase.value = 'saving'
  retryAllowed.value = false
  retryArmed.value = false
  error.value = null
  validation.value = ''
  notice.value = ''
  try {
    const result = await client.saveQuota(request)
    if (!matches(result.quota, request.sourceKey, request.dailyTokenLimit)) throw new Error('quota_receipt_mismatch')
    // Shared settlement must survive lock/unmount; only UI work depends on ownership.
    if (!activeKey(request.idempotencyKey)) return
    settle(request.idempotencyKey)
    if (owns(client)) await confirmed(result.quota)
  } catch (cause) {
    if (!activeKey(request.idempotencyKey)) return
    if (rejected(cause)) settle(request.idempotencyKey)
    else markOperation(client, request.idempotencyKey, 'unknown')
    if (!owns(client)) return
    if (rejected(cause)) {
      pending.value = null
      if (cause instanceof ZbaRequestError && cause.code === 'quota_revision_conflict') {
        phase.value = 'conflict'
        notice.value = '额度已被其他人修改。草稿已保留；读取最新后再核对，绝不会自动重试保存。'
      } else {
        phase.value = 'idle'
        if (!auth(cause)) error.value = toUiError(cause)
      }
    } else {
      phase.value = 'unknown'
      error.value = toUiError(cause)
    }
  }
}
async function retry(): Promise<void> {
  const request = pending.value
  if (!request || !canWrite.value || pluginBusy.value || phase.value !== 'unknown' || !retryAllowed.value
    || recovery.value?.key !== request.idempotencyKey) return
  if (!retryArmed.value) { retryArmed.value = true; return }
  // Reuse exactly the live owner's immutable request; never reconstruct one after reopening.
  await send(request)
}
async function check(): Promise<void> {
  const record = recovery.value
  if (!record || phase.value !== 'unknown' || !canQuery.value) return
  const client = ownerClient
  phase.value = 'checking'
  error.value = null
  validation.value = ''
  try {
    const result = await client.findQuota(record.key)
    if (!activeKey(record.key)) return
    if (result.found && matches(result.quota, record.meta.sourceKey, record.meta.dailyTokenLimit)) {
      settle(record.key)
      if (owns(client)) await confirmed(result.quota)
      return
    }
    if (!owns(client)) return
    retryAllowed.value = !result.found && pending.value?.idempotencyKey === record.key
    retryArmed.value = false
    phase.value = 'unknown'
    notice.value = result.found ? '回执与本次提交不匹配，仍无法确认结果。请继续核对原操作键。'
      : '暂未找到回执，但不能确认没有执行。原操作键保留，不会自动重新保存；原视图可明确确认后按原请求同键重试。'
  } catch (cause) {
    if (!activeKey(record.key)) return
    markOperation(client, record.key, 'unknown')
    if (!owns(client)) return
    phase.value = 'unknown'
    retryAllowed.value = false
    retryArmed.value = false
    if (!auth(cause)) error.value = toUiError(cause)
  }
}
async function requestLeave(reason: 'lock' | 'downgrade', _trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') return true // The registry keeps quota and plugin operation keys independently.
  if (unsettled.value || pluginBusy.value) {
    validation.value = '提交结果尚未确认，请先核对原操作键。'
    return false
  }
  if (dirty.value) { discard.value = 'close'; return false }
  return true
}
defineExpose({ requestLeave })
watch(recovery, (record, previous) => {
  if (record) {
    if (!pending.value) {
      phase.value = 'unknown'
      notice.value = '已恢复原操作键，只能核对先前提交；不会构造或重发请求。'
    }
  } else if (previous && localSettlement !== previous.key) {
    pending.value = null
    retryAllowed.value = false
    retryArmed.value = false
    baseline.value = null
    draft.value = ''
    error.value = null
    validation.value = ''
    phase.value = 'reload-required'
    notice.value = '原提交已有确定结论，请读取最新额度后再编辑。'
    if (props.active) void load()
  }
}, { immediate: true, flush: 'sync' })
// Disabling the submitted form can drop focus to body. Keep Escape/Tab owned by the dialog.
watch(() => unsettled.value || pluginBusy.value, (value) => {
  if (value && props.active) void nextTick(() => listHeading.value?.focus())
})
watch(() => props.active, (active) => {
  if (!active) { ++readSequence; loading.value = false }
  else if ((!loaded.value || phase.value === 'reload-required') && !unsettled.value) void load()
})
onMounted(() => { void load() })
onBeforeUnmount(() => { alive = false; ++readSequence })
</script>

<template>
  <ManagementDialog title="Token 配额" title-id="token-quota-title" test-id="token-quota-dialog"
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="requestClose()">
    <template #eyebrow><span class="eyebrow">DAILY TOKEN BUDGET</span></template>
    <p class="muted">按已知来源管理每日额度。用量由服务端统计，重置时间以服务端为准；耗尽后进入节能模式。</p>
    <RegisteredPluginSwitch :client="props.client" plugin-key="preset.feature.token-quota" :mode="props.mode" :writes-enabled="props.writesEnabled"
      :active="props.active" :blocked="unsettled || dirty || loading" :runtime-state="props.runtimeState"
      @busy="pluginBusy = $event" @saved="emit('saved')" @auth-expired="value => emit('auth-expired', value)" />
    <div v-if="!canWrite" class="notice" data-testid="quota-readonly">
      当前只读。<button v-if="props.mode !== 'admin'" type="button" @click="emit('enable-admin')">启用管理员操作</button>
      <p v-if="!props.features.includes('quotas.save') || !props.features.includes('quotas.find')">服务端未公布完整的额度写入与回执核对能力。</p>
    </div>
    <p v-if="!canList" class="notice" data-testid="quota-unavailable">此服务端暂不支持配额列表。</p>
    <p v-if="loading" role="status">正在读取最新配额…</p>
    <ErrorNotice v-if="error" :error="error" />
    <p v-if="validation" class="notice bad" role="alert" data-testid="quota-validation">{{ validation }}</p>
    <p v-if="notice" class="notice" role="status" data-testid="quota-notice">{{ notice }}</p>
    <div v-if="unsettled" class="notice warn" role="status" data-testid="quota-unknown">
      <p>{{ phase === 'saving' ? '正在提交，请等待确认。' : '提交结果未知，原操作键已保留。请先核对；暂未找到回执时，仅原视图可明确确认后按原键重试。' }}</p>
      <button v-if="phase !== 'saving'" type="button" data-testid="quota-check" :disabled="!canQuery || phase === 'checking'" @click="check">{{ phase === 'checking' ? '正在核对…' : '核对提交结果' }}</button>
      <button v-if="retryAllowed && pending && phase === 'unknown' && !retryArmed" type="button" data-testid="quota-retry" :disabled="!canWrite || pluginBusy" @click="retry">使用原操作键重试…</button>
      <div v-if="retryArmed" data-testid="quota-retry-confirmation">
        <p>只重试先前的同一来源、额度和版本，不会生成新操作键。确认继续？</p>
        <button type="button" data-testid="quota-retry-confirm" :disabled="!canWrite || pluginBusy || phase !== 'unknown'" @click="retry">确认原键重试</button>
        <button type="button" @click="retryArmed = false">取消</button>
      </div>
    </div>
    <div v-if="phase === 'conflict'" class="notice warn" data-testid="quota-conflict">
      <p>读取最新额度不会替换你的草稿。核对后才能再次保存。</p>
      <button type="button" data-testid="quota-rebase" :disabled="loading || !props.active || !canList" @click="rebase">读取最新，保留草稿</button>
    </div>
    <div class="list-toolbar">
      <h3 ref="listHeading" tabindex="-1">已知来源 <span class="muted">{{ items.length }}</span></h3>
      <button type="button" data-testid="quota-refresh" :disabled="loading || unsettled || !canList || !props.active || phase === 'conflict' || dirty" @click="load">刷新用量</button>
    </div>
    <p v-if="loaded && !items.length" class="muted" data-testid="quota-empty">尚无已知来源。有来源产生 Token 用量后，会在此显示。</p>
    <ul class="quota-list">
      <li v-for="(item, index) in items" :key="item.sourceKey" class="quota-source" :data-testid="'quota-source-' + index">
        <header><h4>{{ item.displayName }}</h4><span class="quota-state" :class="{ depleted: item.energySaving }">{{ item.energySaving ? '节能模式' : '额度可用' }}</span></header>
        <dl class="quota-metrics">
          <div><dt>今日已用</dt><dd>{{ number(item.usedTokens) }}</dd></div>
          <div><dt>今日剩余</dt><dd>{{ number(item.remainingTokens) }}</dd></div>
          <div><dt>每日额度</dt><dd>{{ number(item.dailyTokenLimit) }}</dd></div>
        </dl>
        <p class="muted">输入 {{ number(item.usedPromptTokens) }} · 输出 {{ number(item.usedCompletionTokens) }}</p>
        <footer><p class="muted">统计日 {{ item.resetDate }}<br>下次重置 <time :datetime="item.nextResetAt">{{ resetTime(item.nextResetAt) }}</time></p>
          <button type="button" :data-testid="'quota-edit-' + index" :aria-label="'编辑 ' + item.displayName + ' 每日额度'" :disabled="!editable || baseline !== null" @click="edit(item)">编辑额度</button></footer>
      </li>
    </ul>
    <form v-if="baseline" class="quota-editor" @submit.prevent="save">
      <h3 ref="editorHeading" tabindex="-1">{{ baseline.displayName }} · 每日额度</h3>
      <p class="muted" data-testid="quota-latest">服务端当前额度 {{ number(baseline.dailyTokenLimit) }} Token</p>
      <label for="quota-limit">每日 Token 额度</label>
      <input id="quota-limit"  :value="draft" type="number" inputmode="numeric" step="1" :min="baseline.minimumDailyTokenLimit" :max="baseline.maximumDailyTokenLimit" :disabled="!editable" data-testid="quota-limit" required @input="draft = ($event.target as HTMLInputElement).value; discard = null; validation = ''">
      <p class="muted">允许范围：{{ number(baseline.minimumDailyTokenLimit) }}–{{ number(baseline.maximumDailyTokenLimit) }}。每次只保存当前来源，不修改其他来源。</p>
      <div class="editor-actions"><button type="submit" class="primary" data-testid="quota-save" :disabled="!editable || !dirty">保存额度</button>
        <button type="button" data-testid="quota-cancel-edit" :disabled="unsettled" @click="requestClose(true)">结束编辑</button></div>
    </form>
    <div v-if="discard" class="notice warn" role="alert" data-testid="quota-discard"><p>有未保存的额度草稿，确定放弃？</p>
      <button type="button" data-testid="quota-discard-confirm" @click="confirmDiscard">放弃草稿</button><button type="button" @click="discard = null">继续编辑</button></div>
  </ManagementDialog>
</template>

<style scoped>
.eyebrow { color: var(--accent-deep); font-size: 11px; font-weight: 650; letter-spacing: .1em; }
.muted { color: var(--text-muted); font-size: 12px; line-height: 1.7; margin: 6px 0; overflow-wrap: anywhere; }
.notice { border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin: 12px 0; background: var(--surface-muted); font-size: 13px; line-height: 1.7; overflow-wrap: anywhere; }
.notice p { margin: 0 0 8px; }.notice.warn { background: var(--warn-bg); color: var(--warn-text); }.notice.bad { background: var(--bad-bg); color: var(--bad-text); }
.list-toolbar, .quota-source header, .quota-source footer { display: flex; align-items: center; justify-content: space-between; flex-wrap: wrap; gap: 10px; }
.list-toolbar { margin-top: 24px; }.list-toolbar h3 { font-size: 14px; margin: 0; }
.quota-list { list-style: none; padding: 0; display: grid; gap: 12px; }
.quota-source { min-width: 0; border: 1px solid var(--border); border-radius: 10px; padding: 18px; background: var(--surface); }
.quota-source h4, .quota-editor h3 { font-size: 14px; margin: 0; overflow-wrap: anywhere; }
.quota-state { font-size: 11px; border-radius: 5px; padding: 3px 8px; color: var(--ok-text); background: var(--ok-bg); }.quota-state.depleted { color: var(--warn-text); background: var(--warn-bg); }
.quota-metrics { display: grid; grid-template-columns: repeat(3, minmax(0, 1fr)); gap: 12px; margin: 18px 0 10px; }
.quota-metrics dt { color: var(--text-muted); font-size: 11px; }.quota-metrics dd { font-size: 20px; font-weight: 650; font-variant-numeric: tabular-nums; margin: 5px 0 0; overflow-wrap: anywhere; }
.quota-source footer { border-top: 1px solid var(--border); padding-top: 8px; margin-top: 12px; }
.quota-editor { display: grid; gap: 10px; padding: 18px; border: 1px solid var(--border-strong); border-radius: 10px; background: var(--surface-muted); margin-top: 20px; min-width: 0; }
.quota-editor label { font-size: 12px; font-weight: 600; }.quota-editor input { min-width: 0; width: 100%; max-width: 280px; border-radius: 8px; min-height: 38px; }
.editor-actions { display: flex; flex-wrap: wrap; gap: 8px; }button { min-height: 36px; border-radius: 8px; }
@media (max-width: 600px) { .quota-source, .quota-editor { padding: 14px; }.quota-metrics { grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 12px; }.quota-metrics > div:first-child { grid-column: 1 / -1; }.quota-metrics dd { font-size: 17px; }button, .quota-editor input { min-height: 44px; }.editor-actions > button { flex: 1 1 auto; } }
</style>
