<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaPluginSettings } from '../../api/zba'
import { isPluginWriteOutcomeUnknown as isUnknown } from './plugin-write-outcome'
import { beginOperation, getPendingOperation, markOperation } from '../../shared/sync/pending-operations'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { useDraftProtection } from '../../shared/ui/draft-protection'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import DeclaredPluginSettings from './DeclaredPluginSettings.vue'
import { clearPluginSecretDrafts, createPluginSettingsDraft, pluginSettingsErrors, type PluginSettingDraft, type PluginSettingsDraft } from './declared-plugin-settings'
import { pluginRecoveryNotice, pluginSettingEdits, pluginSettingsDirty, pluginSettingsKey, type PluginNotice } from './plugin-marketplace'

const props = defineProps<{
  client: ZbaClient; mode: 'read' | 'admin'; writesEnabled: boolean; active: boolean
  pluginId: string; trigger?: HTMLElement | null; fallback?: HTMLElement | null
}>()
const emit = defineEmits<{ close: []; changed: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)
const pluginId = ref(props.pluginId)
const settings = ref<ZbaPluginSettings | null>(null)
const draft = ref<PluginSettingsDraft>({})
const settingsLoading = ref(false)
const settingsError = ref<UiError | null>(null)
const settingsNotice = ref<PluginNotice | null>(null)
const settingsPending = computed(() => getPendingOperation(props.client, 'plugin-settings', pluginId.value))
const settingsBusy = computed(() => settingsPending.value?.state === 'in-flight')
const settingsBlocked = computed(() => Boolean(settingsPending.value || getPendingOperation(props.client, 'plugin', pluginId.value)))
const reviewedSettingsKey = ref('')
const reconcileArmed = ref(false)
const dirty = computed(() => pluginSettingsDirty(draft.value))
let alive = true
let settingsEpoch = 0
const discardAction = ref<null | 'switch' | 'close' | 'leave' | 'reload'>(null)
let switchTarget: string | null = null
let leaveResolve: ((value: boolean) => void) | null = null
let leavePromise: Promise<boolean> | null = null

function describe(cause: unknown): UiError { return toUiError(cause) }
function live(client: ZbaClient): boolean { return alive && props.active && client === props.client }
function ownsSettings(client: ZbaClient, id: string, epoch: number): boolean { return live(client) && pluginId.value === id && epoch === settingsEpoch }
function showError(cause: unknown): void {
  const value = describe(cause)
  if (cause instanceof ZbaRequestError && cause.status === 401) { endSession(); emit('auth-expired', value) }
  else settingsError.value = value
}
function applySettings(result: ZbaPluginSettings): void {
  // Retain only the redacted projection, even if a server accidentally includes secret values.
  const values = Object.fromEntries(result.schema.filter(field => field.type !== 'secret' && field.key in result.values).map(field => [field.key, result.values[field.key]!]))
  settings.value = { ...result, values }
  draft.value = createPluginSettingsDraft(result.schema, values)
}
function discardSecretsOnLeave(): void { clearPluginSecretDrafts(settings.value?.schema ?? [], draft.value) }
function cancelDiscard(): void {
  discardAction.value = null; switchTarget = null
  leaveResolve?.(false); leaveResolve = null; leavePromise = null
}
function confirmDiscard(): void {
  if (settingsBlocked.value) return
  const action = discardAction.value; const target = switchTarget
  discardAction.value = null; switchTarget = null
  discardSecretsOnLeave()
  draft.value = settings.value ? createPluginSettingsDraft(settings.value.schema, settings.value.values) : {}
  if (action === 'switch' && target) void selectSettings(target)
  else if (action === 'close') emit('close')
  else if (action === 'leave') { leaveResolve?.(true); leaveResolve = null; leavePromise = null }
  else if (action === 'reload') void loadSettings()
}
function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', _trigger?: HTMLElement | null): Promise<boolean> {
  discardSecretsOnLeave()
  // Authentication cleanup must never wait for a receipt; the shared registry owns its key.
  if (reason === 'lock' || reason === 'navigate') { cancelDiscard(); return Promise.resolve(true) }
  if (settingsPending.value) return Promise.resolve(true)
  if (!dirty.value) return Promise.resolve(true)
  if (leavePromise) return leavePromise
  cancelDiscard(); discardAction.value = 'leave'
  leavePromise = new Promise(resolve => { leaveResolve = resolve })
  return leavePromise
}
function close(): void {
  discardSecretsOnLeave()
  if (discardAction.value) { cancelDiscard(); return }
  if (dirty.value && !settingsPending.value) { discardAction.value = 'close'; return }
  emit('close')
}
function endSession(): void {
  discardSecretsOnLeave(); ++settingsEpoch; cancelDiscard()
  settings.value = null; draft.value = {}; settingsLoading.value = false
  settingsError.value = null; settingsNotice.value = null; reviewedSettingsKey.value = ''; reconcileArmed.value = false
}
async function selectSettings(id: string): Promise<void> {
  endSession(); pluginId.value = id
  await loadSettings()
}
useDraftProtection(() => dirty.value || settingsBlocked.value)
defineExpose({ requestLeave, discardSecretsOnLeave, endSession, draftCount: computed(() => dirty.value ? 1 : 0) })

