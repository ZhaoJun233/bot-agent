<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaPluginCatalog, type ZbaPluginOperationKind, type ZbaPluginSettings } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import DeclaredPluginSettings from './DeclaredPluginSettings.vue'
import PluginCatalogBrowser from './PluginCatalogBrowser.vue'
import { createPluginSettingsDraft, pluginSettingsErrors, type PluginSettingDraft, type PluginSettingsDraft } from './declared-plugin-settings'
import { githubSourceError } from './plugin-manifest'
import type { PluginCatalogEntry } from './plugin-catalog'
import {
  catalogEntry,
  pluginOperationKey,
  pluginOperationNotice,
  pluginRecoveryNotice,
  pluginSettingEdits,
  pluginSettingsDirty,
  pluginSettingsKey,
  resetSettingDrafts,
  type PluginNotice
} from './plugin-marketplace'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
  focusPluginId?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>(), { active: true, focusPluginId: undefined, trigger: null, fallback: null })
const emit = defineEmits<{
  close: []
  changed: []
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)

const catalog = ref<ZbaPluginCatalog | null>(null)
const revision = ref('')
const loading = ref(false)
const busy = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<PluginNotice | null>(null)

/** 数据源（GitHub 仓库或固定版本 Release）。只做本地形状校验，绝不 fetch。 */
const sourceUrl = ref('')
const sourceError = ref('')
const sourceBusy = ref(false)
const sourceEntries = ref<PluginCatalogEntry[]>([])

/** 稳定幂等键：同一次变更的失败重试必须复用同一个键，绝不换键重提。 */
const mutationKeys: Record<string, string> = {}
const recovery = ref<{ pluginId: string; kind: ZbaPluginOperationKind; key: string; label: string } | null>(null)
const recovering = ref(false)

const settingsEntry = ref<PluginCatalogEntry | null>(null)
const settings = ref<ZbaPluginSettings | null>(null)
const draft = ref<PluginSettingsDraft>({})
const settingsKey = ref('')
const settingsLoading = ref(false)
const settingsBusy = ref(false)
const settingsError = ref<UiError | null>(null)
const settingsNotice = ref<PluginNotice | null>(null)
let alive = true

const entries = computed<PluginCatalogEntry[]>(() => {
  const catalogEntries = (catalog.value?.plugins ?? []).map(catalogEntry)
  const seen = new Set(catalogEntries.map((entry) => entry.id + '@' + entry.manifest.version))
  const extra = sourceEntries.value.filter((entry) => !seen.has(entry.id + '@' + entry.manifest.version))
  return [...extra, ...catalogEntries]
})
const dirty = computed(() => pluginSettingsDirty(draft.value))

function describe(cause: unknown): UiError {
  const value = toUiError(cause)
  return value
}
function keepOrInvalidate(pluginId: string, cause: unknown): PluginNotice {
  // 幂等冲突 = 这个键已经绑定了另一份变更，只能作废后换键；其余未知一律保留键。
  if (cause instanceof ZbaRequestError && cause.code === 'idempotency_conflict') delete mutationKeys[pluginId]
  return pluginRecoveryNotice(cause) ?? {
    tone: 'warn',
    message: '提交结果未知：可能已受理，也可能没有。请用同一操作键核对，不要换键重复提交。'
  }
}
function isUnknown(cause: unknown): boolean {
  if (cause instanceof ZbaNetworkError) return true
  if (cause instanceof ZbaRequestError) return cause.status >= 500 || cause.status === 0
  return false
}

