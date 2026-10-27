<script setup lang="ts">
import { computed, nextTick, onMounted, ref } from 'vue'
import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaConfigurationReadResult,
  type ZbaReadField
} from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import {
  baselineRaw,
  buildPatch,
  conflictingPaths,
  createOperationKey,
  draftRaw,
  fieldPath,
  isDirty,
  isEditable,
  newNonce,
  nowSeconds,
  valueKind,
  type DraftMap
} from '../configuration/draft'
import { fieldLabel } from '../configuration/field-labels'
import { resolvePluginFields, type PluginSettingsSpec } from './plugin-settings'

/**
 * 插件设置弹窗：只编辑这个插件拥有的字段。
 *
 * 写路径与配置页同一条（/configuration/save + 操作键 + CAS 版本），
 * 所以同样遵守：结果未知时锁定并只允许核对，冲突时读取最新并标出重叠字段。
 */
const props = defineProps<{
  client: ZbaClient
  mode: 'read' | 'admin'
  writesEnabled: boolean
  spec: PluginSettingsSpec
  displayName: string
}>()

const emit = defineEmits<{
  close: [saved: boolean]
  'enable-admin': []
  'auth-expired': [error: UiError]
}>()

type Phase = 'loading' | 'idle' | 'saving' | 'unconfirmed' | 'conflict'

const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const base = ref<ZbaConfigurationReadResult | null>(null)
const draft = ref<DraftMap>({})
const phase = ref<Phase>('loading')
const error = ref<UiError | null>(null)
const message = ref<string | null>(null)
const fieldErrors = ref<string[]>([])
const conflictKeys = ref<string[]>([])
const pendingKey = ref<string | null>(null)
const discardArmed = ref(false)
const savedOnce = ref(false)
const dialog = ref<HTMLElement | null>(null)
const heading = ref<HTMLElement | null>(null)

const canWrite = computed(() => props.mode === 'admin' && props.writesEnabled)
const locked = computed(() => phase.value === 'saving' || phase.value === 'unconfirmed' || phase.value === 'loading')
const fields = computed(() => {
  const view = snapshot.value?.view
  return view ? resolvePluginFields(props.spec, view) : []
})
const dirtyCount = computed(() => Object.keys(draft.value).length)
const titleId = 'plugin-settings-title-' + props.spec.pluginKey.replace(/[^a-z0-9]/gi, '-')

function reportAuth(cause: unknown): boolean {
  if (cause instanceof ZbaRequestError && cause.status === 401) {
    emit('auth-expired', toUiError(cause))
    return true
  }
  return false
}

async function load(): Promise<boolean> {
  error.value = null
  try {
    snapshot.value = await props.client.readConfiguration(1)
    if (dirtyCount.value === 0) base.value = snapshot.value
    return true
  } catch (cause) {
    if (!reportAuth(cause)) error.value = toUiError(cause)
    return false
  }
}

function path(section: string, key: string): string {
  return fieldPath(section as never, key)
}

function current(section: string, key: string, field: ZbaReadField): string {
  return draftRaw(draft.value, path(section, key), field)
}

function dirty(section: string, key: string, field: ZbaReadField): boolean {
  return isDirty(draft.value, path(section, key), field)
}

function editable(field: ZbaReadField): boolean {
  return canWrite.value && isEditable(field)
}

function onInput(section: string, key: string, field: ZbaReadField, raw: string): void {
  if (locked.value || phase.value === 'conflict') return
  if (dirtyCount.value === 0) base.value = snapshot.value
  const next = { ...draft.value }
  if (raw === baselineRaw(field)) delete next[path(section, key)]
  else next[path(section, key)] = { kind: 'set', raw }
  draft.value = next
  discardArmed.value = false
  message.value = null
  fieldErrors.value = []
}

function reset(section: string, key: string): void {
  if (locked.value) return
  const next = { ...draft.value }
  delete next[path(section, key)]
  draft.value = next
}

function display(field: ZbaReadField): string {
  if (field.visibility === 0) return '已隐藏'
  const value = field.value
  if (value === null || value === undefined) return '—'
  if (typeof value === 'boolean') return value ? '开' : '关'
  if (typeof value === 'string') return value.length === 0 ? '（空）' : value
  if (Array.isArray(value)) return value.length === 0 ? '（空）' : value.length + ' 项'
  if (typeof value === 'object') return JSON.stringify(value)
  return String(value)
}

function readOnlyReason(field: ZbaReadField): string {
  if (!canWrite.value) return ''
  if (field.visibility !== 1) return '这个字段不回显值'
  return '列表/结构类字段请到「配置」页编辑'
}