async function loadSettings(): Promise<void> {
  if (!props.active) return
  const id = pluginId.value
  const client = props.client; const epoch = ++settingsEpoch
  settingsLoading.value = true; settingsError.value = null; reviewedSettingsKey.value = ''; reconcileArmed.value = false
  try {
    const result = await client.pluginSettings(id)
    if (!ownsSettings(client, id, epoch)) return
    applySettings(result)
    const pending = getPendingOperation(client, 'plugin-settings', id)
    if (pending?.state === 'unknown') reviewedSettingsKey.value = pending.key
  } catch (cause) { if (ownsSettings(client, id, epoch)) showError(cause) }
  finally { if (alive && client === props.client && pluginId.value === id && epoch === settingsEpoch) settingsLoading.value = false }
}
function onSettingChange(key: string, value: PluginSettingDraft): void {
  if (!canWrite.value || settingsBlocked.value || settingsLoading.value) return
  draft.value = { ...draft.value, [key]: value }
}
async function saveSettings(): Promise<void> {
  const id = pluginId.value; const current = settings.value
  if (!current || !canWrite.value || settingsBlocked.value || settingsLoading.value) return
  const problems = pluginSettingsErrors(current.schema, draft.value)
  if (problems.length) { settingsNotice.value = { tone: 'bad', message: problems.join(' ') }; return }
  const edits = pluginSettingEdits(current.schema, draft.value)
  if (!edits.length) return
  const client = props.client; const key = pluginSettingsKey(id); const epoch = settingsEpoch
  try { beginOperation(client, { key, kind: 'plugin-settings', target: id }) }
  catch (cause) { settingsError.value = describe(cause); return }
  settingsError.value = null; settingsNotice.value = null; reviewedSettingsKey.value = ''; reconcileArmed.value = false
  // No request body/secret is retained for retries. The transport owns the dispatched copy.
  discardSecretsOnLeave(); draft.value = createPluginSettingsDraft(current.schema, current.values)
  try {
    const result = await client.savePluginSettings(id, { expectedRevision: current.revision, idempotencyKey: key, edits })
    markOperation(client, key, 'settled')
    if (!ownsSettings(client, id, epoch)) return
    applySettings(result)
    settingsNotice.value = { tone: result.restartRequired ? 'warn' : 'ok', message: result.restartRequired ? '设置已保存，等待插件初始化应用；停用的插件会在启用时应用。' : '设置已保存并热应用。' }
    emit('changed')
  } catch (cause) {
    markOperation(client, key, isUnknown(cause) ? 'unknown' : 'settled')
    if (!ownsSettings(client, id, epoch)) return
    if (isUnknown(cause)) settingsNotice.value = { tone: 'warn', message: '保存结果未知，原操作键已保留。重读仅展示当前设置，不证明原请求未执行，不要重复提交。' }
    else { showError(cause); settingsNotice.value = pluginRecoveryNotice(cause) }
  }
}
function reloadSettings(): void {
  if (settingsBusy.value || settingsLoading.value || !props.active) return
  if (dirty.value && !settingsPending.value) { discardAction.value = 'reload'; return }
  discardSecretsOnLeave(); void loadSettings()
}
function reconcileSettings(): void {
  const pending = settingsPending.value
  if (!canWrite.value || pending?.state !== 'unknown' || pending.key !== reviewedSettingsKey.value || settingsLoading.value || !settings.value || !reconcileArmed.value) return
  markOperation(props.client, pending.key, 'settled')
  reconcileArmed.value = false; reviewedSettingsKey.value = ''
  draft.value = createPluginSettingsDraft(settings.value.schema, settings.value.values)
  settingsNotice.value = { tone: 'warn', message: '已按你人工核对的当前状态继续编辑；这不是原提交的成功或失败回执。没有重发原请求。' }
}
watch(() => [props.active, props.mode, props.writesEnabled], () => {
  // Permission generations own reads and writes as well as the visible secret draft.
  ++settingsEpoch; settingsLoading.value = false; discardSecretsOnLeave()
  reviewedSettingsKey.value = ''; reconcileArmed.value = false
  if (!props.active) cancelDiscard()
  else if (!settings.value) void loadSettings()
}, { flush: 'sync' })
watch(() => props.pluginId, id => {
  if (id === pluginId.value) { cancelDiscard(); return }
  discardSecretsOnLeave()
  if (dirty.value && !settingsPending.value) { switchTarget = id; discardAction.value = 'switch'; return }
  // Hosts guard normal navigation with requestLeave; forced target changes still fence stale replies.
  void selectSettings(id)
})
watch(() => props.client, () => { void selectSettings(props.pluginId) }, { flush: 'sync' })
onMounted(() => { void loadSettings() })
onBeforeUnmount(() => { endSession(); alive = false })
</script>

