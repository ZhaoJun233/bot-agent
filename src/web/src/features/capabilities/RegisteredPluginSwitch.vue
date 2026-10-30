<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaConfigurationReadResult } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { buildPatch, fieldPath } from '../configuration/draft'
import { settingsFor } from './plugin-settings'
import { switchState } from './plugin-toggle'
import { useConfigurationCommit } from './configuration-commit'

const props = withDefaults(defineProps<{
  client: ZbaClient; pluginKey: string; mode: 'read' | 'admin'; writesEnabled: boolean
  active: boolean; blocked: boolean; runtimeState?: number
  compact?: boolean; title?: string; configuration?: ZbaConfigurationReadResult | null
}>(), { compact: false })
const emit = defineEmits<{ busy: [value: boolean]; pinned: [value: boolean]; saved: []; 'auth-expired': [error: UiError] }>()
// An unsettled intent belongs to its original client and plugin, never to the next scope.
const ownerClient = props.client
const ownerKey = props.pluginKey
const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const desired = ref<boolean | null>(null)
const target = ref<boolean | null>(null)
const editable = ref(false)
const loading = ref(false)
const reloadRequired = ref(false)
const error = ref<UiError | null>(null)
let alive = true
let sequence = 0
let ownership = 0
const sameOwner = computed(() => props.client === ownerClient && props.pluginKey === ownerKey)
const authorized = computed(() => sameOwner.value && props.active && props.mode === 'admin' && props.writesEnabled)
const spec = settingsFor(ownerKey)
const fieldSwitch = !!spec?.enableField
const title = computed(() => props.title ?? spec?.title ?? '插件')
const commit = useConfigurationCommit({
  client: () => ownerClient,
  canWrite: () => authorized.value && !props.blocked && !loading.value && !reloadRequired.value && editable.value,
  canQuery: () => authorized.value,
  onAuth: value => emit('auth-expired', value)
})
const { phase, pendingKey, error: commitError, notice } = commit
const busy = computed(() => pendingKey.value !== null || phase.value === 'saving' || phase.value === 'checking')
watch(busy, value => emit('busy', value), { flush: 'sync' })
watch(() => busy.value || target.value !== null || reloadRequired.value || !!error.value || !!commitError.value, value => emit('pinned', value), { flush: 'sync' })
const canSave = computed(() => authorized.value && !props.blocked && !loading.value && !reloadRequired.value
  && editable.value && phase.value === 'idle' && desired.value !== null)
const checked = computed(() => target.value ?? desired.value ?? false)
const testId = (part: string): string => props.compact ? 'cap-switch-' + part + 'plugin:' + ownerKey : 'quota-plugin-' + part
const runtime = computed(() => props.runtimeState === 4 ? '运行中' : props.runtimeState === 2 ? '已停用' : '以能力目录为准')

function accept(result: ZbaConfigurationReadResult | null, retainIntent = false): boolean {
  snapshot.value = null
  editable.value = false
  if (!result || result.code !== 0 || result.contractRevision !== 'cfg-0.3.3' || !result.view
    || !/^[a-f0-9]{32}$/i.test(result.generation) || !result.version) {
    desired.value = null
    return false
  }
  if (fieldSwitch && spec) {
    const state = switchState(spec, result.view)
    desired.value = state.enabled
    editable.value = state.editable
  } else {
    const matches = result.view.registered.plugins.filter(item => item.key === ownerKey)
    desired.value = matches.length === 1 && typeof matches[0]?.desiredEnabled === 'boolean' ? matches[0].desiredEnabled : null
    editable.value = desired.value !== null
  }
  if (desired.value === null) return false
  snapshot.value = result
  if (!retainIntent) target.value = null
  return true
}

