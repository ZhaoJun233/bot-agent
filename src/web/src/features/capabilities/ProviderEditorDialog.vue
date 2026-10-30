<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaConfigurationReadResult, type ZbaProviderConfigurationView } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { newNonce } from '../configuration/draft'
import { useConfigurationCommit } from './configuration-commit'
import { editableText, providerDraft, providerDraftErrors, providerEdit, providerSecrets } from './provider-draft'
import { providerPresets, providerProtocols } from './provider-protocols'

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
const key = ref(props.providerKey ?? 'provider.' + newNonce())
const exists = ref(props.providerKey !== undefined)
const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const baseline = ref<ZbaProviderConfigurationView | undefined>()
const draft = reactive(providerDraft())
const loading = ref(false)
const error = ref<UiError | null>(null)
const validation = ref<string[]>([])
const changed = ref(false)
const discardArmed = ref(false)
const deleteArmed = ref(false)
const deleted = ref(false)
const preset = ref('openai')
const baselineChanged = ref(false)
const needsReload = ref(false)
let alive = true
let readSequence = 0
let intent: 'save' | 'delete' = 'save'
const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)
const commit = useConfigurationCommit({
  client: () => props.client,
  canWrite: () => canWrite.value,
  canQuery: () => props.active,
  onAuth: (value) => emit('auth-expired', value)
})
const { phase, pendingKey, error: commitError, notice, locked } = commit
const protocol = computed(() => providerProtocols.find((item) => item.id === draft.protocol))
const readOnly = (field: 'name' | 'baseUrl' | 'modelName'): boolean => exists.value && editableText(baseline.value?.[field]) === null
const credentialLabels: Record<number, string> = { 0: '未配置', 1: '共用主模型凭据', 2: '独立凭据已绑定', 3: '旧凭据需人工核对' }
// 模型发现是只读的辅助能力：失败时保留输入框，让用户手动填写。
const modelListId = 'provider-model-options'
const models = ref<string[]>([])
const discovering = ref(false)
const modelsError = ref('')

function touch(): void { changed.value = true; discardArmed.value = false; deleteArmed.value = false; validation.value = [] }
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
  touch()
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
    if (!keepDraft) { Object.assign(draft, providerDraft(provider)); changed.value = false }
    else baselineChanged.value = true
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
  if (await load(true)) commit.acknowledgeReload()
}
async function afterSave(): Promise<void> {
  changed.value = false
  deleteArmed.value = false
  baselineChanged.value = false
  if (intent === 'delete') { deleted.value = true; emit('saved'); return }
  exists.value = true
  needsReload.value = true
  emit('saved')
  await load()
}
async function save(remove = false): Promise<void> {
  if (!canWrite.value || locked.value || loading.value || !snapshot.value || deleted.value) return
  validation.value = remove ? [] : providerDraftErrors(draft, baseline.value)
  if (validation.value.length) return
  intent = remove ? 'delete' : 'save'
  const success = await commit.save(snapshot.value.generation, {
    contractRevision: snapshot.value.contractRevision,
    expectedVersion: snapshot.value.version,
    changes: { registered: { plugins: [], policies: [], providers: [providerEdit(key.value, draft, baseline.value, remove)] } },
    secrets: providerSecrets(draft, remove)
  })
  if (success) await afterSave()
}
async function check(): Promise<void> { if (await commit.check()) await afterSave() }
function requestClose(): void {
  if (pendingKey.value || phase.value === 'saving' || phase.value === 'checking') {
    validation.value = ['提交结果尚未确认，请先核对。离开此视图不会重复提交。']
    return
  }
  if (changed.value && !discardArmed.value) { discardArmed.value = true; return }
  emit('close')
}
watch(() => props.active, (active) => {
  if (!active) { ++readSequence; loading.value = false }
  else if ((!snapshot.value || needsReload.value) && !pendingKey.value) void load()
})
onMounted(() => { void load() })
onBeforeUnmount(() => { alive = false; ++readSequence })
</script>

