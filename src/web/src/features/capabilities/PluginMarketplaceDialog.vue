<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaInstalledPlugins, type ZbaPluginCatalog, type ZbaPluginOperationKind, type ZbaPluginSettings } from '../../api/zba'
import { beginOperation, getPendingOperation, markOperation, pendingOperations, type PendingOperation } from '../../shared/sync/pending-operations'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import DeclaredPluginSettings from './DeclaredPluginSettings.vue'
import PluginCatalogBrowser from './PluginCatalogBrowser.vue'
import { clearPluginSecretDrafts, createPluginSettingsDraft, pluginSettingsErrors, type PluginSettingDraft, type PluginSettingsDraft } from './declared-plugin-settings'
import { githubSourceError } from './plugin-manifest'
import type { PluginCatalogEntry } from './plugin-catalog'
import { mergePluginCatalog, pluginOperationKey, pluginOperationNotice, pluginRecoveryNotice, pluginSettingEdits, pluginSettingsDirty, pluginSettingsKey, type PluginNotice } from './plugin-marketplace'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
  focusPluginId?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>(), { active: true, focusPluginId: undefined, trigger: null, fallback: null })
const emit = defineEmits<{ close: []; changed: []; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)
const catalog = ref<ZbaPluginCatalog | null>(null)
const installed = ref<ZbaInstalledPlugins | null>(null)
const revision = ref('')
const loading = ref(false)
const busy = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<PluginNotice | null>(null)
const sourceUrl = ref('')
const sourceError = ref('')
const sourceBusy = ref(false)
const sourceEntries = ref<PluginCatalogEntry[]>([])
const recoveringKey = ref('')
const packagePending = computed(() => pendingOperations(props.client, 'plugin').filter(record => record.state !== 'settled'))
const unresolvedIds = computed(() => pendingOperations(props.client).filter(record => record.state !== 'settled' && (record.kind === 'plugin' || record.kind === 'plugin-settings')).map(record => record.target))
const settingsEntry = ref<PluginCatalogEntry | null>(null)
const settings = ref<ZbaPluginSettings | null>(null)
const draft = ref<PluginSettingsDraft>({})
const settingsLoading = ref(false)
const settingsError = ref<UiError | null>(null)
const settingsNotice = ref<PluginNotice | null>(null)
const settingsPending = computed(() => settingsEntry.value ? getPendingOperation(props.client, 'plugin-settings', settingsEntry.value.id) : null)
const settingsBusy = computed(() => settingsPending.value?.state === 'in-flight')
const settingsBlocked = computed(() => Boolean(settingsPending.value || (settingsEntry.value && getPendingOperation(props.client, 'plugin', settingsEntry.value.id))))
const reviewedSettingsKey = ref('')
const reconcileArmed = ref(false)
const entries = computed(() => mergePluginCatalog(catalog.value?.plugins ?? [], installed.value, sourceEntries.value))
const dirty = computed(() => pluginSettingsDirty(draft.value))
let alive = true
let settingsEpoch = 0
let catalogEpoch = 0
let sourceEpoch = 0
const discardAction = ref<null | 'switch' | 'close' | 'leave'>(null)
let switchTarget: PluginCatalogEntry | null = null
let leaveResolve: ((value: boolean) => void) | null = null

function describe(cause: unknown): UiError { return toUiError(cause) }
function isUnknown(cause: unknown): boolean {
  // A response decoding failure after a write is not proof that it was rejected.
  if (cause instanceof ZbaNetworkError) return true
  if (cause instanceof ZbaRequestError) return cause.status >= 500 || cause.status === 0 || cause.code === 'invalid_json'
  return true
}
function live(client: ZbaClient): boolean { return alive && props.active && client === props.client }
function ownsSettings(client: ZbaClient, id: string, epoch: number): boolean { return live(client) && settingsEntry.value?.id === id && epoch === settingsEpoch }
function showError(cause: unknown, target: 'settings' | 'catalog'): void {
  const value = describe(cause)
  if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
  else if (target === 'settings') settingsError.value = value
  else error.value = value
}
function discardSecretsOnLeave(): void {
  clearPluginSecretDrafts(settings.value?.schema ?? settingsEntry.value?.manifest.settings ?? [], draft.value)
}
function cancelDiscard(): void {
  discardAction.value = null; switchTarget = null
  leaveResolve?.(false); leaveResolve = null
}
function confirmDiscard(): void {
  const action = discardAction.value; const target = switchTarget
  discardAction.value = null; switchTarget = null
  discardSecretsOnLeave()
  if (action === 'switch' && target) void selectSettings(target)
  else if (action === 'close') emit('close')
  else if (action === 'leave') { leaveResolve?.(true); leaveResolve = null }
}
async function requestLeave(reason: 'lock' | 'downgrade', _trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') { cancelDiscard(); discardSecretsOnLeave(); return true }
  // Already-dispatched requests live in the shared registry; never delay authentication cleanup.
  if (!dirty.value || settingsPending.value) { discardSecretsOnLeave(); return true }
  cancelDiscard(); discardAction.value = 'leave'
  return new Promise(resolve => { leaveResolve = resolve })
}
function close(): void {
  if (discardAction.value) { cancelDiscard(); return }
  if (dirty.value && !settingsPending.value) { discardAction.value = 'close'; return }
  discardSecretsOnLeave(); emit('close')
}
defineExpose({ requestLeave, discardSecretsOnLeave })