<template>
  <ManagementDialog title="插件声明设置" title-id="declared-plugin-settings-title" test-id="declared-plugin-settings-dialog"
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="close">
    <template #eyebrow><span class="eyebrow">PLUGIN SETTINGS</span></template>
    <p class="muted">只显示插件声明的设置；密钥不回显，保存时仅提交本次变更。</p>
    <div v-if="!canWrite" class="notice" data-testid="declared-plugin-settings-readonly">
      当前只读。<button v-if="props.mode !== 'admin'" type="button" @click="emit('enable-admin')">启用管理员操作</button>
    </div>
    <div v-if="discardAction" class="notice warn" role="alert" data-testid="declared-plugin-settings-discard-confirmation">
      <p>当前设置有未保存的草稿。确认放弃并继续？</p>
      <button type="button" data-testid="declared-plugin-settings-discard-confirm" @click="confirmDiscard">放弃草稿并继续</button>
      <button type="button" data-testid="declared-plugin-settings-discard-cancel" @click="cancelDiscard">继续编辑</button>
    </div>
    <section class="settings-panel" aria-labelledby="declared-plugin-settings-heading">
      <h3 id="declared-plugin-settings-heading">插件声明的设置 · {{ pluginId }}</h3>
      <p v-if="settingsLoading" role="status">正在读取设置…</p>
      <ErrorNotice v-if="settingsError" :error="settingsError" />
      <p v-if="settingsNotice" class="notice" :class="settingsNotice.tone" role="status" data-testid="declared-plugin-settings-notice">{{ settingsNotice.message }}</p>
      <div v-if="settingsPending" class="notice warn" role="status" data-testid="declared-plugin-settings-recovery">
        <p>{{ settingsPending.state === 'in-flight' ? '设置正在提交' : '设置保存结果未知' }}。原操作键：<code>{{ settingsPending.key }}</code>。不能换键重发原请求；重新读取只展示当前状态，不能证明原请求未执行。</p>
        <template v-if="settingsPending.state === 'unknown' && reviewedSettingsKey === settingsPending.key">
          <label><input v-model="reconcileArmed" type="checkbox" data-testid="declared-plugin-settings-reconcile-ack">我已人工核对当前设置并确认以当前状态继续，不重复原变更；这不是原请求的回执。</label>
          <button type="button" data-testid="declared-plugin-settings-reconcile" :disabled="!canWrite || !reconcileArmed || settingsLoading" @click="reconcileSettings">按已核对的当前状态继续</button>
        </template>
      </div>
      <DeclaredPluginSettings
        v-if="settings"
        :definitions="settings.schema"
        :draft="draft"
        :configured-secrets="settings.configuredSecrets"
        :disabled="!canWrite || settingsBlocked || settingsLoading"
        @change="onSettingChange"
      />
      <div class="settings-actions">
        <button v-if="settings" type="button" class="primary" data-testid="declared-plugin-settings-save" :disabled="!canWrite || settingsBlocked || settingsLoading || !dirty" @click="saveSettings">
          {{ settingsBusy ? '正在保存…' : '保存设置' }}
        </button>
        <button type="button" data-testid="declared-plugin-settings-reload" :disabled="!props.active || settingsBusy || settingsLoading" @click="reloadSettings">放弃草稿并重新载入</button>
      </div>
      <p v-if="settings?.restartRequired" class="field-hint" data-testid="declared-plugin-settings-restart">当前设置尚未应用；启用插件或修正后重新保存以热应用。</p>
    </section>
  </ManagementDialog>
</template>

<style scoped>
.eyebrow { color: var(--accent-deep); font-size: 0.75rem; font-weight: 650; letter-spacing: .1em; }
.muted, .field-hint { color: var(--text-muted); font-size: 0.8125rem; line-height: 1.7; }
.notice { overflow-wrap: anywhere; border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin-block: 12px; background: var(--surface-muted); color: var(--text-muted); font-size: 0.8125rem; line-height: 1.65; }
.notice.warn { background: var(--warn-bg); color: var(--warn-text); border-color: var(--border-strong); }
.notice.bad { background: var(--bad-bg); color: var(--bad-text); border-color: var(--border-strong); }
.notice p { margin: 0 0 8px; }
.settings-panel { display: grid; gap: 12px; min-width: 0; }
.settings-panel h3 { margin: 0; font-size: 0.875rem; overflow-wrap: anywhere; }
.settings-actions { display: flex; flex-wrap: wrap; gap: 8px; }
button { min-height: 36px; border-radius: 8px; }
@media (max-width: 767px) { .settings-actions > button { flex: 1 1 auto; }button { min-height: 44px; } }
</style>