async function loadCatalog(showLoading = true): Promise<void> {
  if (showLoading) loading.value = true
  error.value = null
  try {
    const result = await props.client.pluginCatalog()
    if (!alive) return
    catalog.value = result
    revision.value = result.revision
  } catch (cause) {
    if (!alive) return
    const value = describe(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
    else error.value = value
  } finally {
    if (alive && showLoading) loading.value = false
  }
}

async function runMutation(kind: ZbaPluginOperationKind, entry: PluginCatalogEntry, manifest = entry.manifest): Promise<void> {
  if (!canWrite.value || busy.value) return
  const pluginId = entry.id
  const label = kind === 'install' ? '安装' : kind === 'update' ? '更新' : '卸载'
  const key = mutationKeys[pluginId] ?? pluginOperationKey(kind, pluginId, manifest.version)
  mutationKeys[pluginId] = key
  busy.value = true
  error.value = null
  notice.value = null
  recovery.value = null
  const client = props.client
  try {
    const result = kind === 'uninstall'
      ? await client.uninstallPlugin(pluginId, { expectedRevision: revision.value, idempotencyKey: key })
      : kind === 'update'
        ? await client.updatePlugin({ manifest, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
        : await client.installPlugin({ manifest, expectedRevision: revision.value, idempotencyKey: key, trustAcknowledged: true })
    if (!alive || client !== props.client) return
    delete mutationKeys[pluginId]
    revision.value = result.revision
    notice.value = pluginOperationNotice(result.operation)
    emit('changed')
    await loadCatalog(false)
  } catch (cause) {
    if (!alive || client !== props.client) return
    if (isUnknown(cause)) {
      recovery.value = { pluginId, kind, key, label }
      notice.value = keepOrInvalidate(pluginId, cause)
    } else if (cause instanceof ZbaRequestError) {
      const value = describe(cause)
      if (cause.status === 401) emit('auth-expired', value)
      else { error.value = value; const hint = pluginRecoveryNotice(cause); if (hint) notice.value = hint }
      if (cause.code === 'idempotency_conflict') delete mutationKeys[pluginId]
    } else {
      error.value = describe(cause)
    }
  } finally {
    if (alive && client === props.client) busy.value = false
  }
}

/** 结果未知时按原键核对，绝不重新发起变更。 */
async function checkRecovery(): Promise<void> {
  const pending = recovery.value
  if (!pending || recovering.value) return
  recovering.value = true
  const client = props.client
  try {
    const result = await client.pluginOperation(pending.key)
    if (!alive || client !== props.client) return
    revision.value = result.revision
    notice.value = pluginOperationNotice(result.operation)
    delete mutationKeys[pending.pluginId]
    recovery.value = null
    emit('changed')
    await loadCatalog(false)
  } catch (cause) {
    if (!alive || client !== props.client) return
    const hint = pluginRecoveryNotice(cause)
    if (hint) { notice.value = hint; if (cause instanceof ZbaRequestError && cause.code === 'idempotency_conflict') { delete mutationKeys[pending.pluginId]; recovery.value = null } }
    else error.value = describe(cause)
  } finally {
    if (alive && client === props.client) recovering.value = false
  }
}

async function resolveSource(): Promise<void> {
  const raw = sourceUrl.value.trim()
  sourceError.value = ''
  notice.value = null
  if (!raw) { sourceError.value = '请填写数据源地址：GitHub 仓库主页或固定版本 Release。'; return }
  const problem = githubSourceError(raw)
  if (problem) { sourceError.value = problem; return }
  sourceBusy.value = true
  const client = props.client
  try {
    const result = await client.resolvePluginSource(raw)
    if (!alive || client !== props.client) return
    const entry: PluginCatalogEntry = {
      id: result.manifest.id,
      name: result.manifest.id,
      summary: '来自指定数据源（尚未安装）。',
      category: '未分类',
      manifest: result.manifest
    }
    sourceEntries.value = [entry, ...sourceEntries.value.filter((item) => item.id !== entry.id)]
    notice.value = { tone: 'warn', message: result.trustNotice || '请核对固定版本、SHA-256 与权限声明后再安装。' }
  } catch (cause) {
    if (!alive || client !== props.client) return
    const value = describe(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
    else sourceError.value = value.message
  } finally {
    if (alive && client === props.client) sourceBusy.value = false
  }
}

async function openSettings(entry: PluginCatalogEntry): Promise<void> {
  if (settingsEntry.value?.id === entry.id && settings.value) return
  settingsEntry.value = entry
  settings.value = null
  draft.value = {}
  settingsError.value = null
  settingsNotice.value = null
  settingsKey.value = pluginSettingsKey(entry.id)
  await loadSettings(entry)
}

async function loadSettings(entry: PluginCatalogEntry, keepDraft = false): Promise<void> {
  settingsLoading.value = true
  settingsError.value = null
  const client = props.client
  try {
    const result = await client.pluginSettings(entry.id)
    if (!alive || client !== props.client || settingsEntry.value?.id !== entry.id) return
    settings.value = result
    if (!keepDraft) draft.value = createPluginSettingsDraft(result.schema, result.values)
  } catch (cause) {
    if (!alive || client !== props.client) return
    const value = describe(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
    else settingsError.value = value
  } finally {
    if (alive && client === props.client) settingsLoading.value = false
  }
}

function onSettingChange(key: string, value: PluginSettingDraft): void {
  draft.value = { ...draft.value, [key]: value }
}

async function saveSettings(): Promise<void> {
  const entry = settingsEntry.value
  const current = settings.value
  if (!entry || !current || !canWrite.value || settingsBusy.value) return
  const problems = pluginSettingsErrors(current.schema, draft.value)
  if (problems.length) { settingsNotice.value = { tone: 'bad', message: problems.join(' ') }; return }
  const edits = pluginSettingEdits(current.schema, draft.value)
  if (!edits.length) { settingsNotice.value = { tone: 'ok', message: '没有需要保存的更改。' }; return }
  settingsBusy.value = true
  settingsError.value = null
  settingsNotice.value = null
  const client = props.client
  try {
    const result = await client.savePluginSettings(entry.id, {
      expectedRevision: current.revision,
      idempotencyKey: settingsKey.value,
      edits
    })
    if (!alive || client !== props.client) return
    settings.value = result
    draft.value = createPluginSettingsDraft(result.schema, result.values)
    // 已提交：换一个键，下一次保存是另一份变更；同一份变更的重试仍复用旧键。
    settingsKey.value = pluginSettingsKey(entry.id)
    settingsNotice.value = { tone: result.restartRequired ? 'warn' : 'ok', message: result.restartRequired ? '设置已保存，重启 BotAgent 后生效。' : '设置已保存。' }
    emit('changed')
  } catch (cause) {
    if (!alive || client !== props.client) return
    const value = describe(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
    else if (isUnknown(cause)) settingsNotice.value = { tone: 'warn', message: '保存结果未知：请重新载入设置核对，不要直接重复提交。' }
    else if (cause instanceof ZbaRequestError && cause.code === 'idempotency_conflict') {
      // 这个键已绑定另一份变更：作废后重新生成，避免下次保存被服务端拒绝。
      settingsKey.value = pluginSettingsKey(entry.id)
      settingsNotice.value = pluginRecoveryNotice(cause)
    } else settingsError.value = value
  } finally {
    if (alive && client === props.client) settingsBusy.value = false
  }
}

function reloadSettings(): void {
  const entry = settingsEntry.value
  if (!entry) return
  resetSettingDrafts(draft.value, Object.keys(draft.value))
  settingsKey.value = pluginSettingsKey(entry.id)
  void loadSettings(entry)
}

onMounted(async () => {
  await loadCatalog()
  if (!alive || !props.focusPluginId) return
  const target = entries.value.find((entry) => entry.id === props.focusPluginId)
  if (target) await openSettings(target)
})
onBeforeUnmount(() => { alive = false })
</script>

<template>
  <ManagementDialog title="插件市场" title-id="marketplace-title" test-id="marketplace" wide
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="emit('close')">
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

    <div v-if="recovery" class="notice warn" role="status" data-testid="marketplace-recovery">
      <p>{{ recovery.label }}的结果未知，操作键已保留：请核对后继续，不要换键重复提交。</p>
      <button type="button" data-testid="marketplace-recovery-check" :disabled="recovering || !props.active" @click="checkRecovery">
        {{ recovering ? '正在核对…' : '用原操作键核对结果' }}
      </button>
    </div>

    <PluginCatalogBrowser
      :entries="entries"
      :can-write="canWrite"
      :busy="busy"
      :loading="loading"
      @install="(entry) => runMutation('install', entry)"
      @uninstall="(entry) => runMutation('uninstall', entry)"
      @settings="(entry) => openSettings(entry)"
    />

    <section v-if="settingsEntry" class="settings-panel" aria-labelledby="marketplace-settings-heading">
      <h3 id="marketplace-settings-heading">插件声明的设置 · {{ settingsEntry.name }}</h3>
      <p v-if="settingsLoading" role="status">正在读取设置…</p>
      <ErrorNotice v-if="settingsError" :error="settingsError" />
      <p v-if="settingsNotice" class="notice" :class="settingsNotice.tone" role="status" data-testid="marketplace-settings-notice">{{ settingsNotice.message }}</p>
      <DeclaredPluginSettings
        v-if="settings"
        :definitions="settings.schema"
        :draft="draft"
        :configured-secrets="settings.configuredSecrets"
        :disabled="!canWrite || settingsBusy"
        @change="onSettingChange"
      />
      <div v-if="settings" class="settings-actions">
        <button type="button" class="primary" data-testid="marketplace-settings-save" :disabled="!canWrite || settingsBusy || settingsLoading || !dirty" @click="saveSettings">
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