async function loadCatalog(showLoading = true): Promise<void> {
  if (!props.active) return
  const client = props.client; const epoch = ++catalogEpoch
  if (showLoading) loading.value = true
  error.value = null
  // Independent reads: an index failure must not hide installed-only management rows.
  const [indexResult, installedResult] = await Promise.allSettled([
    client.pluginCatalog(),
    typeof client.installedPlugins === 'function' ? client.installedPlugins() : Promise.resolve(null)
  ])
  if (!live(client) || epoch !== catalogEpoch) return
  if (indexResult.status === 'fulfilled') { catalog.value = indexResult.value; revision.value = indexResult.value.revision }
  else showError(indexResult.reason, 'catalog')
  if (installedResult.status === 'fulfilled') {
    installed.value = installedResult.value
    if (installedResult.value) revision.value = installedResult.value.revision
  } else showError(installedResult.reason, 'catalog')
  loading.value = false
}
async function runMutation(kind: ZbaPluginOperationKind, entry: PluginCatalogEntry, manifest = entry.manifest): Promise<void> {
  if (!canWrite.value || busy.value || loading.value || unresolvedIds.value.includes(entry.id)) return
  const client = props.client; const key = pluginOperationKey(kind, entry.id, manifest.version)
  try { beginOperation(client, { key, kind: 'plugin', target: entry.id, meta: { operation: kind, version: manifest.version } }) }
  catch (cause) { error.value = describe(cause); return }
  busy.value = true; error.value = null; notice.value = null
  try {
    const result = kind === 'uninstall'
      ? await client.uninstallPlugin(entry.id, { expectedRevision: revision.value, idempotencyKey: key })
      : kind === 'update'
        ? await client.updatePlugin({ manifest, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
        : await client.installPlugin({ manifest, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
    markOperation(client, key, 'settled')
    if (!live(client)) return
    revision.value = result.revision; notice.value = pluginOperationNotice(result.operation)
    emit('changed'); await loadCatalog(false)
  } catch (cause) {
    markOperation(client, key, isUnknown(cause) ? 'unknown' : 'settled')
    if (!live(client)) return
    if (isUnknown(cause)) notice.value = { tone: 'warn', message: '提交结果未知：原操作键已保留，请核对，不要换键重复提交。' }
    else { showError(cause, 'catalog'); notice.value = pluginRecoveryNotice(cause) }
  } finally { if (alive && client === props.client) busy.value = false }
}
async function checkRecovery(pending: PendingOperation): Promise<void> {
  if (!props.active || recoveringKey.value || pending.state === 'in-flight') return
  const client = props.client; recoveringKey.value = pending.key
  try {
    const result = await client.pluginOperation(pending.key)
    if (result.operation.pluginId !== pending.target || result.operation.kind !== pending.meta.operation) throw new ZbaRequestError(200, 'invalid_json', 'Operation receipt target mismatch')
    markOperation(client, pending.key, 'settled')
    if (!live(client)) return
    revision.value = result.revision; notice.value = pluginOperationNotice(result.operation)
    emit('changed'); await loadCatalog(false)
  } catch (cause) {
    // Not-found, expired or even an idempotency mismatch is not a receipt for this original write.
    if (!live(client)) return
    notice.value = pluginRecoveryNotice(cause)
    if (!notice.value) showError(cause, 'catalog')
  } finally { if (alive && client === props.client) recoveringKey.value = '' }
}
async function resolveSource(): Promise<void> {
  if (!canWrite.value || sourceBusy.value) return
  const raw = sourceUrl.value.trim(); sourceError.value = ''; notice.value = null
  if (!raw) { sourceError.value = '请填写数据源地址：GitHub 仓库主页或固定版本 Release。'; return }
  const problem = githubSourceError(raw)
  if (problem) { sourceError.value = problem; return }
  sourceBusy.value = true; const client = props.client; const epoch = ++sourceEpoch
  try {
    const result = await client.resolvePluginSource(raw)
    if (!live(client) || epoch !== sourceEpoch) return
    const entry: PluginCatalogEntry = { id: result.manifest.id, name: result.manifest.id, summary: '来自指定数据源。安装状态以宿主读取为准。', category: '指定数据源', repository: raw.split('/releases/tag/')[0], manifest: result.manifest }
    sourceEntries.value = [...sourceEntries.value.filter(item => item.id !== entry.id), entry]
    notice.value = { tone: 'warn', message: result.trustNotice }
  } catch (cause) { if (live(client) && epoch === sourceEpoch) showError(cause, 'catalog') }
  finally { if (alive && client === props.client && epoch === sourceEpoch) sourceBusy.value = false }
}
async function openSettings(entry: PluginCatalogEntry): Promise<void> {
  if (settingsEntry.value?.id === entry.id && settings.value) return
  if (dirty.value && !settingsPending.value) { switchTarget = entry; discardAction.value = 'switch'; return }
  await selectSettings(entry)
}
async function selectSettings(entry: PluginCatalogEntry): Promise<void> {
  discardSecretsOnLeave(); ++settingsEpoch
  settingsEntry.value = entry; settings.value = null; draft.value = {}
  settingsError.value = null; settingsNotice.value = null; reviewedSettingsKey.value = ''; reconcileArmed.value = false
  await loadSettings(entry)
}
async function loadSettings(entry: PluginCatalogEntry): Promise<void> {
  const client = props.client; const epoch = ++settingsEpoch
  settingsLoading.value = true; settingsError.value = null; reviewedSettingsKey.value = ''; reconcileArmed.value = false
  try {
    const result = await client.pluginSettings(entry.id)
    if (!ownsSettings(client, entry.id, epoch)) return
    settings.value = result; draft.value = createPluginSettingsDraft(result.schema, result.values)
    const pending = getPendingOperation(client, 'plugin-settings', entry.id)
    if (pending?.state === 'unknown') reviewedSettingsKey.value = pending.key
  } catch (cause) { if (ownsSettings(client, entry.id, epoch)) showError(cause, 'settings') }
  finally { if (alive && client === props.client && settingsEntry.value?.id === entry.id && epoch === settingsEpoch) settingsLoading.value = false }
}
function onSettingChange(key: string, value: PluginSettingDraft): void {
  if (!canWrite.value || settingsBlocked.value || settingsLoading.value) return
  draft.value = { ...draft.value, [key]: value }
}
async function saveSettings(): Promise<void> {
  const entry = settingsEntry.value; const current = settings.value
  if (!entry || !current || !canWrite.value || settingsBlocked.value || settingsLoading.value) return
  const problems = pluginSettingsErrors(current.schema, draft.value)
  if (problems.length) { settingsNotice.value = { tone: 'bad', message: problems.join(' ') }; return }
  const edits = pluginSettingEdits(current.schema, draft.value)
  if (!edits.length) return
  const client = props.client; const key = pluginSettingsKey(entry.id); const epoch = settingsEpoch
  try { beginOperation(client, { key, kind: 'plugin-settings', target: entry.id }) }
  catch (cause) { settingsError.value = describe(cause); return }
  settingsError.value = null; settingsNotice.value = null; reviewedSettingsKey.value = ''; reconcileArmed.value = false
  // No request body/secret is retained for retries. The transport owns the dispatched copy.
  discardSecretsOnLeave(); draft.value = createPluginSettingsDraft(current.schema, current.values)
  try {
    const result = await client.savePluginSettings(entry.id, { expectedRevision: current.revision, idempotencyKey: key, edits })
    markOperation(client, key, 'settled')
    if (!ownsSettings(client, entry.id, epoch)) return
    settings.value = result; draft.value = createPluginSettingsDraft(result.schema, result.values)
    settingsNotice.value = { tone: result.restartRequired ? 'warn' : 'ok', message: result.restartRequired ? '设置已保存，重启 BotAgent 后生效。' : '设置已保存。' }
    emit('changed')
  } catch (cause) {
    markOperation(client, key, isUnknown(cause) ? 'unknown' : 'settled')
    if (!ownsSettings(client, entry.id, epoch)) return
    if (isUnknown(cause)) settingsNotice.value = { tone: 'warn', message: '保存结果未知，原操作键已保留。重读仅展示当前设置，不证明原请求未执行，不要重复提交。' }
    else { showError(cause, 'settings'); settingsNotice.value = pluginRecoveryNotice(cause) }
  }
}
function reloadSettings(): void {
  const entry = settingsEntry.value
  if (!entry || settingsBusy.value || settingsLoading.value || !props.active) return
  discardSecretsOnLeave(); void loadSettings(entry)
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
  if (!canWrite.value) discardSecretsOnLeave()
  if (!props.active) { ++settingsEpoch; ++catalogEpoch; ++sourceEpoch; settingsLoading.value = false; loading.value = false; sourceBusy.value = false; cancelDiscard() }
})
watch(() => props.client, () => {
  discardSecretsOnLeave(); ++settingsEpoch; ++catalogEpoch; ++sourceEpoch
  cancelDiscard(); catalog.value = null; installed.value = null; sourceEntries.value = []; settingsEntry.value = null; settings.value = null; draft.value = {}
  busy.value = false; loading.value = false; sourceBusy.value = false; recoveringKey.value = ''; error.value = null; notice.value = null
  if (props.active) void loadCatalog()
})
onMounted(async () => {
  await loadCatalog()
  if (!alive || !props.active || !props.focusPluginId) return
  const target = entries.value.find(entry => entry.id === props.focusPluginId)
  if (target) await openSettings(target)
})
onBeforeUnmount(() => { discardSecretsOnLeave(); cancelDiscard(); alive = false; ++settingsEpoch; ++catalogEpoch; ++sourceEpoch })
</script>

<template>
  <ManagementDialog title="插件市场" title-id="marketplace-title" test-id="marketplace" wide
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="close">
    <template #eyebrow><span class="eyebrow">PLUGIN MARKETPLACE</span></template>
    <p class="muted">
      外部插件默认停用，安装、更新、卸载与设置都需要重启后生效。清单里的下载地址只由服务端解析，浏览器不会访问它；
      此处也不会回显任何密钥原文。
    </p>
    <div v-if="!canWrite" class="notice" data-testid="marketplace-readonly">
      当前只读。<button v-if="props.mode !== 'admin'" type="button" @click="emit('enable-admin')">启用管理员操作</button>
    </div>
    <p v-if="loading" role="status">正在读取市场目录…</p>
    <ErrorNotice v-if="error" :error="error" />
    <p v-if="notice" class="notice" :class="notice.tone" role="status" data-testid="marketplace-notice">{{ notice.message }}</p>

    <section class="source-panel" aria-labelledby="marketplace-source-heading">
      <h3 id="marketplace-source-heading">指定数据源</h3>
      <p class="field-hint">支持公开 GitHub 仓库主页或 <code>/releases/tag/固定版本</code>；不接受任意下载地址、查询参数或凭据。</p>
      <div class="source-row">
        <label class="grow">数据源地址
          <input v-model="sourceUrl" data-testid="marketplace-source-input" type="url" autocomplete="off" spellcheck="false"
            placeholder="https://github.com/example-org/example-plugin" :disabled="sourceBusy">
        </label>
        <button type="button" class="tonal" data-testid="marketplace-source-resolve" :disabled="sourceBusy || !canWrite" @click="resolveSource">
          {{ sourceBusy ? '解析中…' : '解析清单' }}
        </button>
      </div>
      <p v-if="sourceError" class="notice bad" role="alert" data-testid="marketplace-source-error">{{ sourceError }}</p>
    </section>

    <div v-for="pending in packagePending" :key="pending.key" class="notice warn" role="status" data-testid="marketplace-recovery">
      <p>{{ pending.target }} · {{ pending.meta.operation }}：{{ pending.state === 'in-flight' ? '正在提交' : '结果未知' }}，原操作键已保留。不要换键重复提交。</p>
      <code>{{ pending.key }}</code>
      <button type="button" data-testid="marketplace-recovery-check" :disabled="Boolean(recoveringKey) || pending.state === 'in-flight' || !props.active" @click="checkRecovery(pending)">
        {{ recoveringKey === pending.key ? '正在核对…' : '用原操作键核对结果' }}
      </button>
    </div>
    <div v-if="discardAction" class="notice warn" role="alert" data-testid="marketplace-discard-confirmation">
      <p>当前设置有未保存的草稿。确认放弃并继续？</p>
      <button type="button" data-testid="marketplace-discard-confirm" @click="confirmDiscard">放弃草稿并继续</button>
      <button type="button" data-testid="marketplace-discard-cancel" @click="cancelDiscard">继续编辑</button>
    </div>

    <PluginCatalogBrowser
      :entries="entries"
      :can-write="canWrite"
      :unresolved-ids="unresolvedIds"
      :busy="busy"
      :loading="loading"
      @install="(entry) => runMutation(entry.installedVersion ? 'update' : 'install', entry)"
      @uninstall="(entry) => runMutation('uninstall', entry)"
      @settings="(entry) => openSettings(entry)"
    />

    <section v-if="settingsEntry" class="settings-panel" aria-labelledby="marketplace-settings-heading">
      <h3 id="marketplace-settings-heading">插件声明的设置 · {{ settingsEntry.name }}</h3>
      <p v-if="settingsLoading" role="status">正在读取设置…</p>
      <ErrorNotice v-if="settingsError" :error="settingsError" />
      <p v-if="settingsNotice" class="notice" :class="settingsNotice.tone" role="status" data-testid="marketplace-settings-notice">{{ settingsNotice.message }}</p>
      <div v-if="settingsPending" class="notice warn" role="status" data-testid="marketplace-settings-recovery">
        <p>{{ settingsPending.state === 'in-flight' ? '设置正在提交' : '设置保存结果未知' }}。原操作键：<code>{{ settingsPending.key }}</code>。不能换键重发原请求；重新读取只展示当前状态，不能证明原请求未执行。</p>
        <template v-if="settingsPending.state === 'unknown' && reviewedSettingsKey === settingsPending.key">
          <label><input v-model="reconcileArmed" type="checkbox" data-testid="marketplace-settings-reconcile-ack">我已人工核对当前设置并确认以当前状态继续，不重复原变更；这不是原请求的回执。</label>
          <button type="button" data-testid="marketplace-settings-reconcile" :disabled="!canWrite || !reconcileArmed || settingsLoading" @click="reconcileSettings">按已核对的当前状态继续</button>
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
      <div v-if="settings" class="settings-actions">
        <button type="button" class="primary" data-testid="marketplace-settings-save" :disabled="!canWrite || settingsBlocked || settingsLoading || !dirty" @click="saveSettings">
          {{ settingsBusy ? '正在保存…' : '保存设置' }}
        </button>
        <button type="button" data-testid="marketplace-settings-reload" :disabled="settingsBusy || settingsLoading" @click="reloadSettings">放弃草稿并重新载入</button>
      </div>
      <p v-if="settings?.restartRequired" class="field-hint" data-testid="marketplace-settings-restart">当前设置标记为需重启；保存后请手动重启 BotAgent。</p>
    </section>
  </ManagementDialog>
</template>

<style scoped>
.eyebrow { color: var(--accent-deep); font-size: 11px; font-weight: 650; letter-spacing: .1em; }
.muted, .field-hint { color: var(--text-muted); font-size: 13px; line-height: 1.7; }
.notice { overflow-wrap: anywhere; border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin-block: 12px; background: var(--surface-muted); color: var(--text-muted); font-size: 13px; line-height: 1.65; }
.notice.warn { background: var(--warn-bg); color: var(--warn-text); border-color: var(--border-strong); }
.notice.bad { background: var(--bad-bg); color: var(--bad-text); border-color: var(--border-strong); }
.notice p { margin: 0 0 8px; }
.source-panel, .settings-panel { border: 1px solid var(--border); border-radius: 8px; padding: 18px; margin: 22px 0; display: grid; gap: 12px; min-width: 0; background: var(--surface); }
.source-panel { background: var(--surface-muted); }
.source-panel h3, .settings-panel h3 { margin: 0; font-size: 14px; overflow-wrap: anywhere; letter-spacing: -.01em; }
.source-panel .field-hint { margin: 0; font-size: 12px; }
.source-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: flex-end; }
.source-row .grow { flex: 1 1 260px; display: grid; gap: 6px; font-size: 12px; font-weight: 600; color: var(--text-muted); min-width: 0; }
.source-row input { width: 100%; min-width: 0; min-height: 36px; border-radius: 8px; background: var(--surface); }
.settings-actions { display: flex; flex-wrap: wrap; gap: 8px; }
button { min-height: 36px; border-radius: 8px; }
@media (max-width: 767px) { .source-panel, .settings-panel { padding: 14px; }.source-row > button, .settings-actions > button { flex: 1 1 auto; }button, .source-row input { min-height: 44px; } }
</style>