<template>
  <ManagementDialog :title="exists ? '提供商设置' : '添加提供商'" title-id="provider-editor-title" test-id="provider-editor"
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" @close="requestClose">
    <template #eyebrow><span class="eyebrow">MODEL PROVIDER</span></template>
    <p class="muted">协议决定调用方式；品牌预设只辅助选择。凭据不回显，保存与测试都由后端处理。</p>
    <div v-if="!canWrite" class="notice" data-testid="provider-readonly">
      当前只读。<button v-if="props.mode !== 'admin'" type="button" @click="emit('enable-admin')">启用管理员操作</button>
    </div>
    <p v-if="loading" role="status">正在读取配置…</p>
    <ErrorNotice v-if="error" :error="error" />
    <ErrorNotice v-if="commitError" :error="commitError" />
    <div v-if="validation.length" class="notice bad" role="alert" data-testid="provider-validation"><p v-for="line in validation" :key="line">{{ line }}</p></div>
    <p v-if="notice" class="notice" role="status" data-testid="provider-notice">{{ notice }}</p>
    <div v-if="phase === 'unconfirmed' || phase === 'checking'" class="notice warn" role="status" data-testid="provider-unknown">
      <p>结果未知。操作键已保留，不会自动重试或生成另一次写入。</p>
      <button type="button" data-testid="provider-check" :disabled="phase === 'checking' || !props.active" @click="check">{{ phase === 'checking' ? '正在核对…' : '核对提交结果' }}</button>
    </div>
    <div v-if="phase === 'conflict' || phase === 'reload-required'" class="notice warn" data-testid="provider-conflict">
      <p>请先读取最新配置；现有草稿不会被替换。</p>
      <button type="button" data-testid="provider-rebase" :disabled="loading" @click="rebase">读取最新，保留草稿</button>
    </div>
    <section v-if="baselineChanged && baseline" class="notice" data-testid="provider-latest" aria-label="服务端最新配置">
      <strong>最新配置（请与草稿核对）</strong>
      <p>{{ baseline.name.display }} · {{ baseline.baseUrl.display }} · {{ baseline.modelName.display }}</p>
      <p>优先级 {{ baseline.priority }} · {{ baseline.desiredEnabled ? '启用' : '停用' }}</p>
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
            <select v-model="draft.protocol" data-testid="provider-protocol" @change="touch">
              <option v-if="!protocol" :value="draft.protocol" disabled>不支持的协议，请重新选择</option>
              <option v-for="item in providerProtocols" :key="item.id" :value="item.id">{{ item.label }}</option>
            </select>
          </label>
        </div>
        <p class="field-hint" data-testid="provider-protocol-hint">{{ protocol?.hint }}</p>
        <label>API 根地址
          <input v-model="draft.baseUrl" data-testid="provider-endpoint" :readonly="readOnly('baseUrl')" type="url" :placeholder="'https://example.com' + (protocol?.path ?? '')" autocomplete="off" spellcheck="false" required>
        </label>
        <div class="editor-grid">
          <label>模型名称
            <span class="model-row">
              <input v-model="draft.modelName" data-testid="provider-model" :readonly="readOnly('modelName')" placeholder="model-example" autocomplete="off" spellcheck="false" :list="modelListId" required>
              <button type="button" data-testid="provider-discover-models" :disabled="!canWrite || locked || loading || !exists || discovering" @click="discoverModels">{{ discovering ? '正在发现…' : '发现模型' }}</button>
            </span>
          </label>
          <label>优先级<input :value="draft.priority" data-testid="provider-priority" type="number" step="1" required @input="draft.priority = ($event.target as HTMLInputElement).value"></label>
        </div>
        <datalist :id="modelListId"><option v-for="item in models" :key="item" :value="item" /></datalist>
        <p class="field-hint">模型发现只读取服务端已有的候选；失败或返回不全时直接手动填写即可。{{ exists ? '' : '先保存提供商后才能发现模型。' }}</p>
        <p v-if="modelsError" class="field-hint" role="status" data-testid="provider-models-error">{{ modelsError }}</p>
        <div v-if="draft.protocol === 'azure-openai'" class="editor-grid" data-testid="provider-azure-options">
          <label>Azure 部署名<input v-model="draft.azureDeployment" data-testid="provider-azure-deployment" placeholder="deployment-example" required></label>
          <label>Azure API 版本<input v-model="draft.azureApiVersion" data-testid="provider-azure-version" required></label>
        </div>
        <label v-if="draft.protocol === 'anthropic-messages'">Anthropic API 版本<input v-model="draft.anthropicVersion" data-testid="provider-anthropic-version" required></label>
        <section class="credential-section" aria-labelledby="provider-credential-heading">
          <h3 id="provider-credential-heading">凭据</h3>
          <p class="field-hint" data-testid="provider-credential-status">{{ exists ? credentialLabels[baseline?.credential ?? 0] : '新提供商尚未配置凭据' }}。不会读取或显示已有密钥。</p>
          <label>凭据操作
            <select v-model="draft.credentialMode" data-testid="provider-credential-mode" @change="touch">
              <option value="keep">保留现有绑定</option>
              <option value="primary">共用主模型凭据</option>
              <option value="none">不使用凭据（如本地 Ollama）</option>
              <option value="independent">独立凭据（新建或替换密钥）</option>
            </select>
          </label>
          <div v-if="draft.credentialMode === 'independent'" class="editor-grid" data-testid="provider-independent-credential">
            <label>凭据键（credentialKey）
              <input v-model="draft.credentialKey" data-testid="provider-credential-key" placeholder="provider-example-key" maxlength="128" autocomplete="off" spellcheck="false">
            </label>
            <label>新密钥
              <input v-model="draft.credentialSecret" data-testid="provider-credential-secret" type="password" autocomplete="new-password" spellcheck="false" :placeholder="exists ? '留空表示沿用已绑定密钥' : '仅本次提交，不会回显'">
            </label>
          </div>
          <p class="field-hint">密钥只在本次保存中发送一次，之后只显示“已绑定”，后台不会回传原文。</p>
        </section>
        <label class="check-label"><input v-model="draft.enabled" data-testid="provider-enabled" type="checkbox" :disabled="!exists">启用此提供商</label>
        <p v-if="!exists" class="notice" data-testid="provider-create-disabled">新提供商先保存为停用。检查配置、测试连接后，再单独启用。</p>
        <p class="field-hint">配置或路由变更可能需要重启；此处不会自动重启服务。</p>
      </fieldset>
      <div class="editor-actions">
        <button type="submit" class="primary" data-testid="provider-save" :disabled="!canWrite || locked || loading || (exists && !changed)">{{ phase === 'saving' ? '正在保存…' : exists ? '保存更改' : '保存为停用' }}</button>
        <button v-if="exists" type="button" data-testid="provider-test-saved" :disabled="!canWrite || locked || loading || changed" @click="emit('test', key)">测试已保存连接</button>
        <button type="button" @click="requestClose">关闭</button>
      </div>
      <section v-if="exists" class="danger-zone">
        <p>删除不可撤销。仍被模型配置或其他对象引用的提供商会被后端拒绝删除，请先解除引用。</p>
        <button v-if="!deleteArmed" type="button" data-testid="provider-delete" :disabled="!canWrite || locked || loading" @click="deleteArmed = true">删除提供商…</button>
        <div v-else class="notice warn" role="alert">
          <p>确认删除此提供商？不会自动修改引用或重启服务。</p>
          <button type="button" data-testid="provider-delete-confirm" :disabled="!canWrite || locked || loading" @click="save(true)">确认删除</button>
          <button type="button" @click="deleteArmed = false">取消</button>
        </div>
      </section>
    </form>
    <div v-if="deleted" class="notice" data-testid="provider-deleted">提供商已删除。运行状态若标为需重启，请在合适的时间手动重启。</div>
    <div v-if="discardArmed" class="notice warn" role="alert" data-testid="provider-discard">
      <p>有未保存的草稿，确定放弃并关闭？</p><button type="button" @click="emit('close')">放弃草稿</button><button type="button" @click="discardArmed = false">继续编辑</button>
    </div>
  </ManagementDialog>
