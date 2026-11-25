<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaConfigurationReadResult, type ZbaProviderConfigurationView } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import { useDraftProtection } from '../../shared/ui/draft-protection'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { newNonce } from '../configuration/draft'
import ConfigurationOperationKey from '../configuration/ConfigurationOperationKey.vue'
import { useConfigurationCommit } from './configuration-commit'
import {
  autoCredentialKey,
  editableText,
  hasProviderMetadataChanges,
  providerCredentialSummary,
  providerDraft,
  providerDraftErrors,
  providerEdit,
  providerSecrets
} from './provider-draft'
import { providerPresets, providerProtocols } from './provider-protocols'
import { pendingOperations } from '../../shared/sync/pending-operations'

const props = withDefaults(defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active?: boolean
  providerKey?: string
  trigger?: HTMLElement | null
  fallback?: HTMLElement | null
}>(), { active: true, trigger: null, fallback: null })
const emit = defineEmits<{
  close: []
  saved: []
  test: [key: string]
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()
const createTargetPrefix = 'provider:create:'
const ownerProviderKey = props.providerKey
const recoveredCreate = props.providerKey === undefined
  ? pendingOperations(props.client, 'configuration').find(item => item.state !== 'settled' && item.target.startsWith(createTargetPrefix))
  : undefined
const key = ref(props.providerKey ?? recoveredCreate?.target.slice(createTargetPrefix.length) ?? 'provider.' + newNonce())
const exists = ref(props.providerKey !== undefined)
const isPrimary = computed(() => exists.value && key.value === 'primary')
const commitTarget = computed(() => exists.value ? 'provider:' + key.value : createTargetPrefix + key.value)
const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const baseline = ref<ZbaProviderConfigurationView | undefined>()
const primarySecretSlot = computed(() => snapshot.value?.secrets?.find(item => item.slot === 0))
const modelCredentials = computed(() => snapshot.value?.view?.registered.modelCredentials ?? [])
const credentialStatus = computed(() =>
  exists.value
    ? providerCredentialSummary(baseline.value, primarySecretSlot.value, baseline.value?.credentialConfigured ?? false)
    : { label: '尚未配置', detail: '新建供应商默认填写独立 API Key；本地 Ollama 可选无需密钥', configured: false }
)
const isCredentialKeyShared = computed(() => {
  const ck = baseline.value?.credentialKey
  if (!ck) return false
  return (snapshot.value?.view?.registered.providers ?? [])
    .some(item => item.key !== key.value && item.credentialKey === ck)
})
const showDirectPrimarySecretPanel = computed(() =>
  isPrimary.value && draft.credentialMode === 'keep' && (baseline.value?.credential === 1 || baseline.value?.credential === 0)
)
const canDeletePrimarySlot = computed(() => Boolean(isPrimary.value && primarySecretSlot.value?.configured))
const canDeleteIndependentKey = computed(() =>
  Boolean(exists.value && !isPrimary.value && baseline.value?.credential === 2 && baseline.value?.credentialKey
    && baseline.value?.credentialConfigured && !isCredentialKeyShared.value)
)
const runtimeSummary = computed(() => {
  switch (baseline.value?.runtimeApplyState) {
    case 1: return '本次读取确认设置已应用；连接是否可用请测试已保存连接。'
    case 3: return '设置需重启机器人服务后应用。请重启 BotAgent 服务，再重新读取确认。'
    case 2: case 4: return '设置未全部应用成功，请检查应用结果并重新读取。'
    default: return '当前生效情况尚未确认，请重新读取或检查应用结果。'
  }
})
const draft = reactive(providerDraft(undefined, key.value))
const loading = ref(false)
const error = ref<UiError | null>(null)
const validation = ref<string[]>([])
const changed = ref(false)
const discardArmed = ref(false)
const deleteArmed = ref(false)
const deleted = ref(false)
const preset = ref('openai')
const baselineChanged = ref(false)
const secretReenterNotice = ref('')
const needsReload = ref(false)
let alive = true
let readSequence = 0
let intent: 'save' | 'delete' = 'save'
const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)
const commit = useConfigurationCommit({
  client: () => props.client,
  target: () => commitTarget.value,
  canWrite: () => canWrite.value,
  canQuery: () => props.active,
  onAuth: (value) => emit('auth-expired', value)
})
const { phase, pendingKey, error: commitError, notice, locked } = commit
const visibleOperationKey = computed(() => canWrite.value && props.providerKey === ownerProviderKey
  ? pendingOperations(props.client, 'configuration', commitTarget.value).find(item => item.key === pendingKey.value && item.state !== 'settled')?.key
  : undefined)
