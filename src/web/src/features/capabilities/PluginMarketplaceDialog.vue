<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaInstalledPlugins, type ZbaPluginCatalog, type ZbaPluginOperationKind } from '../../api/zba'
import { beginOperation, markOperation, pendingOperations, type PendingOperation } from '../../shared/sync/pending-operations'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { useDraftProtection } from '../../shared/ui/draft-protection'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import PluginCatalogBrowser from './PluginCatalogBrowser.vue'
import { githubSourceError } from './plugin-manifest'
import type { PluginCatalogEntry } from './plugin-catalog'
import { mergePluginCatalog, pluginOperationKey, pluginOperationNotice, pluginRecoveryNotice, type PluginNotice } from './plugin-marketplace'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
  focusPluginId?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>(), { active: true, focusPluginId: undefined, trigger: null, fallback: null })
const emit = defineEmits<{ close: []; changed: []; 'open-settings': [pluginId: string]; 'enable-admin': []; 'auth-expired': [error: UiError] }>()
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
const entries = computed(() => mergePluginCatalog(catalog.value?.plugins ?? [], installed.value, sourceEntries.value))
const dirty = computed(() => Boolean(sourceUrl.value.trim()))
let alive = true
let catalogEpoch = 0
let sourceEpoch = 0
const discardAction = ref<null | 'settings' | 'close' | 'leave'>(null)
let settingsTarget: string | null = null
let leavePromise: Promise<boolean> | null = null
let leaveResolve: ((value: boolean) => void) | null = null

function describe(cause: unknown): UiError { return toUiError(cause) }
function isUnknown(cause: unknown): boolean {
  // A response decoding failure after a write is not proof that it was rejected.
  if (cause instanceof ZbaNetworkError) return true
  if (cause instanceof ZbaRequestError && cause.code === 'plugin_reload_failed') return false
  if (cause instanceof ZbaRequestError) return cause.status >= 500 || cause.status === 0 || cause.code === 'invalid_json'
  return true
}
function live(client: ZbaClient): boolean { return alive && props.active && client === props.client }
function showError(cause: unknown): void {
  const value = describe(cause)
  if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
  else error.value = value
}
function discardSecretsOnLeave(): void { /* Marketplace no longer owns any secret fields. */ }
function cancelDiscard(): void {
  discardAction.value = null; settingsTarget = null
  leaveResolve?.(false); leaveResolve = null; leavePromise = null
}
function blockedLeave(): boolean {
  if (!busy.value && !sourceBusy.value && !recoveringKey.value && !packagePending.value.length) return false
  notice.value = { tone: 'warn', message: '插件操作尚未确认，请先核对原操作键，再离开插件市场。' }
  return true
}
function confirmDiscard(): void {
  if (blockedLeave()) return
  const action = discardAction.value; const target = settingsTarget
  discardAction.value = null; settingsTarget = null; sourceUrl.value = ''
  if (action === 'settings' && target) emit('open-settings', target)
  else if (action === 'close') emit('close')
  else if (action === 'leave') { leaveResolve?.(true); leaveResolve = null; leavePromise = null }
}
function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', _trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock' || reason === 'navigate') { cancelDiscard(); return Promise.resolve(true) }
  if (packagePending.value.length) return Promise.resolve(true)
  if (!dirty.value) return Promise.resolve(true)
  if (leavePromise) return leavePromise
  cancelDiscard(); discardAction.value = 'leave'
  leavePromise = new Promise(resolve => { leaveResolve = resolve })
  return leavePromise
}
function close(): void {
  if (discardAction.value) { cancelDiscard(); return }
  if (dirty.value) { discardAction.value = 'close'; return }
  emit('close')
}
function endSession(): void {
  ++catalogEpoch; ++sourceEpoch; cancelDiscard()
  sourceUrl.value = ''; sourceEntries.value = []; sourceBusy.value = false; sourceError.value = ''
  catalog.value = null; installed.value = null; revision.value = ''; loading.value = false
  busy.value = false; recoveringKey.value = ''; error.value = null; notice.value = null
}
useDraftProtection(() => dirty.value || packagePending.value.length > 0 || busy.value)
defineExpose({ requestLeave, discardSecretsOnLeave, endSession, draftCount: computed(() => dirty.value ? 1 : 0) })

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
  else showError(indexResult.reason)
  if (installedResult.status === 'fulfilled') {
    installed.value = installedResult.value
    if (installedResult.value) revision.value = installedResult.value.revision
  } else showError(installedResult.reason)
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
    else { showError(cause); notice.value = pluginRecoveryNotice(cause) }
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
    if (!notice.value) showError(cause)
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
  } catch (cause) { if (live(client) && epoch === sourceEpoch) showError(cause) }
  finally { if (alive && client === props.client && epoch === sourceEpoch) sourceBusy.value = false }
}
function openSettings(entry: PluginCatalogEntry): void {
  if (!props.active || blockedLeave()) return
  if (dirty.value) { settingsTarget = entry.id; discardAction.value = 'settings'; return }
  emit('open-settings', entry.id)
}
watch(() => [props.mode, props.writesEnabled], () => {
  // Catalog reads are permission-independent; do not abandon them on admin enable.
  ++sourceEpoch; sourceBusy.value = false
}, { flush: 'sync' })
watch(() => props.active, active => {
  ++sourceEpoch; sourceBusy.value = false
  if (!active) { ++catalogEpoch; loading.value = false; cancelDiscard() }
  else void loadCatalog()
}, { flush: 'sync' })
watch(() => props.client, () => { endSession(); void loadCatalog() }, { flush: 'sync' })
onMounted(() => { void loadCatalog() })
onBeforeUnmount(() => { endSession(); alive = false })
</script>

