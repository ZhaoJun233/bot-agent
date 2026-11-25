<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaInstalledPlugins, type ZbaPluginCatalog, type ZbaPluginOperationKind } from '../../api/zba'
import type { PluginPreviewSelection } from '../../api/plugin-import'
import type { PluginLifecycleKind } from '../../api/plugin-lifecycle'
import { beginOperation, markOperation, pendingOperations, type PendingOperation } from '../../shared/sync/pending-operations'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import UiBanner from '../../shared/ui/UiBanner.vue'
import { useDraftProtection } from '../../shared/ui/draft-protection'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import PluginCatalogBrowser from '../capabilities/PluginCatalogBrowser.vue'
import PluginGovernanceNotice from '../capabilities/PluginGovernanceNotice.vue'
import PluginInstallPreview from '../capabilities/PluginInstallPreview.vue'
import PluginLifecyclePreview from '../capabilities/PluginLifecyclePreview.vue'
import PluginRepositoryImport from '../capabilities/PluginRepositoryImport.vue'
import { pluginAcquisitionUnavailable, type PackageCatalogEntry, type PluginCatalogEntry } from '../capabilities/plugin-catalog'
import { assertPluginReceipt, catalogEntry, mergePluginCatalog, pluginOperationKey, pluginOperationNotice, pluginRecoveryNotice, type PluginNotice } from '../capabilities/plugin-marketplace'
import { isPluginWriteOutcomeUnknown as isUnknown } from '../capabilities/plugin-write-outcome'
import { usePluginLifecyclePreview } from '../capabilities/use-plugin-lifecycle-preview'
import { usePluginPreview } from '../capabilities/use-plugin-preview'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
  section: 'catalog' | 'import'
  focusPluginId?: string
}>(), { active: true, focusPluginId: undefined })

const emit = defineEmits<{
  changed: []
  'open-settings': [pluginId: string]
  'open-ui': [pluginId: string]
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)
const catalog = ref<ZbaPluginCatalog | null>(null)
const installed = ref<ZbaInstalledPlugins | null>(null)
const metadataFailures = computed(() => installed.value?.metadataFailures ?? catalog.value?.metadataFailures ?? [])
const revision = ref('')
const loading = ref(false)
const busy = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<PluginNotice | null>(null)
const importDraft = ref(false)
const importer = ref<InstanceType<typeof PluginRepositoryImport> | null>(null)
const recoveringKey = ref('')

const packagePending = computed(() => pendingOperations(props.client, 'plugin').filter(record => record.state !== 'settled'))
const unresolvedIds = computed(() =>
  pendingOperations(props.client)
    .filter(record => record.state !== 'settled' && (record.kind === 'plugin' || record.kind === 'plugin-settings'))
    .map(record => record.target)
)
// 市场只展示功能与扩展插件，过滤底层 system.* 包避免概念混乱。
const entries = computed(() =>
  mergePluginCatalog(catalog.value?.plugins ?? [], installed.value, [], catalog.value?.builtinPlugins ?? [])
    .filter(entry => !entry.id.startsWith('system.'))
)

const installPreview = usePluginPreview({
  client: () => props.client,
  revision: () => revision.value,
  canWrite: () => canWrite.value,
  active: () => props.active,
  blockedIds: () => unresolvedIds.value
})
const lifecyclePreview = usePluginLifecyclePreview({
  client: () => props.client,
  installed: () => installed.value,
  revision: () => revision.value,
  canWrite: () => canWrite.value && !busy.value && !recoveringKey.value,
  active: () => props.active,
  blockedIds: () => unresolvedIds.value
})

function invalidatePreviews(): void {
  installPreview.invalidate()
  lifecyclePreview.invalidate()
}

const { preview, acknowledged, expired, canConfirm, loading: previewLoading, error: previewError } = installPreview

function requestInstall(entry: PackageCatalogEntry): void {
  if (pluginAcquisitionUnavailable(entry)) return
  void requestCandidate({ origin: 'catalog', manifest: entry.manifest })
}

async function requestCandidate(selection: PluginPreviewSelection): Promise<void> {
  invalidatePreviews()
  const pluginId = selection.origin === 'repository' ? selection.pluginId : selection.manifest.id
  if (installed.value?.plugins.some(item => item.manifest.id === pluginId)) {
    const candidate = selection.origin === 'repository'
      ? { origin: selection.origin, discoveryId: selection.discoveryId, version: selection.version }
      : { origin: selection.origin, manifest: selection.manifest }
    await lifecyclePreview.request({ kind: 'update', pluginId, candidate })
  } else {
    await installPreview.request(selection)
  }
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
  const entry = catalogEntry({
    name: confirmed.metadata?.name || confirmed.manifest.id,
    summary: '',
    category: '导入',
    enabled: false,
    status: 'available',
    restartRequired: false,
    manifest: confirmed.manifest,
    metadata: confirmed.metadata
  })
  void runMutation('install', entry, confirmed.manifest, confirmed.previewId)
}