const protocol = computed(() => providerProtocols.find((item) => item.id === draft.protocol))
/** 缓存控制参数只对 OpenAI 兼容与 Anthropic 协议有意义；其它协议不显示开关。 */
const promptCacheProtocol = computed(() => draft.protocol === 'openai-chat' || draft.protocol === 'openai-responses' || draft.protocol === 'azure-openai' || draft.protocol === 'anthropic-messages')
const readOnly = (field: 'name' | 'baseUrl' | 'modelName'): boolean => exists.value && editableText(baseline.value?.[field]) === null
// 模型发现是只读的辅助能力：失败时保留输入框，让用户手动填写。
const modelListId = 'provider-model-options'
const credentialListId = 'provider-credential-key-options'
const models = ref<string[]>([])
const discovering = ref(false)
const modelsError = ref('')

function touch(): void {
  changed.value = true
  discardArmed.value = false
  deleteArmed.value = false
  validation.value = []
  secretReenterNotice.value = ''
}

function ensureCredentialKey(): void {
  if (!draft.credentialKey.trim()) {
    draft.credentialKey = baseline.value?.credentialKey || autoCredentialKey(key.value)
  }
}

function onProtocolChange(): void {
  if (!exists.value) {
    if (draft.protocol === 'ollama' && !draft.credentialSecret.trim()) {
      draft.credentialMode = 'none'
    } else if (draft.protocol !== 'ollama' && draft.credentialMode === 'none') {
      draft.credentialMode = 'independent'
      ensureCredentialKey()
    }
  }
  touch()
}

function onCredentialModeChange(): void {
  if (draft.credentialMode === 'independent') {
    ensureCredentialKey()
    draft.primarySecretAction = 'keep'
    draft.deleteIndependentSecret = false
  } else if (draft.credentialMode !== 'none') {
    draft.deleteIndependentSecret = false
  }
  if (draft.credentialMode !== 'keep') {
    draft.primarySecretAction = 'keep'
  }
  touch()
}

function startPrimarySecretReplace(): void {
  draft.credentialMode = 'keep'
  draft.primarySecretAction = 'replace'
  draft.credentialSecret = ''
  touch()
}

function startPrimarySecretDelete(): void {
  draft.credentialMode = 'keep'
  draft.primarySecretAction = 'delete'
  draft.credentialSecret = ''
  touch()
}

function cancelPrimarySecretAction(): void {
  draft.primarySecretAction = 'keep'
  draft.credentialSecret = ''
  validation.value = []
  if (!hasProviderMetadataChanges(key.value, draft, baseline.value, false)) changed.value = false
}

function startIndependentSecretReplace(): void {
  draft.credentialMode = 'independent'
  ensureCredentialKey()
  draft.credentialSecret = ''
  draft.deleteIndependentSecret = false
  touch()
}

function startIndependentSecretDelete(): void {
  draft.credentialMode = 'none'
  draft.credentialSecret = ''
  draft.deleteIndependentSecret = true
  touch()
}

