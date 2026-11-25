<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaPluginSettings } from '../../api/zba'
import { isPluginWriteOutcomeUnknown as isUnknown } from './plugin-write-outcome'
import { beginOperation, getPendingOperation, markOperation } from '../../shared/sync/pending-operations'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import DeclaredPluginSettings from './DeclaredPluginSettings.vue'
import {
  clearPluginSecretDrafts,
  createPluginSettingsDraft,
  pluginSettingsErrors,
  type PluginSettingDraft,
  type PluginSettingsDraft
} from './declared-plugin-settings'
import {
  pluginRecoveryNotice,
  pluginSettingEdits,
  pluginSettingsDirty,
  pluginSettingsKey,
  type PluginNotice
} from './plugin-marketplace'

const props = defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  active: boolean
  pluginId?: string | null
}>()

const emit = defineEmits<{
  saved: []
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

const targetId = computed(() => {
  const raw = props.pluginId?.trim() ?? ''
  return raw.includes('.') ? raw : ''
})

const canWrite = computed(() => props.active && props.mode === 'admin' && props.writesEnabled && targetId.value.length > 0)
const settings = ref<ZbaPluginSettings | null>(null)
const draft = ref<PluginSettingsDraft>({})
const loading = ref(false)
const error = ref<UiError | null>(null)
const notice = ref<PluginNotice | null>(null)
const discardAction = ref<'reload' | 'leave' | null>(null)
const pending = computed(() => targetId.value ? getPendingOperation(props.client, 'plugin-settings', targetId.value) : undefined)
const busy = computed(() => pending.value?.state === 'in-flight')
const blocked = computed(() => Boolean(targetId.value && (pending.value || getPendingOperation(props.client, 'plugin', targetId.value))))
const reviewedKey = ref('')
const reconcileArmed = ref(false)
const dirty = computed(() => pluginSettingsDirty(draft.value))
const visible = computed(() => Boolean(settings.value && settings.value.schema.length > 0))

let alive = true
let epoch = 0
let leaveResolve: ((allowed: boolean) => void) | null = null
let leavePromise: Promise<boolean> | null = null

function owns(client: ZbaClient, id: string, currentEpoch: number): boolean {
  return alive && props.active && client === props.client && targetId.value === id && currentEpoch === epoch
}

function applySettings(result: ZbaPluginSettings): void {
  const values = Object.fromEntries(
    result.schema
      .filter((field) => field.type !== 'secret' && field.key in result.values)
      .map((field) => [field.key, result.values[field.key]!])
  )
  settings.value = { ...result, values }
  draft.value = createPluginSettingsDraft(result.schema, values)
}

function discardSecretsOnLeave(): void {
  clearPluginSecretDrafts(settings.value?.schema ?? [], draft.value)
}

function cancelDiscard(): void {
  discardAction.value = null
  leaveResolve?.(false)
  leaveResolve = null
  leavePromise = null
}

function confirmDiscard(): void {
  if (blocked.value) return
  const action = discardAction.value
  discardAction.value = null
  discardSecretsOnLeave()
  draft.value = settings.value ? createPluginSettingsDraft(settings.value.schema, settings.value.values) : {}
  if (action === 'reload') {
    void load()
  } else if (action === 'leave') {
    leaveResolve?.(true)
    leaveResolve = null
    leavePromise = null
  }
}

function requestLeave(reason: 'close' | 'lock' | 'downgrade' | 'navigate', _trigger?: HTMLElement | null): Promise<boolean> {
  if (reason === 'lock') {
    discardSecretsOnLeave()
    cancelDiscard()
    return Promise.resolve(true)
  }
  if (blocked.value) return Promise.resolve(false)
  if (!dirty.value || pending.value) {
    discardSecretsOnLeave()
    cancelDiscard()
    return Promise.resolve(true)
  }
  if (leavePromise) return leavePromise
  cancelDiscard()
  discardAction.value = 'leave'
  leavePromise = new Promise<boolean>((resolve) => { leaveResolve = resolve })
  return leavePromise
}

function endSession(): void {
  discardSecretsOnLeave()
  cancelDiscard()
  ++epoch
  settings.value = null
  draft.value = {}
  loading.value = false
  error.value = null
  notice.value = null
  reviewedKey.value = ''
  reconcileArmed.value = false
}

async function load(): Promise<void> {
  const id = targetId.value
  if (!props.active || !id || typeof props.client.pluginSettings !== 'function') {
    endSession()
    return
  }
  const client = props.client
  const currentEpoch = ++epoch
  loading.value = true
  error.value = null
  reviewedKey.value = ''
  reconcileArmed.value = false
  try {
    const result = await client.pluginSettings(id)
    if (!owns(client, id, currentEpoch)) return
    applySettings(result)
    const op = getPendingOperation(client, 'plugin-settings', id)
    if (op?.state === 'unknown') reviewedKey.value = op.key
  } catch (cause) {
    if (!owns(client, id, currentEpoch)) return
    if (cause instanceof ZbaRequestError && cause.status === 404) {
      settings.value = null
      draft.value = {}
      return
    }
    const uiErr = toUiError(cause)
    if (cause instanceof ZbaRequestError && cause.status === 401) {
      endSession()
      emit('auth-expired', uiErr)
    } else {
      error.value = uiErr
    }
  } finally {
    if (alive && client === props.client && targetId.value === id && currentEpoch === epoch) {
      loading.value = false
    }
  }
}

function onSettingChange(key: string, value: PluginSettingDraft): void {
  if (!canWrite.value || blocked.value || loading.value) return
  draft.value = { ...draft.value, [key]: value }
}

async function save(): Promise<void> {
  const id = targetId.value
  const current = settings.value
  if (!id || !current || !canWrite.value || blocked.value || loading.value) return
  const problems = pluginSettingsErrors(current.schema, draft.value)
  if (problems.length) {
    notice.value = { tone: 'bad', message: problems.join(' ') }
    return
  }
  const edits = pluginSettingEdits(current.schema, draft.value)
  if (!edits.length) return
  const client = props.client
  const key = pluginSettingsKey(id)
  const currentEpoch = epoch
  try {
    beginOperation(client, { key, kind: 'plugin-settings', target: id })
  } catch (cause) {
    error.value = toUiError(cause)
    return
  }
  error.value = null
  notice.value = null
  reviewedKey.value = ''
  reconcileArmed.value = false
  discardSecretsOnLeave()
  try {
    const result = await client.savePluginSettings(id, {
      expectedRevision: current.revision,
      idempotencyKey: key,
      edits
    })
    markOperation(client, key, 'settled')
    if (!owns(client, id, currentEpoch)) return
    applySettings(result)
    notice.value = result.restartRequired
      ? { tone: 'warn', message: '扩展设置已保存，将在启用或重启通道后生效。' }
      : { tone: 'ok', message: '扩展设置已保存并立即生效。' }
    emit('saved')
  } catch (cause) {
    markOperation(client, key, isUnknown(cause) ? 'unknown' : 'settled')
    if (!owns(client, id, currentEpoch)) return
    if (isUnknown(cause)) {
      notice.value = {
        tone: 'warn',
        message: '保存结果未知，原操作键已保留。请重读核对当前状态，避免盲目重复提交。'
      }
    } else {
      const uiErr = toUiError(cause)
      if (cause instanceof ZbaRequestError && cause.status === 401) {
        endSession()
        emit('auth-expired', uiErr)
      } else {
        error.value = uiErr
        notice.value = pluginRecoveryNotice(cause)
      }
    }
  }
}

function reload(): void {
  if (busy.value || loading.value || !props.active) return
  if (dirty.value && !pending.value) {
    discardAction.value = 'reload'
    return
  }
  discardSecretsOnLeave()
  cancelDiscard()
  void load()
}

function reconcile(): void {
  const op = pending.value
  if (!canWrite.value || op?.state !== 'unknown' || op.key !== reviewedKey.value || loading.value || !settings.value || !reconcileArmed.value) return
  markOperation(props.client, op.key, 'settled')
  reconcileArmed.value = false
  reviewedKey.value = ''
  notice.value = { tone: 'ok', message: '已确认当前扩展设置状态并解除锁定。' }
}

watch(() => [props.active, targetId.value, props.client] as const, ([isActive]) => {
  if (isActive && targetId.value) void load()
  else endSession()
})

watch(() => [props.mode, props.writesEnabled] as const, ([nextMode, nextWrites]) => {
  if (nextMode !== 'admin' || !nextWrites) discardSecretsOnLeave()
})

onMounted(() => {
  if (props.active && targetId.value) void load()
})

onBeforeUnmount(() => {
  alive = false
  endSession()
})

defineExpose({
  dirty,
  hasUnresolvedIntent: computed(() => blocked.value),
  requestLeave,
  discardSecretsOnLeave,
  endSession
})
</script>

<template>
  <section v-if="visible && settings" class="embedded-plugin-settings" data-testid="embedded-declared-plugin-settings">
    <div class="embedded-header">
      <div>
        <h3 class="embedded-title">包声明扩展参数</h3>
        <p class="embedded-subtitle">独立持久化于插件配置与加密密钥库，密钥提交后自动擦除且永不回显。</p>
      </div>
      <div class="embedded-actions">
        <button
          v-if="props.mode !== 'admin' || !props.writesEnabled"
          type="button"
          class="btn-secondary"
          data-testid="embedded-plugin-enable-admin"
          @click="emit('enable-admin')"
        >
          解锁管理模式以编辑
        </button>
        <button
          type="button"
          class="btn-secondary"
          :disabled="busy || loading"
          data-testid="embedded-plugin-settings-reload"
          @click="reload"
        >
          重新读取
        </button>
        <button
          type="button"
          class="btn-primary"
          :disabled="!canWrite || blocked || loading || !dirty"
          data-testid="embedded-plugin-settings-save"
          @click="save"
        >
          {{ busy ? '正在保存…' : '保存扩展参数' }}
        </button>
      </div>
    </div>

    <div v-if="discardAction" class="embedded-notice" data-tone="warn" role="alert" data-testid="embedded-plugin-discard-confirmation">
      <p>当前扩展参数有未保存的修改，确认放弃并继续？</p>
      <div class="embedded-actions">
        <button type="button" class="btn-secondary" data-testid="embedded-plugin-discard-confirm" @click="confirmDiscard">放弃修改并继续</button>
        <button type="button" class="btn-secondary" data-testid="embedded-plugin-discard-cancel" @click="cancelDiscard">继续编辑</button>
      </div>
    </div>

    <ErrorNotice v-if="error" :error="error" />
    <p v-if="notice" class="embedded-notice" :data-tone="notice.tone" role="status">{{ notice.message }}</p>

    <div v-if="pending?.state === 'unknown' && pending.key === reviewedKey" class="reconcile-box">
      <label class="reconcile-check">
        <input v-model="reconcileArmed" type="checkbox" data-testid="embedded-plugin-reconcile-check">
        我已核对当前显示的设置状态
      </label>
      <button type="button" class="btn-secondary" :disabled="!reconcileArmed" @click="reconcile">解除未决锁定</button>
    </div>

    <DeclaredPluginSettings
      :definitions="settings.schema"
      :draft="draft"
      :configured-secrets="settings.configuredSecrets"
      :disabled="!canWrite || blocked || loading"
      @change="onSettingChange"
    />
  </section>
</template>

<style scoped>
.embedded-plugin-settings {
  margin-top: 20px;
  padding-top: 18px;
  border-top: 1px solid var(--border);
  display: grid;
  gap: 12px;
}
.embedded-header {
  display: flex;
  flex-wrap: wrap;
  justify-content: space-between;
  align-items: flex-start;
  gap: 12px;
}
.embedded-title {
  margin: 0;
  font-size: 0.9375rem;
  font-weight: 600;
  color: var(--text);
}
.embedded-subtitle {
  margin: 4px 0 0;
  font-size: 0.75rem;
  color: var(--text-soft);
}
.embedded-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  align-items: center;
}
.embedded-notice {
  margin: 0;
  padding: 10px 12px;
  border-radius: 8px;
  font-size: 0.8125rem;
  background: var(--surface-alt, var(--surface));
  border: 1px solid var(--border);
}
.embedded-notice[data-tone='bad'] {
  border-color: var(--bad, #dc2626);
  color: var(--bad, #dc2626);
}
.embedded-notice[data-tone='warn'] {
  border-color: var(--warn, #d97706);
  color: var(--text);
}
.reconcile-box {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  align-items: center;
  font-size: 0.8125rem;
}
.reconcile-check {
  display: flex;
  gap: 6px;
  align-items: center;
}
</style>
