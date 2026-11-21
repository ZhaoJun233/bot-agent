<script setup lang="ts">
import BreakableId from '../../shared/ui/BreakableId.vue'
import ErrorNotice from '../../shared/ui/ErrorNotice.vue'
import StatusBadge from '../../shared/ui/StatusBadge.vue'
import UiConfirmDialog from '../../shared/ui/UiConfirmDialog.vue'
import { capabilityTitle, kindName } from '../../shared/friendly'
import {
  actionLabels,
  isDangerousAction,
  operationStateLabels,
  operationStateTones,
  type OperationRecord,
  type PendingAction
} from './use-capability-operations'

const props = withDefaults(defineProps<{
  activeRecord: OperationRecord | null
  waitingRecords: readonly OperationRecord[]
  pendingAction: PendingAction | null
  actionTrigger: HTMLElement | null
  fallbackHeading: HTMLElement | null
  actionBusy: boolean
  actionError: { code: string; message: string; status?: number; requestId?: string } | null
  dismissNotice: string | null
  canWrite: boolean
  hasContext?: boolean
  canQuery: (record: OperationRecord) => boolean
  canRetry: (record: OperationRecord) => boolean
  unsettled: (record: OperationRecord) => boolean
}>(), { hasContext: true })

const emit = defineEmits<{
  confirm: []
  cancelPending: []
  query: [record: OperationRecord]
  retry: [record: OperationRecord]
  cancelRecord: [record: OperationRecord]
  closeResult: []
  focusRecord: [record: OperationRecord]
}>()

function actionLabel(action: number): string {
  return actionLabels[action] ?? `动作 #${action}`
}
function stateLabel(state: number): string {
  return operationStateLabels[state] ?? '未知'
}
function stateTone(state: number): 'ok' | 'warn' | 'bad' | 'neutral' {
  return operationStateTones[state] ?? 'neutral'
}
</script>