async function load(): Promise<boolean> {
  if (!sameOwner.value || !props.active || loading.value || (busy.value && phase.value !== 'reload-required')) return false
  const current = ++sequence
  loading.value = true
  error.value = null
  try {
    const result = await ownerClient.readConfiguration(1)
    if (!alive || !sameOwner.value || current !== sequence || !props.active) return false
    const retainIntent = target.value !== null
    if (!accept(result, retainIntent)) throw new Error('plugin_configuration_unavailable')
    reloadRequired.value = false
    commit.acknowledgeReload()
    return true
  } catch (cause) {
    if (alive && sameOwner.value && current === sequence && props.active) {
      editable.value = false
      if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', toUiError(cause))
      else error.value = toUiError(cause)
    }
    return false
  } finally {
    if (alive && current === sequence) loading.value = false
  }
}
async function confirmed(epoch: number): Promise<void> {
  target.value = null
  reloadRequired.value = true
  // Record confirmation while hidden, but never launch a follow-up request after losing ownership/authorization.
  if (!authorized.value || epoch !== ownership) return
  emit('saved')
  await load()
}
async function save(value = !checked.value): Promise<void> {
  const base = snapshot.value
  if (!canSave.value || !base?.view || desired.value === null) return
  const epoch = ownership
  target.value = value
  const changes = fieldSwitch && spec?.enableField
    ? buildPatch({ [fieldPath(spec.enableField.section, spec.enableField.key)]: { kind: 'set', raw: String(value) } }, base.view).patch
    : { registered: { plugins: [{ key: ownerKey, enabled: value }], policies: [], providers: [] } }
  if (await commit.save(base.generation, {
    contractRevision: base.contractRevision, expectedVersion: base.version, changes, secrets: []
  })) await confirmed(epoch)
}
function change(event: Event): void {
  const input = event.target as HTMLInputElement
  const value = input.checked
  input.checked = checked.value
  void save(value)
}
async function check(): Promise<void> { const epoch = ownership; if (await commit.check()) await confirmed(epoch) }
watch(() => props.configuration, value => {
  // Background card refreshes cannot erase a conflict, rejection, or unknown intent.
  if (!props.compact) return
  if (!value || value.code !== 0 || value.contractRevision !== 'cfg-0.3.3') {
    editable.value = false
    if (snapshot.value) reloadRequired.value = true
    return
  }
  if (phase.value === 'idle' && target.value === null && !reloadRequired.value) accept(value)
}, { immediate: true })
watch(() => [props.active, props.mode, props.writesEnabled, props.client, props.pluginKey], () => {
  ++sequence
  ++ownership
  loading.value = false
  if (sameOwner.value && props.active && !busy.value && (!snapshot.value || reloadRequired.value) && !props.compact) void load()
}, { flush: 'sync' })
onMounted(() => { if (!props.compact) void load() })
onBeforeUnmount(() => { alive = false; ++sequence })
</script>

