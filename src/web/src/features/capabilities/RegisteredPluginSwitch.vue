<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { ZbaClient, ZbaRequestError, type ZbaConfigurationReadResult } from '../../api/zba'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { useConfigurationCommit } from './configuration-commit'

const props = defineProps<{
  client: ZbaClient; pluginKey: string; mode: 'read' | 'admin'; writesEnabled: boolean
  active: boolean; blocked: boolean; runtimeState?: number
}>()
const emit = defineEmits<{ busy: [value: boolean]; saved: []; 'auth-expired': [error: UiError] }>()
const snapshot = ref<ZbaConfigurationReadResult | null>(null)
const desired = ref<boolean | null>(null)
const loading = ref(false)
const reloadRequired = ref(false)
const error = ref<UiError | null>(null)
let alive = true
let sequence = 0
const authorized = computed(() => props.active && props.mode === 'admin' && props.writesEnabled)
const commit = useConfigurationCommit({
  client: () => props.client,
  canWrite: () => authorized.value && !props.blocked && !loading.value && !reloadRequired.value,
  canQuery: () => authorized.value,
  onAuth: value => emit('auth-expired', value)
})
const { phase, pendingKey, error: commitError, notice } = commit
const busy = computed(() => pendingKey.value !== null || phase.value === 'saving' || phase.value === 'checking')
watch(busy, value => emit('busy', value), { flush: 'sync' })
const canSave = computed(() => authorized.value && !props.blocked && !loading.value && !reloadRequired.value
  && phase.value === 'idle' && desired.value !== null)
const runtime = computed(() => props.runtimeState === 4 ? '运行中' : props.runtimeState === 2 ? '已停用' : '以能力目录为准')
async function load(): Promise<boolean> {
  if (!authorized.value || loading.value || (busy.value && phase.value !== 'reload-required')) return false
  const client = props.client
  const current = ++sequence
  loading.value = true
  error.value = null
  try {
    const result = await client.readConfiguration(1)
    if (!alive || client !== props.client || current !== sequence || !authorized.value) return false
    const plugin = result.view?.registered.plugins.find(item => item.key === props.pluginKey)
    if (result.code !== 0 || !plugin) throw new Error('plugin_configuration_unavailable')
    snapshot.value = result
    desired.value = plugin.desiredEnabled
    reloadRequired.value = false
    commit.acknowledgeReload()
    return true
  } catch (cause) {
    if (alive && current === sequence && client === props.client) {
      if (cause instanceof ZbaRequestError && cause.status === 401) emit('auth-expired', toUiError(cause))
      else error.value = toUiError(cause)
    }
    return false
  } finally {
    if (alive && current === sequence) loading.value = false
  }
}
async function confirmed(): Promise<void> {
  // A lost refresh must not expose a toggle based on an old desired state.
  reloadRequired.value = true
  emit('saved')
  await load()
}
async function save(): Promise<void> {
  const base = snapshot.value
  if (!canSave.value || !base || desired.value === null) return
  if (await commit.save(base.generation, {
    contractRevision: base.contractRevision, expectedVersion: base.version,
    changes: { registered: { plugins: [{ key: props.pluginKey, enabled: !desired.value }], policies: [], providers: [] } }, secrets: []
  })) await confirmed()
}
async function check(): Promise<void> { if (await commit.check()) await confirmed() }
watch(authorized, value => { if (value && !snapshot.value) void load() })
onMounted(() => { void load() })
onBeforeUnmount(() => { alive = false; ++sequence })
</script>

<template>
  <section class="plugin-enforcement" data-testid="quota-plugin-switch">
    <h3>配额拦截插件</h3>
    <p>当前运行状态：{{ runtime }}。停用只关闭超额拦截，仍继续记录用量。</p>
    <p v-if="desired !== null" data-testid="quota-plugin-desired">已保存设置：{{ desired ? '启用' : '停用' }}。修改后需重启宿主生效。</p>
    <p v-if="props.mode !== 'admin' || !props.writesEnabled" class="muted">管理员可修改插件启停设置。</p>
    <p v-if="loading" role="status">正在读取插件设置…</p>
    <ErrorNotice v-if="error || commitError" :error="error ?? commitError!" />
    <p v-if="notice" class="notice" role="status">{{ notice }}</p>
    <p v-if="reloadRequired" class="notice warn">设置已保存；请重新读取后再修改。</p>
    <button v-if="authorized && phase === 'idle'" type="button" data-testid="quota-plugin-toggle" :disabled="!canSave" @click="save">{{ desired ? '保存停用设置' : '保存启用设置' }}</button>
    <button v-if="phase === 'unconfirmed'" type="button" data-testid="quota-plugin-check" :disabled="!authorized" @click="check">核对启停提交结果</button>
    <button v-if="phase === 'conflict' || phase === 'reload-required' || reloadRequired || (error && authorized)" type="button" data-testid="quota-plugin-reload" :disabled="!authorized || loading" @click="load">读取最新启停设置</button>
    <p v-if="busy" class="notice warn" role="status">启停提交尚未确认；请保留此页面并核对原回执，不会重复提交。</p>
  </section>
</template>

<style scoped>
.plugin-enforcement { margin-block: 16px; padding: 16px; border: 1px solid var(--border); border-radius: 16px; }
.plugin-enforcement h3 { margin-top: 0; }
.plugin-enforcement button { margin: 4px 8px 4px 0; }
</style>
