<script setup lang="ts">
import { useId } from 'vue'
import type { RuntimeEffectPresentation } from './runtime-effects'
import type { ZbaReadField, ZbaListRuleValue } from '../../api/zba'
import { draftListSummary, policyDisplay, policySource, policyStored } from './policy-presentation'

const props = defineProps<{
  label: string
  kind: number
  value: string
  stored: ZbaReadField<ZbaListRuleValue>
  effective?: ZbaReadField<ZbaListRuleValue>
  runtime?: RuntimeEffectPresentation
  testid: string
}>()
const emit = defineEmits<{ mode: [value: number]; text: [value: string] }>()
const id = useId()
</script>

<template>
  <div class="list-control">
    <label :for="id">{{ props.label }}</label>
    <select
      :id="id"
      :value="kind"
      :data-testid="testid + '-kind'"
      @change="emit('mode', Number(($event.target as HTMLSelectElement).value))"
    >
      <option :value="0">使用默认规则</option>
      <option :value="1">只允许名单中的人</option>
      <option :value="2">允许所有人</option>
      <option :value="3">拒绝所有人</option>
    </select>
    <label v-if="kind === 1" :for="id + '-list'" class="sub-label">
      允许的{{ label.includes('群') ? '群号' : '用户号' }}（逗号或空格分隔）
    </label>
    <textarea
      v-if="kind === 1"
      :id="id + '-list'"
      :value="value"
      rows="2"
      :data-testid="testid + '-value'"
      placeholder="例如 10001"
      @input="emit('text', ($event.target as HTMLTextAreaElement).value)"
    />
    <div class="policy-summary-box">
      <p class="stored">{{ policyStored(stored) }}</p>
      <p :data-testid="testid + '-runtime'">
        <strong>当前运行：</strong>{{ runtime?.label ?? '当前生效情况待确认' }}
        <template v-if="runtime?.state === 'current'">
          · {{ policyDisplay(effective) }}
          <span v-if="effective"> · 来源：{{ policySource(effective) }}</span>
        </template>
      </p>
      <p><strong>待保存：</strong>{{ draftListSummary(kind, value) }}</p>
    </div>
    <p v-if="kind === 3 || (kind === 1 && !value.trim())" class="warning" role="status">
      此选择将明确拒绝所有人，不会回退到默认名单；保存前需要二次确认。
    </p>
    <p v-else-if="kind === 2" class="stored">
      仅放开接收范围；仍受聊天开关与其它规则限制，不会授予管理命令权限。
    </p>
  </div>
</template>

<style scoped>
.list-control {
  display: grid;
  gap: 0.5rem;
  min-width: 0;
  padding: 0.9rem;
  border: 1px solid var(--border);
  border-radius: var(--radius);
  background: var(--surface-muted);
}
.list-control p {
  margin: 0;
  font-size: var(--text-xs);
  line-height: 1.55;
  overflow-wrap: anywhere;
}
.list-control label {
  font-size: var(--text-sm);
  font-weight: 600;
  color: var(--text);
}
.list-control .sub-label {
  font-size: var(--text-xs);
  color: var(--text-muted);
  font-weight: 500;
}
.list-control select,
.list-control textarea {
  width: 100%;
  min-width: 0;
  box-sizing: border-box;
  font: inherit;
}
.policy-summary-box {
  display: grid;
  gap: 4px;
  padding-top: 4px;
}
.stored {
  color: var(--text-soft);
}
.warning {
  color: var(--warn-text);
  font-weight: 500;
}
</style>
