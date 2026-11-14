<script setup lang="ts">
import { onBeforeUnmount, ref, watch } from 'vue'
import type { ZbaClient, ZbaManagedObjectKey } from '../../api/zba'
import type { MessagingCapability, MessagingCapabilitySnapshot } from '../../api/messaging-capabilities'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import ManagementDialog from '../../shared/ui/ManagementDialog.vue'
import { messagingName, conditionLabel, conditionReason } from './messaging-labels'

const props = defineProps<{ client: ZbaClient; target: ZbaManagedObjectKey; active: boolean; trigger?: HTMLElement | null; fallback?: HTMLElement | null }>()
const emit = defineEmits<{ close: []; 'auth-expired': [error: UiError] }>()
const snapshot = ref<MessagingCapabilitySnapshot | null>(null)
const busy = ref(false)
const error = ref<UiError | null>(null)
const unavailable = ref(false)
const detail = ref<MessagingCapability | null>(null)
const detailTrigger = ref<HTMLElement | null>(null)
let epoch = 0
async function refresh(): Promise<void> {
  const request = ++epoch
  const client = props.client
  snapshot.value = null
  detail.value = null
  unavailable.value = false
  error.value = null
  busy.value = true
  try {
    const result = await client.inspectCapability(props.target)
    if (request !== epoch || !props.active || client !== props.client) return
    if (result.code !== 0) {
      error.value = { code: 'messaging_inspect_' + result.code, message: result.code === 1 ? '当前身份没有查看权限。' : '无法读取此驱动，请关闭后重新加载能力列表。' }
      return
    }
    const actual = result.descriptor?.key
    if (actual?.kind !== 'driver' || props.target.kind !== 'driver'
      || actual.key.platformId !== props.target.key.platformId || actual.key.accountScope !== props.target.key.accountScope
      || actual.key.instanceId !== props.target.key.instanceId) {
      error.value = { code: 'messaging_target_mismatch', message: '返回的驱动身份与当前选择不符，请重新读取。' }
      return
    }
    snapshot.value = result.messaging ?? null
    unavailable.value = !result.messaging
  } catch (cause) {
    if (request !== epoch || !props.active || client !== props.client) return
    error.value = toUiError(cause)
    if (error.value.status === 401 || error.value.status === 403) emit('auth-expired', error.value)
  } finally { if (request === epoch) busy.value = false }
}
function explain(item: MessagingCapability, event: Event): void {
  detailTrigger.value = event.currentTarget instanceof HTMLElement ? event.currentTarget : null
  detail.value = item
}
watch(() => [props.client, props.target, props.active], () => {
  epoch++
  snapshot.value = null
  detail.value = null
  if (props.active) void refresh()
}, { immediate: true })
onBeforeUnmount(() => { epoch++ })
</script>

<template>
  <ManagementDialog title="消息能力与限制" title-id="official-messaging-title" test-id="official-messaging-dialog"
    :active="props.active" :trigger="props.trigger" :fallback="props.fallback" drawer @close="emit('close')">
    <p class="scope">所选 QQ 官方驱动 · 未选择会话</p>
    <p class="scope-note">平台策略检查只覆盖本地条件。应用权限和具体会话条件需分别核实。</p>
    <div class="observation">
      <p aria-live="polite" data-testid="messaging-observed-at">{{ busy ? '正在读取…' : snapshot ? '读取时间：' + new Date(snapshot.observedAt).toLocaleString() : '尚无可用快照' }}</p>
      <button type="button" class="tonal" data-testid="messaging-refresh" :disabled="busy" @click="refresh">重新读取</button>
    </div>
    <ErrorNotice :error="error" title="读取消息能力失败" />
    <p v-if="unavailable" class="scope-note" data-testid="messaging-unavailable">当前宿主未提供消息能力快照，请核对宿主版本。不能据此判断功能可用。</p>
    <div v-if="snapshot" class="capability-list" data-testid="messaging-list">
      <article v-for="item in snapshot.items" :key="item.id" class="messaging-row" :data-testid="'messaging-' + item.id">
        <div class="row-title"><h3>{{ messagingName(item.id) }}</h3><span class="implementation">{{ item.implementation === 0 ? '已实现' : '待交付' }}</span></div>
        <dl>
          <div><dt>平台策略</dt><dd :data-state="item.policy.state">{{ conditionLabel(item.policy) }}</dd></div>
          <div><dt>应用权限</dt><dd :data-state="item.applicationGrant.state">{{ conditionLabel(item.applicationGrant) }}</dd></div>
        </dl>
        <button type="button" class="link-button" :data-testid="'messaging-reason-' + item.id" :aria-label="'查看' + messagingName(item.id) + '的条件与原因'" @click="explain(item, $event)">条件与原因 →</button>
      </article>
    </div>
  </ManagementDialog>
  <ManagementDialog v-if="detail" :title="messagingName(detail.id) + ' · 条件与原因'" title-id="messaging-reason-title" test-id="messaging-reason-dialog"
    :active="props.active" :trigger="detailTrigger" :fallback="props.fallback" @close="detail = null">
    <p v-if="detail.implementation === 1" class="scope-note">此能力尚未交付，当前不会执行。</p>
    <dl class="reason-list">
      <div v-for="entry in [{ name: '平台策略', value: detail.policy }, { name: '应用权限', value: detail.applicationGrant }, { name: '会话条件', value: detail.conversation }, { name: '操作者条件', value: detail.actor }]" :key="entry.name">
        <dt>{{ entry.name }} · {{ conditionLabel(entry.value) }}</dt><dd>{{ conditionReason(entry.value) }}</dd>
      </div>
    </dl>
    <p class="scope-note">发送失败或结果未知时应保留原回执并核对状态。需要改用普通文本时，先确认原请求未发送；不会自动重复发送。</p>
    <button type="button" class="tonal" data-testid="messaging-reason-back" @click="detail = null">返回能力列表</button>
  </ManagementDialog>
</template>

<style scoped>
.scope { margin: 0 0 8px; font-weight: 600; }
.scope-note { color: var(--text-muted); line-height: 1.7; }
.observation { display: flex; align-items: center; justify-content: space-between; gap: 16px; margin: 20px 0; }
.observation p { color: var(--text-muted); font-size: 13px; }
.observation button { flex-shrink: 0; }
.capability-list { display: grid; gap: 12px; }
.messaging-row { border: 1px solid var(--border); border-radius: 14px; padding: 16px 18px; }
.row-title { display: flex; align-items: baseline; justify-content: space-between; gap: 12px; }
.row-title h3 { margin: 0; font-size: 15px; }
.implementation { color: var(--text-muted); white-space: nowrap; font-size: 12px; }
.messaging-row dl { display: flex; flex-wrap: wrap; gap: 16px 40px; margin: 14px 0; font-size: 13px; }
.messaging-row dl div { display: flex; gap: 10px; }
dt { color: var(--text-muted); }
dd { margin: 0; }
dd[data-state="1"], dd[data-state="3"] { font-weight: 600; }
.reason-list { display: grid; gap: 22px; margin: 24px 0; }
.reason-list dt { font-weight: 600; color: var(--text); margin-bottom: 7px; }
.reason-list dd { line-height: 1.7; color: var(--text-muted); }
</style>