async function discoverModels(): Promise<void> {
  if (!canWrite.value || locked.value || loading.value || discovering.value || !exists.value) return
  const client = props.client
  const target = key.value
  discovering.value = true
  modelsError.value = ''
  try {
    const result = await client.providerModels(target)
    if (!alive || client !== props.client) return
    models.value = result.models
    if (result.errorCode) modelsError.value = '未能发现模型（' + result.errorCode + '）。可直接手动填写模型名称。'
    else if (!result.models.length) modelsError.value = '服务端没有返回可用模型。可直接手动填写模型名称。'
  } catch (cause) {
    if (!alive || client !== props.client) return
    modelsError.value = toUiError(cause).message + ' 可直接手动填写模型名称。'
  } finally {
    if (alive && client === props.client) discovering.value = false
  }
}
function selectPreset(): void {
  const choice = providerPresets.find((item) => item.id === preset.value)
  if (!choice) return
  draft.protocol = choice.protocol
  if (!draft.name) draft.name = choice.label
  onProtocolChange()
}
async function load(keepDraft = false): Promise<boolean> {
  if (!props.active) return false
  const sequence = ++readSequence
  const client = props.client
  loading.value = true
  error.value = null
  try {
    const result = await client.readConfiguration(1)
    if (!alive || sequence !== readSequence || client !== props.client || !props.active) return false
    if (result.code !== 0 || !result.view) {
      error.value = { code: 'provider_read', message: '无法读取可编辑配置。' }
      return false
    }
    const provider = result.view.registered.providers.find((item) => item.key === key.value)
    if (exists.value && !provider) {
      error.value = { code: 'provider_missing', message: '提供商已被移除；草稿保留，但不能重新覆盖。' }
      return false
    }
    snapshot.value = result
    baseline.value = provider
    needsReload.value = false
    if (!keepDraft) {
      Object.assign(draft, providerDraft(provider, key.value))
      changed.value = false
    } else {
      baselineChanged.value = true
    }
    return true
  } catch (cause) {
    if (!alive || sequence !== readSequence || client !== props.client) return false
    const value = toUiError(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', value)
    else error.value = value
    return false
  } finally {
    if (alive && sequence === readSequence) loading.value = false
  }
}
async function rebase(): Promise<void> {
  const hadSecretIntent = Boolean(draft.credentialSecret) || draft.primarySecretAction !== 'keep' || draft.deleteIndependentSecret
  if (await load(true)) {
    if (hadSecretIntent) {
      discardSecretsOnLeave()
      secretReenterNotice.value = '已读取最新配置；为安全起见，秘密输入已清空，请核对最新状态后重新操作或输入 API Key。'
    }
    commit.acknowledgeReload()
  }
}
async function afterSave(): Promise<void> {
  discardSecretsOnLeave()
  changed.value = false
  deleteArmed.value = false
  baselineChanged.value = false
  secretReenterNotice.value = ''
  if (intent === 'delete') { deleted.value = true; emit('saved'); return }
  exists.value = true
  needsReload.value = true
  emit('saved')
  await load()
}
async function save(remove = false): Promise<void> {
  if (!canWrite.value || locked.value || loading.value || !snapshot.value || deleted.value) return
  if (remove && isPrimary.value) return
  validation.value = remove ? [] : providerDraftErrors(draft, baseline.value, {
    isPrimary: isPrimary.value,
    modelCredentials: modelCredentials.value
  })
  if (validation.value.length) return
  const metadataChanged = hasProviderMetadataChanges(key.value, draft, baseline.value, remove)
  const secrets = providerSecrets(draft, remove, baseline.value, isPrimary.value)
  if (exists.value && !remove && !metadataChanged && secrets.length === 0) {
    validation.value = ['没有检测到需要保存的修改。']
    return
  }
  intent = remove ? 'delete' : 'save'
  const success = await commit.save(snapshot.value.generation, {
    contractRevision: snapshot.value.contractRevision,
    expectedVersion: snapshot.value.version,
    changes: metadataChanged
      ? { registered: { plugins: [], policies: [], providers: [providerEdit(key.value, draft, baseline.value, remove)] } }
      : {},
    secrets
  })
  if (success) await afterSave()
}
async function check(): Promise<void> { if (await commit.check()) await afterSave() }
useDraftProtection(() => changed.value || pendingKey.value !== null || phase.value === 'saving')
function discardAndClose(): void {
  if (pendingKey.value || phase.value === 'saving' || phase.value === 'checking') return
  discardSecretsOnLeave()
  emit('close')
}
function requestClose(): void {
  if (pendingKey.value || phase.value === 'saving' || phase.value === 'checking') {
    validation.value = ['提交结果尚未确认，请先核对。离开此视图不会重复提交。']
    return
  }
  // Repeated Escape, backdrop or close clicks never imply explicit discard consent.
  if (changed.value) { discardArmed.value = true; return }
  emit('close')
}
function discardSecretsOnLeave(): void {
  draft.credentialSecret = ''
  draft.primarySecretAction = 'keep'
  draft.deleteIndependentSecret = false
}
async function requestLeave(reason: 'lock' | 'downgrade' | 'navigate', _trigger?: HTMLElement | null): Promise<boolean> {
  discardSecretsOnLeave()
  if (reason === 'lock' || reason === 'navigate') return true // The shared registry, not the dialog, owns unresolved keys.
  if (pendingKey.value || phase.value === 'saving' || phase.value === 'checking') {
    validation.value = ['提交结果尚未确认，请先核对原操作键。']
    return false
  }
  if (changed.value) { discardArmed.value = true; return false }
  return true
}
defineExpose({ requestLeave, discardSecretsOnLeave, draftCount: computed(() => changed.value ? 1 : 0) })
watch(() => [props.mode, props.writesEnabled, props.client], () => { discardSecretsOnLeave() }, { flush: 'sync' })
watch(() => props.active, (active) => {
  if (!active) { discardSecretsOnLeave(); ++readSequence; loading.value = false }
  else if ((!snapshot.value || needsReload.value) && !pendingKey.value) void load()
})
onMounted(() => { void load() })
onBeforeUnmount(() => { discardSecretsOnLeave(); alive = false; ++readSequence })
</script>

<template>
  <ManagementDialog :title="exists ? (isPrimary ? '主模型供应商设置' : '模型供应商设置') : '新增模型供应商'" title-id="provider-editor-title" test-id="provider-editor"
    drawer :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="requestClose">
    <template #eyebrow><span class="eyebrow">{{ isPrimary ? 'PRIMARY MODEL PROVIDER' : 'MODEL PROVIDER' }}</span></template>
    <p class="muted">
      {{ isPrimary
        ? '主供应商（primary）的 API 根地址、默认模型名称与 API Key 会同步作为全局主模型配置生效。凭据不回显，保存与测试均由后端执行。'
        : '每个供应商独立管理自己的 API 根地址、默认模型与 API Key。协议决定调用方式，凭据永不回显。' }}
    </p>
    <div v-if="!canWrite" class="notice" data-testid="provider-readonly">
      当前只读。<button v-if="props.mode !== 'admin'" type="button" @click="emit('enable-admin')">启用管理员操作</button>
    </div>
    <p v-if="loading" role="status">正在读取配置…</p>
    <ErrorNotice v-if="error" :error="error" />
    <ErrorNotice v-if="commitError" :error="commitError" />
    <div v-if="validation.length" class="notice bad" role="alert" data-testid="provider-validation"><p v-for="line in validation" :key="line">{{ line }}</p></div>
    <p v-if="notice" class="notice" role="status" data-testid="provider-notice">{{ notice }}</p>
    <p v-if="secretReenterNotice" class="notice warn" role="status" data-testid="provider-secret-reenter">{{ secretReenterNotice }}</p>
    <div v-if="phase === 'unconfirmed' || phase === 'checking'" class="notice warn" role="status" data-testid="provider-unknown">
      <p>结果未知。操作键已保留，不会自动重试或生成另一次写入。</p>
      <button type="button" data-testid="provider-check" :disabled="phase === 'checking' || !props.active" @click="check">{{ phase === 'checking' ? '正在核对…' : '核对提交结果' }}</button>
    </div>
    <ConfigurationOperationKey v-if="visibleOperationKey" :operation-key="visibleOperationKey" />
    <div v-if="phase === 'conflict' || phase === 'reload-required'" class="notice warn" data-testid="provider-conflict">
      <p>请先读取最新配置；现有草稿不会被替换。</p>
      <button type="button" data-testid="provider-rebase" :disabled="loading" @click="rebase">读取最新，保留草稿</button>
    </div>
    <section v-if="baselineChanged && baseline" class="notice" data-testid="provider-latest" aria-label="服务端最新配置">
      <strong>最新配置（请与草稿核对）</strong>
      <p>{{ baseline.name.display }} · {{ baseline.baseUrl.display }} · {{ baseline.modelName.display }}</p>
      <p>优先级 {{ baseline.priority }} · {{ baseline.desiredEnabled ? '启用' : '停用' }}</p>
    </section>
    <section v-if="baseline && !deleted" class="notice" data-testid="provider-saved-state" aria-label="提供商已保存设置与当前生效情况">
      <p><strong>已保存设置：</strong>{{ baseline.desiredEnabled ? '启用' : '停用' }}；下方修改在保存前仅是草稿。</p>
      <p><strong>当前生效情况：</strong>{{ runtimeSummary }}</p>
    </section>
    <form v-if="snapshot && !deleted" @submit.prevent="save()" @input="touch">
      <fieldset :disabled="!canWrite || locked || loading" class="editor-fields">
        <legend class="sr-only">提供商配置</legend>
        <label v-if="!exists">品牌预设
          <select v-model="preset" data-testid="provider-preset" @change="selectPreset"><option v-for="item in providerPresets" :key="item.id" :value="item.id">{{ item.label }}</option></select>
        </label>
        <div class="editor-grid">
          <label>名称
            <input v-model="draft.name" data-testid="provider-name" :readonly="readOnly('name')" :placeholder="readOnly('name') ? '当前投影不可编辑' : '例如：主模型'" maxlength="160" required>
          </label>
          <label>协议
            <select v-model="draft.protocol" data-testid="provider-protocol" @change="onProtocolChange">
              <option v-if="!protocol" :value="draft.protocol" disabled>不支持的协议，请重新选择</option>
              <option v-for="item in providerProtocols" :key="item.id" :value="item.id">{{ item.label }}</option>
            </select>
          </label>
        </div>
        <p class="field-hint" data-testid="provider-protocol-hint">{{ protocol?.hint }}</p>
        <label>API 根地址
          <input v-model="draft.baseUrl" data-testid="provider-endpoint" :readonly="readOnly('baseUrl')" type="url" :placeholder="'https://example.com' + (protocol?.path ?? '')" autocomplete="off" spellcheck="false" required>
        </label>
        <p v-if="isPrimary && baseline?.baseUrl.effectiveSource === 2" class="field-hint" data-testid="provider-endpoint-env-hint">
          当前运行生效值来自环境变量（{{ baseline.baseUrl.display }}）；上方输入框为已存储值，保存后将由面板配置接管。
        </p>
        <p v-if="exists" class="field-hint">Strict 模式下修改已有供应商地址需复用已登记端点；如需接入全新网关地址，请点击「新增模型供应商」。</p>
        <div class="editor-grid">
          <label>默认模型名称
            <span class="model-row">
              <input v-model="draft.modelName" data-testid="provider-model" :readonly="readOnly('modelName')" placeholder="model-example" autocomplete="off" spellcheck="false" :list="modelListId" required>
              <button type="button" data-testid="provider-discover-models" :disabled="!canWrite || locked || loading || !exists || discovering" @click="discoverModels">{{ discovering ? '正在发现…' : '发现模型' }}</button>
            </span>
          </label>
          <label>优先级
            <input :value="draft.priority" data-testid="provider-priority" type="number" step="1" :disabled="isPrimary" required @input="draft.priority = ($event.target as HTMLInputElement).value">
          </label>
        </div>
        <p v-if="isPrimary && baseline?.modelName.effectiveSource === 2" class="field-hint" data-testid="provider-model-env-hint">
          当前运行生效模型来自环境变量（{{ baseline.modelName.display }}）；保存后将由面板配置接管。
        </p>
        <datalist :id="modelListId"><option v-for="item in models" :key="item" :value="item" /></datalist>
        <p class="field-hint">模型发现只读取服务端已有的候选；失败或返回不全时直接手动填写即可。{{ exists ? '' : '先保存提供商后才能发现模型。' }}</p>
        <p v-if="modelsError" class="field-hint" role="status" data-testid="provider-models-error">{{ modelsError }}</p>
        <div v-if="draft.protocol === 'azure-openai'" class="editor-grid" data-testid="provider-azure-options">
          <label>Azure 部署名<input v-model="draft.azureDeployment" data-testid="provider-azure-deployment" placeholder="deployment-example" required></label>
          <label>Azure API 版本<input v-model="draft.azureApiVersion" data-testid="provider-azure-version" required></label>
        </div>
        <label v-if="draft.protocol === 'anthropic-messages'">Anthropic API 版本<input v-model="draft.anthropicVersion" data-testid="provider-anthropic-version" required></label>
        <template v-if="promptCacheProtocol">
          <label class="check-label"><input v-model="draft.promptCache" data-testid="provider-prompt-cache" type="checkbox">发送供应商缓存控制参数</label>
          <p class="field-hint" data-testid="provider-prompt-cache-hint">默认关；开启后 OpenAI 兼容通道（含 Azure）会带 prompt_cache_key，Anthropic 通道会用 cache_control，其它协议不发缓存参数。</p>
        </template>

        <section class="credential-section" aria-labelledby="provider-credential-heading">
          <div class="credential-head">
            <h3 id="provider-credential-heading">API Key 与凭据</h3>
            <StatusBadge :tone="credentialStatus.configured ? 'ok' : 'neutral'" :label="credentialStatus.label" data-testid="provider-credential-badge" />
          </div>
          <p class="field-hint" data-testid="provider-credential-status">{{ credentialStatus.detail }}。不会读取或显示已有密钥。</p>

          <!-- 主供应商（primary）内联槽位 0 直控区 -->
          <div v-if="showDirectPrimarySecretPanel" class="primary-secret-panel" data-testid="provider-primary-secret">
            <div v-if="draft.primarySecretAction === 'keep'" class="inline-actions">
              <button type="button" class="tonal" data-testid="provider-primary-secret-replace" @click="startPrimarySecretReplace">
                {{ primarySecretSlot?.configured ? '替换 API Key' : '设置 API Key' }}
              </button>
              <button v-if="canDeletePrimarySlot" type="button" class="ghost danger-text" data-testid="provider-primary-secret-delete" @click="startPrimarySecretDelete">
                删除已存 Key
              </button>
            </div>
            <div v-else-if="draft.primarySecretAction === 'replace'" class="secret-inline-editor">
              <label>新的主模型 API Key
                <input
                  v-model="draft.credentialSecret"
                  data-testid="provider-credential-secret"
                  type="password"
                  autocomplete="new-password"
                  spellcheck="false"
                  placeholder="sk-…（仅本次提交，永不回显）"
                >
              </label>
              <button type="button" class="ghost" data-testid="provider-primary-secret-cancel" @click="cancelPrimarySecretAction">取消</button>
            </div>
            <div v-else class="notice warn" data-testid="provider-primary-secret-deleting">
              <p>已标记删除数据库中的主模型 API Key；若部署环境配置了 <code>BOTAGENT_API_KEY</code>，保存后将自动回退至环境变量。</p>
              <button type="button" class="ghost" data-testid="provider-primary-secret-cancel" @click="cancelPrimarySecretAction">撤销删除</button>
            </div>

            <details class="advanced-credential" data-testid="provider-credential-advanced">
              <summary>高级：切换主供应商凭据绑定模式</summary>
              <label>凭据模式
                <select v-model="draft.credentialMode" data-testid="provider-credential-mode" @change="onCredentialModeChange">
                  <option value="keep">使用主模型 API Key（槽位 0，默认）</option>
                  <option value="independent">绑定独立凭据（credentialKey）</option>
                  <option value="none">不使用凭据（仅本地 Ollama）</option>
                </select>
              </label>
            </details>
          </div>

          <!-- 新建或非 primary（以及已切换高级模式的 primary） -->
          <template v-else>
            <div
              v-if="exists && !isPrimary && baseline?.credential === 2 && draft.credentialMode === 'keep'"
              class="inline-actions"
              data-testid="provider-independent-shortcuts"
            >
              <button type="button" class="tonal" data-testid="provider-independent-secret-replace" @click="startIndependentSecretReplace">
                替换 API Key
              </button>
              <button
                v-if="canDeleteIndependentKey"
                type="button"
                class="ghost danger-text"
                data-testid="provider-independent-secret-delete"
                @click="startIndependentSecretDelete"
              >
                解绑并删除密钥
              </button>
            </div>

            <label>凭据模式
              <select v-model="draft.credentialMode" data-testid="provider-credential-mode" @change="onCredentialModeChange">
                <option v-if="exists" value="keep">保留现有凭据绑定</option>
                <option value="independent">{{ exists ? '设置或替换独立 API Key' : '使用独立 API Key（推荐）' }}</option>
                <option value="none">不使用密钥（如本地 Ollama）</option>
                <option v-if="!isPrimary" value="primary">共用主供应商（primary）API Key</option>
              </select>
            </label>

            <div v-if="draft.credentialMode === 'independent'" class="independent-secret-box" data-testid="provider-independent-credential">
              <label>API Key
                <input
                  v-model="draft.credentialSecret"
                  data-testid="provider-credential-secret"
                  type="password"
                  autocomplete="new-password"
                  spellcheck="false"
                  :placeholder="exists && baseline?.credential === 2 && baseline?.credentialKey === draft.credentialKey.trim()
                    ? '输入新 API Key 以替换，或留空沿用已存密钥'
                    : '输入服务商 API Key（仅本次提交，永不回显）'"
                >
              </label>
              <p class="field-hint">新密钥将与当前供应商配置在同一次事务中原子保存，后台永不回传原文。</p>

              <details class="advanced-credential" data-testid="provider-credential-advanced">
                <summary>高级：凭据标识（credentialKey）与复用</summary>
                <label>凭据标识（自动生成，可改为复用已有凭据）
                  <input
                    v-model="draft.credentialKey"
                    data-testid="provider-credential-key"
                    placeholder="key-provider-example"
                    maxlength="128"
                    autocomplete="off"
                    spellcheck="false"
                    :list="credentialListId"
                  >
                </label>
                <datalist :id="credentialListId">
                  <option v-for="item in modelCredentials" :key="item.key" :value="item.key" />
                </datalist>
                <p class="field-hint"><code>credentialKey</code> 仅用于服务端关联已存储的密钥行，不是密钥原文。</p>
              </details>
            </div>

            <div v-if="draft.credentialMode === 'none' && draft.deleteIndependentSecret" class="notice warn" data-testid="provider-independent-secret-deleting">
              <p>保存后将解除绑定并删除独立密钥 <code>{{ baseline?.credentialKey }}</code>。{{ draft.enabled && draft.protocol !== 'ollama' ? '请先取消勾选下方「启用此模型供应商」后再保存。' : '' }}</p>
            </div>
          </template>
        </section>

        <label class="check-label">
          <input v-model="draft.enabled" data-testid="provider-enabled" type="checkbox" :disabled="!exists || isPrimary">
          启用此模型供应商
        </label>
        <p v-if="isPrimary" class="field-hint" data-testid="provider-primary-readonly-hint">主供应商（primary）的优先级固定为 0 且始终保持启用，不可停用或删除。</p>
        <p v-else-if="!exists" class="notice" data-testid="provider-create-disabled">新增的模型供应商将先保存为停用状态。核对配置并测试连接后，再启用即可。</p>
        <p class="field-hint">配置变更保存后由服务端热重建路由；若运行状态标为需重启，请在合适时间手动重启服务。</p>
      </fieldset>
      <div class="editor-actions">
        <button type="submit" class="primary" data-testid="provider-save" :disabled="!canWrite || locked || loading || (exists && !changed)">{{ phase === 'saving' ? '正在保存…' : exists ? '保存更改' : '保存为停用' }}</button>
        <button v-if="exists" type="button" class="tonal" data-testid="provider-test-saved" :disabled="!canWrite || locked || loading || changed" @click="emit('test', key)">测试已保存连接</button>
        <button type="button" @click="requestClose">关闭</button>
      </div>
      <section v-if="exists && !isPrimary" class="danger-zone">
        <p>删除不可撤销。仍被模型配置或其他对象引用的模型供应商会被后端拒绝删除，请先解除引用。</p>
        <button v-if="!deleteArmed" type="button" class="danger-ghost" data-testid="provider-delete" :disabled="!canWrite || locked || loading" @click="deleteArmed = true">删除模型供应商…</button>
        <div v-else class="notice warn" role="alert">
          <p>确认删除此模型供应商？不会自动修改引用或重启服务。</p>
          <button type="button" class="danger" data-testid="provider-delete-confirm" :disabled="!canWrite || locked || loading" @click="save(true)">确认删除</button>
          <button type="button" @click="deleteArmed = false">取消</button>
        </div>
      </section>
    </form>
    <div v-if="deleted" class="notice" data-testid="provider-deleted">模型供应商已删除。运行状态若标为需重启，请在合适的时间手动重启。</div>
    <div v-if="discardArmed" class="notice warn" role="alert" data-testid="provider-discard">
      <p>有未保存的草稿，确定放弃并关闭？</p><button type="button" @click="discardAndClose">放弃草稿</button><button type="button" @click="discardArmed = false">继续编辑</button>
    </div>
  </ManagementDialog>
</template>

<style scoped>
.eyebrow { color: var(--accent-deep); font-size: 0.75rem; font-weight: 650; letter-spacing: .1em; }
.muted, .field-hint { color: var(--text-muted); font-size: 0.8125rem; line-height: 1.7; }
.editor-fields { display: grid; gap: 12px; border: 0; padding: 0; margin: 16px 0; min-width: 0; }
.editor-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.model-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.model-row input { flex: 1 1 160px; }
.model-row button { flex: 0 0 auto; white-space: nowrap; }
.editor-fields label { display: grid; gap: 7px; font-size: 0.75rem; font-weight: 600; color: var(--text-muted); min-width: 0; }
.editor-fields input:not([type=checkbox]), .editor-fields select { width: 100%; min-width: 0; min-height: 36px; border-radius: 8px; color: var(--text); }
.editor-fields input[type=number] { max-width: 180px; }
.editor-fields input[readonly], .editor-fields input:disabled { color: var(--text-muted); background: var(--surface-muted); }
.editor-fields .check-label { display: flex; flex-direction: row; align-items: center; gap: 10px; min-height: 36px; color: var(--text); }
.check-label input { accent-color: var(--accent-deep); }
.field-hint { margin: -6px 0 0; font-weight: 400; font-size: 0.75rem; }
.credential-section { background: var(--surface-muted); border: 1px solid var(--border); border-radius: 8px; padding: 14px; display: grid; gap: 12px; min-width: 0; }
.credential-head { display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; }
.credential-section h3 { margin: 0; font-size: 0.875rem; color: var(--text); }
.primary-secret-panel, .independent-secret-box { display: grid; gap: 10px; min-width: 0; }
.inline-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.secret-inline-editor { display: flex; flex-wrap: wrap; gap: 8px; align-items: flex-end; }
.secret-inline-editor > label { flex: 1 1 220px; }
.danger-text { color: var(--bad-text); }
.advanced-credential { border-top: 1px solid var(--border-subtle); padding-top: 10px; font-size: 0.75rem; color: var(--text-muted); display: grid; gap: 10px; }
.advanced-credential summary { cursor: pointer; font-weight: 600; color: var(--text-muted); }
.editor-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; border-top: 1px solid var(--border); padding-top: 18px; }
button { min-height: 36px; border-radius: 8px; }
.danger-zone { border-top: 1px solid var(--border); margin-top: 24px; padding-top: 16px; font-size: 0.75rem; line-height: 1.7; color: var(--text-muted); }
.danger-zone > button, .danger-zone .notice button:first-of-type { color: var(--bad-text); }
.notice { overflow-wrap: anywhere; border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin-block: 12px; background: var(--surface-muted); color: var(--text-muted); font-size: 0.8125rem; line-height: 1.65; }
.notice.warn { background: var(--warn-bg); color: var(--warn-text); border-color: var(--border-strong); }.notice.bad { background: var(--bad-bg); color: var(--bad-text); border-color: var(--border-strong); }.notice p { margin: 0 0 8px; }
@media (max-width: 767px) { .editor-grid { grid-template-columns: 1fr; }.editor-actions > button { flex: 1 1 auto; }.credential-section { padding: 14px; }button, .editor-fields input:not([type=checkbox]), .editor-fields select, .editor-fields .check-label { min-height: 44px; } }
</style>
