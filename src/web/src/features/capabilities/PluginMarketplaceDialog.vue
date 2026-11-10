<script setup lang="ts">
import PluginGovernanceNotice from './PluginGovernanceNotice.vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaInstalledPlugins, type ZbaPluginCatalog, type ZbaPluginOperationKind } from '../../api/zba'
import { isPluginWriteOutcomeUnknown as isUnknown } from './plugin-write-outcome'
import { beginOperation, markOperation, pendingOperations, type PendingOperation } from '../../shared/sync/pending-operations'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { useDraftProtection } from '../../shared/ui/draft-protection'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import PluginCatalogBrowser from './PluginCatalogBrowser.vue'
import PluginRepositoryImport from './PluginRepositoryImport.vue'
import PluginInstallPreview from './PluginInstallPreview.vue'
import PluginLifecyclePreview from './PluginLifecyclePreview.vue'
import type { PluginPreviewSelection } from '../../api/plugin-import'
import type { PluginLifecycleKind } from '../../api/plugin-lifecycle'
import { usePluginLifecyclePreview } from './use-plugin-lifecycle-preview'
import { usePluginPreview } from './use-plugin-preview'
import { pluginAcquisitionUnavailable, type PackageCatalogEntry, type PluginCatalogEntry } from './plugin-catalog'
import { assertPluginReceipt, catalogEntry, mergePluginCatalog, pluginOperationKey, pluginOperationNotice, pluginRecoveryNotice, type PluginNotice } from './plugin-marketplace'

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
const importDraft = ref(false)
const importer = ref<InstanceType<typeof PluginRepositoryImport> | null>(null)
const recoveringKey = ref('')
const packagePending = computed(() => pendingOperations(props.client, 'plugin').filter(record => record.state !== 'settled'))
const unresolvedIds = computed(() => pendingOperations(props.client).filter(record => record.state !== 'settled' && (record.kind === 'plugin' || record.kind === 'plugin-settings')).map(record => record.target))
const entries = computed(() => mergePluginCatalog(catalog.value?.plugins ?? [], installed.value, [], catalog.value?.builtinPlugins ?? []))
const dirty = computed(() => importDraft.value)
const installPreview = usePluginPreview({ client: () => props.client, revision: () => revision.value, canWrite: () => canWrite.value, active: () => props.active, blockedIds: () => unresolvedIds.value })
const lifecyclePreview = usePluginLifecyclePreview({ client: () => props.client, installed: () => installed.value, revision: () => revision.value,
  canWrite: () => canWrite.value && !busy.value && !recoveringKey.value, active: () => props.active, blockedIds: () => unresolvedIds.value })
function invalidatePreviews(): void { installPreview.invalidate(); lifecyclePreview.invalidate() }
const { preview, acknowledged, expired, canConfirm, loading: previewLoading, error: previewError } = installPreview
function requestInstall(entry: PackageCatalogEntry): void {
  if (pluginAcquisitionUnavailable(entry)) return
  void requestCandidate({ origin: 'catalog', manifest: entry.manifest })
}
async function requestCandidate(selection: PluginPreviewSelection): Promise<void> {
  invalidatePreviews()
  const pluginId = selection.origin === 'repository' ? selection.pluginId : selection.manifest.id
  if (installed.value?.plugins.some(item => item.manifest.id === pluginId)) {
    const candidate = selection.origin === 'repository' ? { origin: selection.origin, discoveryId: selection.discoveryId, version: selection.version }
      : { origin: selection.origin, manifest: selection.manifest }
    await lifecyclePreview.request({ kind: 'update', pluginId, candidate })
  } else await installPreview.request(selection)
}
function requestLifecycle(kind: Exclude<PluginLifecycleKind, 'update'>, entry: PackageCatalogEntry): void {
  invalidatePreviews()
  void lifecyclePreview.request({ kind, pluginId: entry.id })
}
function confirmLifecycle(): void {
  if (busy.value) return
  const confirmed = lifecyclePreview.consume()
  if (!confirmed) return
  const manifest = confirmed.targetManifest ?? confirmed.currentManifest
  const entry = catalogEntry({ manifest, enabled: confirmed.enabled, status: confirmed.currentStatus, restartRequired: confirmed.restartRequired })
  void runMutation(confirmed.kind, entry, manifest, confirmed.previewId)
}
function confirmPreview(): void {
  if (busy.value) return
  const confirmed = installPreview.consume()
  if (!confirmed) return
  const entry = catalogEntry({ name: confirmed.metadata?.name || confirmed.manifest.id, summary: '', category: '导入', enabled: false, status: 'available', restartRequired: false, manifest: confirmed.manifest, metadata: confirmed.metadata })
  void runMutation('install', entry, confirmed.manifest, confirmed.previewId)
}
let alive = true
let catalogEpoch = 0
const discardAction = ref<null | 'settings' | 'close' | 'leave'>(null)
let settingsTarget: string | null = null
let leavePromise: Promise<boolean> | null = null
let leaveResolve: ((value: boolean) => void) | null = null