async function save(): Promise<void> {
  const view = snapshot.value?.view
  if (!view || !snapshot.value || !canWrite.value || locked.value || phase.value === 'conflict') return
  const built = buildPatch(draft.value, view)
  fieldErrors.value = built.errors
  if (built.errors.length > 0) return
  if (Object.keys(built.patch).length === 0) {
    message.value = '没有需要保存的修改。'
    return
  }
  const key = createOperationKey(snapshot.value.generation, nowSeconds(), newNonce())
  pendingKey.value = key
  phase.value = 'saving'
  error.value = null
  message.value = null
  conflictKeys.value = []
  try {
    const result = await props.client.saveConfiguration({
      contractRevision: snapshot.value.contractRevision,
      expectedVersion: (base.value ?? snapshot.value).version,
      key,
      changes: built.patch,
      secrets: []
    })
    if (result.code === 12 || ((result.code === 0 || result.code === 13) && !result.receipt)) {
      phase.value = 'unconfirmed'
      return
    }
    pendingKey.value = null
    if (result.code === 0 || result.code === 13) {
      await finishSaved()
      return
    }
    if (result.code === 3) {
      phase.value = 'conflict'
      message.value = '这些设置刚被其他人改过。草稿已保留；读取最新后再确认要不要覆盖。'
      return
    }
    phase.value = 'idle'
    fieldErrors.value = result.errors.map((item) => '字段 ' + String(item.address.field) + '：' + item.code)
    error.value = { code: 'save_' + result.code, message: '保存没有成功（code ' + result.code + '），修改没有生效。' }
  } catch (cause) {
    if (reportAuth(cause)) { phase.value = 'idle'; return }
    const unknown = cause instanceof ZbaNetworkError
      || (cause instanceof ZbaRequestError && (cause.status >= 500
        || (cause.code === 'invalid_json' && cause.status >= 200 && cause.status < 300)))
    if (unknown) { phase.value = 'unconfirmed'; return }
    pendingKey.value = null
    phase.value = 'idle'
    error.value = toUiError(cause)
  }
}

async function finishSaved(): Promise<void> {
  draft.value = {}
  savedOnce.value = true
  phase.value = 'idle'
  await load()
  message.value = '已保存。'
}

/** 结果未知：只用同一个操作键核对，不重发。 */
async function check(): Promise<void> {
  const key = pendingKey.value
  if (key === null) return
  error.value = null
  try {
    const result = await props.client.findConfiguration(key)
    if (result.code === 0 && result.receipt) {
      pendingKey.value = null
      await finishSaved()
      return
    }
    if (result.code === 14 || result.code === 6 || result.code === 5) {
      pendingKey.value = null
      phase.value = 'idle'
      message.value = '服务端没有这次提交的记录，修改没有生效。草稿还在，可以再保存一次。'
      if (result.code !== 14) await load()
      return
    }
    error.value = { code: 'find_' + result.code, message: '核对没有得到结论（code ' + result.code + '），请稍后再试。' }
  } catch (cause) {
    if (!reportAuth(cause)) error.value = toUiError(cause)
  }
}

async function reloadAfterConflict(): Promise<void> {
  const before = base.value?.view ?? null
  if (!(await load())) return
  const next = snapshot.value?.view
  if (next) conflictKeys.value = conflictingPaths(draft.value, before, next)
  base.value = snapshot.value
  phase.value = 'idle'
  message.value = conflictKeys.value.length > 0
    ? conflictKeys.value.length + ' 项和对方的修改重叠（已标出），确认后再保存。'
    : '已读取最新值，你的修改和对方没有重叠，可以直接保存。'
}

/** 关闭：有未保存修改时先确认一次；结果未知时不允许关（核对入口在这里）。 */
function requestClose(): void {
  if (phase.value === 'saving') return
  if (phase.value === 'unconfirmed') {
    message.value = '保存结果还没确认，请先点「核对结果」。'
    return
  }
  if (dirtyCount.value > 0 && !discardArmed.value) {
    discardArmed.value = true
    return
  }
  emit('close', savedOnce.value)
}

function onKeydown(event: KeyboardEvent): void {
  if (event.key === 'Escape') {
    event.preventDefault()
    requestClose()
    return
  }
  if (event.key !== 'Tab') return
  const root = dialog.value
  if (!root) return
  const focusable = Array.from(root.querySelectorAll<HTMLElement>(
    'button:not([disabled]), input:not([disabled]), select:not([disabled]), textarea:not([disabled])'))
  const first = focusable[0]
  const last = focusable[focusable.length - 1]
  if (!first || !last) return
  const active = document.activeElement as HTMLElement | null
  const outside = active === null || !root.contains(active)
  if (event.shiftKey && (active === first || outside)) { event.preventDefault(); last.focus() }
  else if (!event.shiftKey && (active === last || outside)) { event.preventDefault(); first.focus() }
}

function inputValue(event: Event): string {
  return (event.target as HTMLInputElement | HTMLSelectElement).value
}

function checkedValue(event: Event): string {
  return (event.target as HTMLInputElement).checked ? 'true' : 'false'
}

onMounted(async () => {
  void nextTick(() => heading.value?.focus())
  await load()
  phase.value = 'idle'
})

defineExpose({ dirtyCount })
</script>