<template>
  <section
    v-if="props.activeRecord"
    class="panel op-result-panel"
    data-testid="cap-result"
    aria-labelledby="cap-result-title"
  >
    <div class="panel-heading">
      <div>
        <h2 id="cap-result-title">动作回执 · {{ actionLabel(props.activeRecord.action) }}</h2>
        <p>{{ props.activeRecord.kindLabel }} · {{ props.activeRecord.label }}</p>
      </div>
      <StatusBadge
        v-if="props.activeRecord.result?.receipt"
        :tone="stateTone(props.activeRecord.result.receipt.state)"
        :label="stateLabel(props.activeRecord.result.receipt.state)"
        dot
        data-testid="cap-result-state"
      />
      <StatusBadge
        v-else-if="props.activeRecord.phase === 'unknown'"
        tone="warn"
        label="结果未知"
        dot
      />
    </div>

    <p v-if="props.activeRecord.phase === 'preparing'" class="inline-notice" data-testid="cap-result-preparing">
      正在确认最新状态，尚未发出动作…
    </p>
    <p v-else-if="props.activeRecord.phase === 'submitting'" class="inline-notice" data-testid="cap-result-submitting">
      已提交，正在等待服务端受理…
    </p>
    <p v-else-if="props.activeRecord.checking" class="inline-notice" data-testid="cap-result-checking">
      正在核对并沿用原操作键重发…
    </p>

    <dl class="cap-meta">
      <div>
        <dt>操作键</dt>
        <dd class="mono" data-testid="cap-op-key"><BreakableId :value="props.activeRecord.key" copyable copy-label="复制操作键" /></dd>
      </div>
      <div>
        <dt>结果码</dt>
        <dd data-testid="cap-result-code">{{ props.activeRecord.result ? props.activeRecord.result.code : '—' }}</dd>
      </div>
      <div v-if="props.activeRecord.result?.receipt?.errorCode">
        <dt>错误代码</dt>
        <dd class="cap-error-code mono">{{ props.activeRecord.result.receipt.errorCode }}</dd>
      </div>
      <div v-if="props.activeRecord.cancelledState !== null">
        <dt>取消状态</dt>
        <dd data-testid="cap-cancel-state">{{ stateLabel(props.activeRecord.cancelledState) }}</dd>
      </div>
    </dl>

    <p v-if="props.activeRecord.phase === 'unknown'" class="inline-notice warn" data-testid="cap-result-unknown">
      未收到明确回执，操作键已保留。请先核对；同键重试不会产生重复副作用。
    </p>
    <ErrorNotice :error="props.activeRecord.error" title="动作未完成" />
    <p v-if="props.dismissNotice" class="inline-notice warn" data-testid="cap-result-pinned">
      {{ props.dismissNotice }}
    </p>

    <div class="op-actions">
      <button
        v-if="props.canQuery(props.activeRecord)"
        type="button"
        class="tonal"
        data-testid="cap-result-query"
        :disabled="props.activeRecord.busy"
        @click="emit('query', props.activeRecord)"
      >
        {{ props.activeRecord.busy ? '核对中…' : '核对结果' }}
      </button>
      <button
        v-if="props.canRetry(props.activeRecord)"
        type="button"
        class="tonal"
        data-testid="cap-result-retry"
        :disabled="props.activeRecord.busy || !props.canWrite"
        @click="emit('retry', props.activeRecord)"
      >
        同键重试
      </button>
      <button
        v-if="props.canQuery(props.activeRecord)"
        type="button"
        class="tonal"
        data-testid="cap-result-cancel"
        :disabled="props.activeRecord.busy || !props.canWrite"
        @click="emit('cancelRecord', props.activeRecord)"
      >
        请求取消
      </button>
      <button
        type="button"
        class="ghost"
        data-testid="cap-result-close"
        @click="emit('closeResult')"
      >
        {{ props.unsettled(props.activeRecord) ? '保持跟踪' : '关闭回执' }}
      </button>
    </div>

    <div v-if="props.waitingRecords.length > 0" class="op-waiting">
      <p class="op-waiting-title">其它未终态操作（{{ props.waitingRecords.length }}）</p>
      <ul class="cap-ops-items">
        <li v-for="record in props.waitingRecords" :key="record.key">
          <div class="cap-op-desc">
            <strong>{{ actionLabel(record.action) }} · {{ record.label }}</strong>
            <span class="cap-op-state"> · <code>{{ record.key }}</code></span>
          </div>
          <button type="button" class="tonal" @click="emit('focusRecord', record)">查看</button>
        </li>
      </ul>
    </div>
  </section>

  <UiConfirmDialog
    v-if="props.pendingAction"
    :title="`确认执行「${actionLabel(props.pendingAction.capability.action)}」`"
    title-id="cap-confirm-title"
    test-id="cap-confirm"
    :trigger="props.actionTrigger"
    :fallback="props.fallbackHeading"
    :danger="isDangerousAction(props.pendingAction.capability.action)"
    :busy="props.actionBusy"
    :disabled="!props.canWrite || !props.hasContext"
    :confirm-label="props.actionBusy ? '提交中…' : `确认${actionLabel(props.pendingAction.capability.action)}`"
    @confirm="emit('confirm')"
    @cancel="emit('cancelPending')"
  >
    <p>
      即将对 <strong>{{ capabilityTitle(props.pendingAction.descriptor.displayName, props.pendingAction.descriptor.key) }}</strong>
      （{{ kindName(props.pendingAction.descriptor.key.kind) }}）执行
      <strong>{{ actionLabel(props.pendingAction.capability.action) }}</strong>。
    </p>
    <p v-if="!props.hasContext" class="inline-notice bad">
      当前尚未取得配置代际，无法提交动作；请关闭后刷新再试。
    </p>
    <p v-if="props.pendingAction.reuseKey" class="inline-notice warn">
      检测到同目标存在未确认的操作键，本次将沿用原键重发，避免重复执行。
    </p>
    <ErrorNotice :error="props.actionError" title="无法提交动作" />
  </UiConfirmDialog>
</template>

<style scoped>
.op-result-panel {
  display: grid;
  gap: 14px;
}
.op-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding-top: 8px;
  border-top: 1px solid var(--border-subtle);
}
.op-waiting {
  display: grid;
  gap: 8px;
  padding-top: 10px;
  border-top: 1px solid var(--border-subtle);
}
.op-waiting-title {
  margin: 0;
  font-size: var(--text-xs);
  color: var(--text-soft);
  font-weight: 600;
}
</style>