function describe(cause: unknown): UiError { return toUiError(cause) }
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
  // Busy/recovery also spans the read-only refresh after a terminal receipt.
  // Only unsettled writes need operation-key protection when leaving.
  if (!packagePending.value.length) return false
  notice.value = { tone: 'warn', message: '插件操作尚未确认，请先核对原操作键，再离开插件市场。' }
  return true
}
function confirmDiscard(): void {
  if (blockedLeave()) return
  const action = discardAction.value; const target = settingsTarget
  discardAction.value = null; settingsTarget = null; importer.value?.reset(); invalidatePreviews()
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
  if (blockedLeave()) return
  invalidatePreviews(); emit('close')
}
function endSession(): void {
  ++catalogEpoch; cancelDiscard(); invalidatePreviews(); importer.value?.reset(); importDraft.value = false
  catalog.value = null; installed.value = null; revision.value = ''; loading.value = false
  busy.value = false; recoveringKey.value = ''; error.value = null; notice.value = null
}
useDraftProtection(() => dirty.value || packagePending.value.length > 0)
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
async function runMutation(kind: ZbaPluginOperationKind, entry: PackageCatalogEntry, manifest = entry.manifest, previewId?: string): Promise<void> {
  if (!previewId) return
  if (!canWrite.value || busy.value || loading.value || unresolvedIds.value.includes(entry.id)) return
  const client = props.client; const key = pluginOperationKey(kind, entry.id, manifest.version)
  try { beginOperation(client, { key, kind: 'plugin', target: entry.id, meta: { operation: kind, version: manifest.version } }) }
  catch (cause) { error.value = describe(cause); return }
  busy.value = true; error.value = null; notice.value = null
  try {
    const result = kind === 'uninstall'
      ? await client.uninstallPlugin(entry.id, { previewId, trustAcknowledged: true, expectedRevision: revision.value, idempotencyKey: key })
      : kind === 'update'
        ? await client.updatePlugin({ manifest, previewId, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
        : kind === 'rollback'
          ? await client.rollbackPlugin({ manifest, previewId, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
          : await client.installPlugin({ manifest, previewId, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
    assertPluginReceipt(result.operation, entry.id, kind)
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
    assertPluginReceipt(result.operation, pending.target, pending.meta.operation)
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
function openSettings(entry: PluginCatalogEntry): void {
  if (!props.active || blockedLeave()) return
  if (dirty.value) { settingsTarget = entry.id; discardAction.value = 'settings'; return }
  emit('open-settings', entry.id)
}
watch(() => [props.mode, props.writesEnabled], () => {
  // Catalog reads are permission-independent; do not abandon them on admin enable.
  invalidatePreviews()
}, { flush: 'sync' })
watch(() => props.active, active => {
  invalidatePreviews()
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
      内置能力无需安装；扩展包默认停用。安装、更新、卸载的生效状态以操作回执为准，可能等待热应用或需要重启宿主。清单下载地址仅由服务端解析；
      此处也不会回显任何密钥原文。
    </p>
    <div v-if="!canWrite" class="notice" data-testid="marketplace-readonly">
      当前只读。<button v-if="props.mode !== 'admin'" type="button" @click="emit('enable-admin')">启用管理员操作</button>
    </div>
    <p v-if="loading" role="status">正在读取市场目录…</p>
    <ErrorNotice v-if="error" :error="error" />
    <p v-if="notice" class="notice" :class="notice.tone" role="status" data-testid="marketplace-notice">{{ notice.message }}</p>

    <PluginGovernanceNotice :source="catalog?.governanceSource ?? installed?.governanceSource" source-only />
    <p v-if="catalog?.metadataNotice" class="notice warn" role="status" data-testid="metadata-notice">{{ catalog.metadataNotice }}</p>
    <div v-for="pending in packagePending" :key="pending.key" class="notice warn" role="status" data-testid="marketplace-recovery">
      <p>{{ pending.target }} · {{ pending.meta.operation }}：{{ pending.state === 'in-flight' ? '正在提交' : '结果未知' }}，原操作键已保留。不要换键重复提交。</p>
      <code>{{ pending.key }}</code>
      <button type="button" data-testid="marketplace-recovery-check" :disabled="Boolean(recoveringKey) || pending.state === 'in-flight' || !props.active" @click="checkRecovery(pending)">
        {{ recoveringKey === pending.key ? '正在核对…' : '用原操作键核对结果' }}
      </button>
    </div>
    <div v-if="discardAction" class="notice warn" role="alert" data-testid="marketplace-discard-confirmation">
      <p>仓库地址或 JSON 导入草稿尚未处理。确认放弃并继续？</p>
      <button type="button" data-testid="marketplace-discard-confirm" @click="confirmDiscard">放弃草稿并继续</button>
      <button type="button" data-testid="marketplace-discard-cancel" @click="cancelDiscard">继续编辑</button>
    </div>

    <PluginCatalogBrowser
      :entries="entries"
      :focus-plugin-id="props.focusPluginId"
      :can-write="canWrite"
      :unresolved-ids="unresolvedIds"
      :busy="busy || previewLoading"
      :loading="loading"
      @install="requestInstall"
      @selection-change="invalidatePreviews"
      @uninstall="(entry) => requestLifecycle('uninstall', entry)"
      @rollback="(entry) => requestLifecycle('rollback', entry)"
      @settings="(entry) => openSettings(entry)"
    />

    <p v-if="previewLoading" role="status">正在只读预览；尚未下载或安装插件…</p>
    <p v-if="previewError" class="notice bad" role="alert" data-testid="preview-error">{{ previewError }}</p>
    <PluginInstallPreview v-if="preview" :preview="preview" v-model:acknowledged="acknowledged" :can-confirm="canConfirm" :expired="expired" :busy="busy" @confirm="confirmPreview" @cancel="installPreview.invalidate" />
    <p v-if="lifecyclePreview.loading.value" class="read-only" role="status">正在生成变更预览；尚未提交变更…</p>
    <p v-if="lifecyclePreview.error.value" class="notice bad" role="alert" data-testid="lifecycle-error">{{ lifecyclePreview.error.value }}</p>
    <PluginLifecyclePreview v-if="lifecyclePreview.preview.value" :preview="lifecyclePreview.preview.value" :acknowledged="lifecyclePreview.acknowledged.value"
      :expired="lifecyclePreview.expired.value" :can-confirm="lifecyclePreview.canConfirm.value" @update:acknowledged="lifecyclePreview.acknowledged.value = $event"
      @confirm="confirmLifecycle" @cancel="lifecyclePreview.invalidate" />
    <PluginRepositoryImport ref="importer" :client="props.client" :can-write="canWrite" :active="props.active" :busy="busy || previewLoading" @preview="requestCandidate" @invalidate="invalidatePreviews" @draft-change="importDraft = $event" />

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