<template>
  <section v-if="!props.compact || desired !== null || target !== null || busy || error || commitError"
    class="plugin-enforcement" :class="{ compact: props.compact }" :data-testid="props.compact ? testId('row-') : 'quota-plugin-switch'">
    <template v-if="!props.compact">
      <h3>配额拦截插件</h3>
      <p>当前运行状态：{{ runtime }}。停用只关闭超额拦截，仍继续记录用量。</p>
    </template>
    <div class="plugin-switch-row">
      <label v-if="props.compact" class="plugin-switch-control">
        <input type="checkbox" role="switch" :checked="checked" :aria-checked="checked" :disabled="!canSave"
          :aria-label="'启用 ' + title" :data-testid="'cap-switch-plugin:' + ownerKey" @change="change" />
        <span>{{ fieldSwitch ? (checked ? '功能已开启' : '功能已关闭') : (checked ? '插件已启用' : '插件已停用') }}</span>
      </label>
      <button v-else type="button" role="switch" class="plugin-switch-button" :aria-checked="checked" :aria-label="'启用 ' + title"
        data-testid="quota-plugin-toggle" :disabled="!canSave" @click="save(target ?? !checked)">
        <span class="plugin-switch-track" aria-hidden="true"><span /></span>
        {{ checked ? '保存停用设置' : '保存启用设置' }}
      </button>
      <span v-if="!authorized" class="muted">管理员模式下可切换</span>
    </div>
    <p v-if="desired !== null" :data-testid="props.compact ? undefined : 'quota-plugin-desired'">
      已保存设置：{{ desired ? '启用' : '停用' }}。<template v-if="!fieldSwitch && (!props.compact || !notice.includes('需重启'))">修改后需重启宿主生效，不会自动重启。</template>
    </p>
    <p v-if="target !== null" class="notice warn" role="status">保留的启停意图：{{ target ? '启用' : '停用' }}。读取最新后请核对，再明确保存此意图。</p>
    <p v-if="loading" role="status">正在读取插件设置…</p>
    <ErrorNotice v-if="error || commitError" :error="error ?? commitError!" />
    <p v-if="notice" class="notice" role="status" :data-testid="testId('notice-')">{{ notice }}</p>
    <p v-if="reloadRequired" class="notice warn">设置已保存；请重新读取后再修改。</p>
    <button v-if="target !== null && phase === 'idle'" type="button" :disabled="!canSave" :data-testid="testId('save-')" @click="save(target)">保存保留的启停意图</button>
    <button v-if="phase === 'unconfirmed'" type="button" :data-testid="props.compact ? testId('check-') : 'quota-plugin-check'" :disabled="!authorized" @click="check">核对启停提交结果</button>
    <button v-if="phase === 'conflict' || phase === 'reload-required' || reloadRequired || error || commitError" type="button"
      :data-testid="props.compact ? testId('reload-') : 'quota-plugin-reload'" :disabled="!sameOwner || !props.active || loading || (busy && phase !== 'reload-required')" @click="load">读取最新启停设置</button>
    <p v-if="busy" class="notice warn" role="status">启停提交尚未确认；操作键已保留，请核对原回执，不会重复提交。</p>
  </section>
</template>

<style scoped>
.plugin-enforcement { margin-block: 16px; padding: 16px; border: 1px solid var(--border); border-radius: 8px; }
.plugin-enforcement.compact { margin: 0; padding: 10px; background: var(--surface-muted); font-size: 12px; }
.plugin-enforcement h3 { margin-top: 0; }
.plugin-enforcement p { margin-block: 8px; }
.plugin-enforcement button { margin: 4px 8px 4px 0; }
.plugin-switch-row, .plugin-switch-control, .plugin-switch-button { display: flex; align-items: center; gap: 10px; }
.plugin-switch-row { flex-wrap: wrap; justify-content: space-between; }
.plugin-switch-control { min-height: 32px; cursor: pointer; }
.plugin-switch-control input { appearance: none; width: 34px; height: 20px; min-height: 20px; padding: 0; flex: none; margin: 0; border: 1px solid var(--border-strong); border-radius: 20px; background: var(--border-strong); cursor: pointer; }
.plugin-switch-control input::before { content: ''; display: block; width: 14px; height: 14px; margin: 2px; border-radius: 50%; background: var(--surface); }
.plugin-switch-control input:checked { background: var(--accent-deep); }
.plugin-switch-control input:checked::before { transform: translateX(14px); }
.plugin-switch-control input:disabled { opacity: .5; cursor: not-allowed; }
.plugin-switch-control input:focus-visible { outline: 2px solid var(--accent-deep); outline-offset: 3px; }
.plugin-switch-track { width: 34px; height: 20px; border-radius: 20px; background: var(--border-strong); }
.plugin-switch-track span { display: block; width: 14px; height: 14px; margin: 3px; border-radius: 50%; background: var(--surface); }
.plugin-switch-button[aria-checked="true"] .plugin-switch-track { background: var(--accent-deep); }
.plugin-switch-button[aria-checked="true"] .plugin-switch-track span { transform: translateX(14px); }
</style>