let alive = true
let catalogEpoch = 0

function live(client: ZbaClient): boolean {
  return alive && props.active && client === props.client
}

function showError(cause: unknown): void {
  const value = toUiError(cause)
  if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
  else error.value = value
}

async function loadCatalog(showLoading = true): Promise<void> {
  if (!props.active) return
  const client = props.client
  const epoch = ++catalogEpoch
  if (showLoading) loading.value = true
  error.value = null
  const [indexResult, installedResult] = await Promise.allSettled([
    client.pluginCatalog(),
    typeof client.installedPlugins === 'function' ? client.installedPlugins() : Promise.resolve(null)
  ])
  if (!live(client) || epoch !== catalogEpoch) return
  if (indexResult.status === 'fulfilled') {
    catalog.value = indexResult.value
    revision.value = indexResult.value.revision
  } else {
    showError(indexResult.reason)
  }
  if (installedResult.status === 'fulfilled') {
    installed.value = installedResult.value
    if (installedResult.value) revision.value = installedResult.value.revision
  } else {
    showError(installedResult.reason)
  }
  loading.value = false
}

async function runMutation(kind: ZbaPluginOperationKind, entry: PackageCatalogEntry, manifest = entry.manifest, previewId?: string): Promise<void> {
  if (!previewId) return
  if (!canWrite.value || busy.value || loading.value || unresolvedIds.value.includes(entry.id)) return
  const client = props.client
  const key = pluginOperationKey(kind, entry.id, manifest.version)
  try {
    beginOperation(client, { key, kind: 'plugin', target: entry.id, meta: { operation: kind, version: manifest.version } })
  } catch (cause) {
    error.value = toUiError(cause)
    return
  }
  busy.value = true
  error.value = null
  notice.value = null
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
    revision.value = result.revision
    notice.value = pluginOperationNotice(result.operation)
    emit('changed')
    await loadCatalog(false)
  } catch (cause) {
    markOperation(client, key, isUnknown(cause) ? 'unknown' : 'settled')
    if (!live(client)) return
    if (isUnknown(cause)) notice.value = { tone: 'warn', message: '提交结果未知：原操作键已保留，请核对，不要换键重复提交。' }
    else {
      showError(cause)
      notice.value = pluginRecoveryNotice(cause)
    }
  } finally {
    if (alive && client === props.client) busy.value = false
  }
}

async function checkRecovery(pending: PendingOperation): Promise<void> {
  if (!props.active || recoveringKey.value || pending.state === 'in-flight') return
  const client = props.client
  recoveringKey.value = pending.key
  try {
    const result = await client.pluginOperation(pending.key)
    assertPluginReceipt(result.operation, pending.target, pending.meta.operation)
    markOperation(client, pending.key, 'settled')
    if (!live(client)) return
    revision.value = result.revision
    notice.value = pluginOperationNotice(result.operation)
    emit('changed')
    await loadCatalog(false)
  } catch (cause) {
    if (!live(client)) return
    notice.value = pluginRecoveryNotice(cause)
    if (!notice.value) showError(cause)
  } finally {
    if (alive && client === props.client) recoveringKey.value = ''
  }
}

function openSettings(entry: PluginCatalogEntry): void {
  if (!props.active || packagePending.value.length) return
  emit('open-settings', entry.id)
}

function openPluginUi(entry: PackageCatalogEntry): void {
  if (!props.active || packagePending.value.length) return
  emit('open-ui', entry.id)
}

function resetAll(): void {
  ++catalogEpoch
  invalidatePreviews()
  importer.value?.reset()
  importDraft.value = false
  catalog.value = null
  installed.value = null
  revision.value = ''
  loading.value = false
  busy.value = false
  recoveringKey.value = ''
  error.value = null
  notice.value = null
}

useDraftProtection(() => importDraft.value || packagePending.value.length > 0)

watch(() => [props.mode, props.writesEnabled], () => invalidatePreviews(), { flush: 'sync' })
watch(() => props.active, active => {
  invalidatePreviews()
  if (!active) { ++catalogEpoch; loading.value = false }
  else void loadCatalog()
}, { flush: 'sync' })
watch(() => props.client, () => { resetAll(); void loadCatalog() }, { flush: 'sync' })