</template>

<style scoped>
.eyebrow { color: var(--accent-deep); font-size: 11px; font-weight: 650; letter-spacing: .1em; }
.muted, .field-hint { color: var(--text-muted); font-size: 13px; line-height: 1.7; }
.editor-fields { display: grid; gap: 18px; border: 0; padding: 0; margin: 24px 0; min-width: 0; }
.editor-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: 16px; }
.model-row { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.model-row input { flex: 1 1 160px; }
.model-row button { flex: 0 0 auto; white-space: nowrap; }
.editor-fields label { display: grid; gap: 7px; font-size: 12px; font-weight: 600; color: var(--text-muted); min-width: 0; }
.editor-fields input:not([type=checkbox]), .editor-fields select { width: 100%; min-width: 0; min-height: 36px; border-radius: 8px; color: var(--text); }
.editor-fields input[readonly] { color: var(--text-muted); background: var(--surface-muted); }
.editor-fields .check-label { display: flex; flex-direction: row; align-items: center; gap: 10px; min-height: 36px; color: var(--text); }
.check-label input { accent-color: var(--accent-deep); }
.field-hint { margin: -6px 0 0; font-weight: 400; font-size: 12px; }
.credential-section { background: var(--surface-muted); border: 1px solid var(--border); border-radius: 8px; padding: 18px; display: grid; gap: 16px; min-width: 0; }
.credential-section h3 { margin: 0; font-size: 14px; color: var(--text); }
.editor-actions { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; border-top: 1px solid var(--border); padding-top: 18px; }
button { min-height: 36px; border-radius: 8px; }
.danger-zone { border-top: 1px solid var(--border); margin-top: 24px; padding-top: 16px; font-size: 12px; line-height: 1.7; color: var(--text-muted); }
.danger-zone > button, .danger-zone .notice button:first-of-type { color: var(--bad-text); }
.notice { overflow-wrap: anywhere; border: 1px solid var(--border); border-radius: 8px; padding: 12px 14px; margin-block: 12px; background: var(--surface-muted); color: var(--text-muted); font-size: 13px; line-height: 1.65; }
.notice.warn { background: var(--warn-bg); color: var(--warn-text); border-color: var(--border-strong); }.notice.bad { background: var(--bad-bg); color: var(--bad-text); border-color: var(--border-strong); }.notice p { margin: 0 0 8px; }
@media (max-width: 767px) { .editor-grid { grid-template-columns: 1fr; }.editor-actions > button { flex: 1 1 auto; }.credential-section { padding: 14px; }button, .editor-fields input:not([type=checkbox]), .editor-fields select, .editor-fields .check-label { min-height: 44px; } }
</style>