<template>
  <Teleport to="body">
    <div class="dialog-backdrop" data-testid="plugin-settings" @click.self="requestClose">
      <section
        ref="dialog"
        class="dialog plugin-settings"
        role="dialog"
        aria-modal="true"
        :aria-labelledby="titleId"
        @keydown="onKeydown"
      >
        <header class="plugin-settings-head">
          <div>
            <p class="cap-kind">插件设置</p>
            <h2 :id="titleId" ref="heading" tabindex="-1">{{ props.displayName }}</h2>
            <p class="dialog-copy">{{ props.spec.summary }}</p>
          </div>
          <button type="button" class="icon-close" aria-label="关闭" data-testid="plugin-settings-x" @click="requestClose">×</button>
        </header>

        <p v-if="props.mode !== 'admin'" class="inline-notice" data-testid="plugin-settings-readonly">
          现在是标准模式，只能查看。
          <button type="button" class="tonal inline-action" :disabled="!props.writesEnabled" @click="emit('enable-admin')">
            进入管理员模式
          </button>
        </p>
        <p v-if="phase === 'unconfirmed'" class="inline-notice warn" role="status" data-testid="plugin-settings-unconfirmed">
          <strong>没收到服务端确认，不确定有没有保存成功。</strong>
          修改已锁定，先核对一下结果。
          <button type="button" class="tonal inline-action" data-testid="plugin-settings-check" @click="check">核对结果</button>
        </p>
        <p v-if="phase === 'conflict'" class="inline-notice bad" role="alert" data-testid="plugin-settings-conflict">
          {{ message }}
          <button type="button" class="tonal inline-action" data-testid="plugin-settings-reload" @click="reloadAfterConflict">读取最新</button>
        </p>
        <ErrorNotice :error="error" title="出错了" />
        <ul v-if="fieldErrors.length > 0" class="field-errors" role="alert">
          <li v-for="item in fieldErrors" :key="item">{{ item }}</li>
        </ul>

        <p v-if="phase === 'loading'" class="empty-state">正在读取设置…</p>
        <p v-else-if="fields.length === 0" class="empty-state" data-testid="plugin-settings-empty">这个插件没有可调的设置。</p>
        <ul v-else class="plugin-fields" data-testid="plugin-settings-fields">
          <li
            v-for="item in fields"
            :key="item.section + '.' + item.key"
            class="plugin-field"
            :class="{ dirty: dirty(item.section, item.key, item.field) }"
            :data-testid="'plugin-field-' + item.key"
          >
            <div class="plugin-field-label">
              <label :for="'pf-' + item.key">{{ fieldLabel(item.key) }}</label>
              <span class="config-key">{{ item.key }}</span>
              <span v-if="item.field.rule.effect === 1" class="cell-sub">改完需重启生效</span>
              <span v-if="conflictKeys.includes(item.section + '.' + item.key)" class="cell-sub conflict-flag">与对方修改重叠</span>
            </div>
            <div class="plugin-field-control">
              <template v-if="editable(item.field)">
                <label v-if="valueKind(item.field) === 'boolean'" class="switch">
                  <input
                    :id="'pf-' + item.key"
                    type="checkbox"
                    role="switch"
                    :checked="current(item.section, item.key, item.field) === 'true'"
                    :disabled="locked || phase === 'conflict'"
                    @change="onInput(item.section, item.key, item.field, checkedValue($event))"
                  />
                  <span>{{ current(item.section, item.key, item.field) === 'true' ? '开' : '关' }}</span>
                </label>
                <input
                  v-else
                  :id="'pf-' + item.key"
                  class="config-input"
                  :type="valueKind(item.field) === 'number' ? 'number' : 'text'"
                  :value="current(item.section, item.key, item.field)"
                  :disabled="locked || phase === 'conflict'"
                  @input="onInput(item.section, item.key, item.field, inputValue($event))"
                />
                <button
                  v-if="dirty(item.section, item.key, item.field)"
                  type="button"
                  class="link-button"
                  :disabled="locked"
                  @click="reset(item.section, item.key)"
                >
                  撤销
                </button>
              </template>
              <template v-else>
                <span :id="'pf-' + item.key" class="plugin-field-value">{{ display(item.field) }}</span>
                <span v-if="readOnlyReason(item.field)" class="cell-sub">{{ readOnlyReason(item.field) }}</span>
              </template>
            </div>
          </li>
        </ul>

        <p v-if="discardArmed" class="inline-notice warn" role="status" data-testid="plugin-settings-discard">
          还有 {{ dirtyCount }} 项没保存。再点一次「关闭」会放弃这些修改。
        </p>
        <p v-else-if="message && phase !== 'conflict'" class="inline-notice" role="status" data-testid="plugin-settings-message">{{ message }}</p>

        <div class="dialog-actions">
          <button type="button" class="tonal" data-testid="plugin-settings-close" :disabled="phase === 'saving'" @click="requestClose">
            {{ discardArmed ? '放弃修改并关闭' : '关闭' }}
          </button>
          <button
            v-if="canWrite"
            type="button"
            class="primary"
            data-testid="plugin-settings-save"
            :disabled="locked || phase === 'conflict' || dirtyCount === 0"
            @click="save"
          >
            {{ phase === 'saving' ? '保存中…' : dirtyCount > 0 ? '保存 ' + dirtyCount + ' 项修改' : '保存' }}
          </button>
        </div>
      </section>
    </div>
  </Teleport>
</template>