onMounted(() => { void loadCatalog() })
onBeforeUnmount(() => { resetAll(); alive = false })

defineExpose({
  reload: () => loadCatalog(true),
  draftCount: computed(() => importDraft.value ? 1 : 0)
})
</script>

<template>
  <section class="market-workspace panel" data-testid="marketplace-workspace">
    <ErrorNotice v-if="error" :error="error" />
    <UiBanner v-if="notice" :tone="notice.tone === 'bad' ? 'bad' : notice.tone === 'warn' ? 'warn' : 'ok'" test-id="marketplace-notice">
      {{ notice.message }}
    </UiBanner>

    <PluginGovernanceNotice :source="catalog?.governanceSource ?? installed?.governanceSource" source-only />
    <UiBanner v-if="catalog?.metadataNotice" tone="warn" test-id="metadata-notice">{{ catalog.metadataNotice }}</UiBanner>
    <UiBanner v-if="metadataFailures.length" tone="warn" test-id="metadata-failures" title="部分插件安装记录无法读取">
      <p>{{ metadataFailures.length }} 项安装记录异常，原记录已保留。仍可浏览目录与按原操作键核对。</p>
      <ul><li v-for="failure in metadataFailures" :key="failure.id">{{ failure.id }} · {{ failure.code }}</li></ul>
    </UiBanner>

    <UiBanner
      v-for="pending in packagePending"
      :key="pending.key"
      tone="warn"
      test-id="marketplace-recovery"
      :title="`${pending.target} · ${pending.meta.operation}`"
    >
      <p>{{ pending.state === 'in-flight' ? '正在提交' : '结果未知' }}，原操作键 <code>{{ pending.key }}</code> 已保留。</p>
      <template #actions>
        <button
          type="button"
          class="tonal"
          data-testid="marketplace-recovery-check"
          :disabled="Boolean(recoveringKey) || pending.state === 'in-flight' || !props.active"
          @click="checkRecovery(pending)"
        >
          {{ recoveringKey === pending.key ? '正在核对…' : '核对原结果' }}
        </button>
      </template>
    </UiBanner>

    <PluginCatalogBrowser
      v-if="props.section === 'catalog'"
      :entries="entries"
      :focus-plugin-id="props.focusPluginId"
      :can-write="canWrite"
      :unresolved-ids="unresolvedIds"
      :busy="busy || previewLoading"
      :loading="loading"
      @install="requestInstall"
      @selection-change="invalidatePreviews"
      @uninstall="entry => requestLifecycle('uninstall', entry)"
      @rollback="entry => requestLifecycle('rollback', entry)"
      @settings="entry => openSettings(entry)"
      @open-ui="entry => openPluginUi(entry)"
    />

    <PluginRepositoryImport
      v-else
      ref="importer"
      :client="props.client"
      :can-write="canWrite"
      :active="props.active"
      :busy="busy || previewLoading"
      @preview="requestCandidate"
      @invalidate="invalidatePreviews"
      @draft-change="(value: boolean) => { importDraft = value }"
      @auth-expired="emit('auth-expired', $event)"
    />

    <p v-if="previewLoading" class="muted" role="status">正在只读预览；尚未下载或安装插件…</p>
    <UiBanner v-if="previewError" tone="bad" role="alert" test-id="preview-error">{{ previewError }}</UiBanner>
    <PluginInstallPreview
      v-if="preview"
      :preview="preview"
      :acknowledged="acknowledged"
      :expired="expired"
      :can-confirm="canConfirm && !busy"
      :busy="busy"
      @update:acknowledged="(value: boolean) => { acknowledged = value }"
      @confirm="confirmPreview"
      @cancel="invalidatePreviews"
    />
    <p v-if="lifecyclePreview.loading.value" class="muted" role="status">正在只读预览生命周期变更…</p>
    <UiBanner v-if="lifecyclePreview.error.value" tone="bad" role="alert" test-id="lifecycle-preview-error">
      {{ lifecyclePreview.error.value }}
    </UiBanner>
    <PluginLifecyclePreview
      v-if="lifecyclePreview.preview.value"
      :preview="lifecyclePreview.preview.value"
      :acknowledged="lifecyclePreview.acknowledged.value"
      :expired="lifecyclePreview.expired.value"
      :can-confirm="lifecyclePreview.canConfirm.value && !busy"
      :busy="busy"
      @update:acknowledged="(value: boolean) => { lifecyclePreview.acknowledged.value = value }"
      @confirm="confirmLifecycle"
      @cancel="invalidatePreviews"
    />
  </section>
</template>

<style scoped>
.market-workspace {
  display: grid;
  gap: 16px;
}
</style>