<template>
  <ManagementDialog title="插件市场" title-id="marketplace-title" test-id="marketplace" wide
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="close">
    <template #eyebrow><span class="eyebrow">PLUGIN MARKETPLACE</span></template>
    <p class="muted">
      外部插件默认停用；安装、更新、卸载与设置支持热应用，无需重启宿主。清单里的下载地址只由服务端解析，浏览器不会访问它；
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
      <p>当前数据源地址尚未处理。确认放弃并继续？</p>
      <button type="button" data-testid="marketplace-discard-confirm" @click="confirmDiscard">放弃草稿并继续</button>
      <button type="button" data-testid="marketplace-discard-cancel" @click="cancelDiscard">继续编辑</button>
    </div>

    <PluginCatalogBrowser
      :entries="entries"
      :focus-plugin-id="props.focusPluginId"
      :can-write="canWrite"
      :unresolved-ids="unresolvedIds"
      :busy="busy"
      :loading="loading"
      @install="(entry) => runMutation(entry.installedVersion ? 'update' : 'install', entry)"
      @uninstall="(entry) => runMutation('uninstall', entry)"
      @settings="(entry) => openSettings(entry)"
    />

  </ManagementDialog>
</template>

<style scoped>
.eyebrow { color: var(--accent-deep); font-size: 0.75rem; font-weight: 650; letter-spacing: .1em; }
.muted, .field-hint { color: var(--text-muted); font-size: 0.8125rem; line-height: 1.7; }
.notice { overflow-wrap: anywhere; border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin-block: 12px; background: var(--surface-muted); color: var(--text-muted); font-size: 0.8125rem; line-height: 1.65; }
.notice.warn { background: var(--warn-bg); color: var(--warn-text); border-color: var(--border-strong); }
.notice.bad { background: var(--bad-bg); color: var(--bad-text); border-color: var(--border-strong); }
.notice p { margin: 0 0 8px; }
.source-panel { border: 1px solid var(--border); border-radius: 8px; padding: 18px; margin: 22px 0; display: grid; gap: 12px; min-width: 0; background: var(--surface); }
.source-panel { background: var(--surface-muted); }
.source-panel h3 { margin: 0; font-size: 0.875rem; overflow-wrap: anywhere; letter-spacing: -.01em; }
.source-panel .field-hint { margin: 0; font-size: 0.75rem; }
.source-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: flex-end; }
.source-row .grow { flex: 1 1 260px; display: grid; gap: 6px; font-size: 0.75rem; font-weight: 600; color: var(--text-muted); min-width: 0; }
.source-row input { width: 100%; min-width: 0; min-height: 36px; border-radius: 8px; background: var(--surface); }
button { min-height: 36px; border-radius: 8px; }
@media (max-width: 767px) { .source-panel { padding: 14px; }.source-row > button { flex: 1 1 auto; }button, .source-row input { min-height: 44px; } }
</style>
