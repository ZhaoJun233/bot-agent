<script setup lang="ts">
import { computed } from 'vue'
import type { ZbaPlatformPolicyEntryView } from '../../api/zba'
import {
  POLICY_ACTIONS,
  POLICY_FEATURES,
  policyBaseline,
  policyDirty,
  policyKey,
  policyTitle,
  type PolicyDrafts,
  type PolicyRowDraft
} from './policy-draft'

/**
 * 平台策略结构化编辑器（#104 S2）。
 *
 * 条目是判定的唯一真相：条目里显式写了的字段压过标量基线，只有「跟随基线」
 * 才回落到面板上的开关与白名单文本。S6 删旧前端时这套界面一起丢了，于是出现
 * 「面板能改的东西不参与判定，参与判定的东西改不了」。
 *
 * 保持受控：不持有本地状态，一律从 `modelValue`（只含改过的行）派生。
 */
const props = withDefaults(defineProps<{
  entries: ZbaPlatformPolicyEntryView[]
  modelValue: PolicyDrafts
  locked?: boolean
}>(), { locked: false })

const emit = defineEmits<{ 'update:modelValue': [value: PolicyDrafts] }>()

const toggleLabels = { 0: '跟随基线', 1: '启用', 2: '停用' } as const
const chatLabels = { 0: '跟随基线', 1: '可聊', 2: '禁聊' } as const
const listLabels = { 0: '跟随基线', 1: '显式名单', 2: '全收' } as const
const actionLabels = { 0: '跟随基线', 1: '显式名单' } as const

const rows = computed(() => props.entries.map((entry) => ({
  entry,
  title: policyTitle(entry.target),
  draft: props.modelValue[policyKey(entry.target)] ?? policyBaseline(entry),
  dirty: policyDirty(props.modelValue[policyKey(entry.target)], policyBaseline(entry)),
  legacy: entry.enabled.rule.legacyNeedsReview
    || entry.groupWhitelist.rule.legacyNeedsReview
    || entry.privateWhitelist.rule.legacyNeedsReview,
  unnamedInstance: entry.target.kind === 'instance' && entry.target.key.instanceId.trim().length === 0
})))

const actionHint = POLICY_ACTIONS.join(' / ')

function update(entry: ZbaPlatformPolicyEntryView, mutate: (row: PolicyRowDraft) => void): void {
  if (props.locked) return
  const key = policyKey(entry.target)
  const baseline = policyBaseline(entry)
  const row: PolicyRowDraft = { ...(props.modelValue[key] ?? baseline), features: (props.modelValue[key] ?? baseline).features.map(item => ({ ...item })) }
  mutate(row)
  const next: PolicyDrafts = { ...props.modelValue }
  if (policyDirty(row, baseline)) next[key] = row
  else delete next[key]
  emit('update:modelValue', next)
}

function featureValue(row: PolicyRowDraft, feature: number): 'inherit' | 'on' | 'off' {
  const found = row.features.find(item => item.feature === feature)
  if (found === undefined) return 'inherit'
  return found.enabled ? 'on' : 'off'
}

function setFeature(entry: ZbaPlatformPolicyEntryView, feature: number, value: string): void {
  update(entry, (row) => {
    row.features = row.features.filter(item => item.feature !== feature)
    if (value === 'on' || value === 'off') row.features.push({ feature, enabled: value === 'on' })
  })
}

function explicitCount(row: PolicyRowDraft): number {
  return row.features.length
}
</script>

<template>
  <div class="policy-editor" data-testid="config-policy-editor">
    <p class="config-guide">
      条目里显式写过的字段压过上面的开关与白名单文本；只有「跟随基线」才回落到面板标量。
      保存只提交改过的行。
    </p>
    <p v-if="entries.length === 0" class="empty-state">没有策略条目：全部平台按标量基线判定。</p>
    <div v-else class="policy-rows">
      <fieldset
        v-for="row in rows"
        :key="policyKey(row.entry.target)"
        class="policy-row"
        :class="{ dirty: row.dirty }"
        :data-testid="`config-policy-${policyKey(row.entry.target)}`"
      >
        <legend class="policy-title">
          {{ row.title }}
          <span v-if="row.dirty" class="cell-sub">已修改</span>
        </legend>
        <p v-if="row.legacy" class="cell-sub conflict-flag">旧条目缺少显式字段：服务端要求人工核对后才允许改写。</p>
        <p v-if="row.unnamedInstance" class="cell-sub conflict-flag">
          实例目标缺少 instanceId：这是内置驱动自己的身份（qq.private / qq.official / local 的驱动标识本来就是空的），不是「某个具体实例」。该平台只登记一个驱动时它照常生效；登记多个驱动时通道级判定仍会失败关闭。
        </p>

        <div class="policy-grid">
          <label class="policy-field">
            <span class="meta-label">启用</span>
            <select
              class="config-input"
              :value="row.draft.enabled"
              :disabled="locked"
              :aria-label="`${row.title} 启用状态`"
              @change="update(row.entry, draft => { draft.enabled = Number(($event.target as HTMLSelectElement).value) })"
            >
              <option v-for="(label, value) in toggleLabels" :key="value" :value="Number(value)">{{ label }}</option>
            </select>
          </label>

          <label class="policy-field">
            <span class="meta-label">聊天</span>
            <select
              class="config-input"
              :value="row.draft.chatEnabled"
              :disabled="locked"
              :aria-label="`${row.title} 聊天状态`"
              @change="update(row.entry, draft => { draft.chatEnabled = Number(($event.target as HTMLSelectElement).value) })"
            >
              <option v-for="(label, value) in chatLabels" :key="value" :value="Number(value)">{{ label }}</option>
            </select>
          </label>

          <div class="policy-field policy-list">
            <span class="meta-label">群名单</span>
            <select
              class="config-input"
              :value="row.draft.groupKind"
              :disabled="locked"
              :aria-label="`${row.title} 群名单模式`"
              @change="update(row.entry, draft => { draft.groupKind = Number(($event.target as HTMLSelectElement).value) })"
            >
              <option v-for="(label, value) in listLabels" :key="value" :value="Number(value)">{{ label }}</option>
            </select>
            <input
              v-if="row.draft.groupKind === 1"
              class="config-input"
              type="text"
              :value="row.draft.groupValue"
              :disabled="locked"
              :aria-label="`${row.title} 群名单`"
              placeholder="群号，逗号或空白分隔；留空表示显式空（全拦）"
              @input="update(row.entry, draft => { draft.groupValue = ($event.target as HTMLInputElement).value })"
            />
            <span v-else-if="row.draft.groupKind === 2" class="cell-sub">显式全收：不再看基线白名单。</span>
            <span v-if="row.draft.groupKind === 1 && row.draft.groupValue.trim().length === 0" class="cell-sub">
              显式空名单 = 全拦名单外所有（和「全收」相反）；要全收请把模式改成「全收」。
            </span>
          </div>

          <div class="policy-field policy-list">
            <span class="meta-label">私聊名单</span>
            <select
              class="config-input"
              :value="row.draft.privateKind"
              :disabled="locked"
              :aria-label="`${row.title} 私聊名单模式`"
              @change="update(row.entry, draft => { draft.privateKind = Number(($event.target as HTMLSelectElement).value) })"
            >
              <option v-for="(label, value) in listLabels" :key="value" :value="Number(value)">{{ label }}</option>
            </select>
            <input
              v-if="row.draft.privateKind === 1"
              class="config-input"
              type="text"
              :value="row.draft.privateValue"
              :disabled="locked"
              :aria-label="`${row.title} 私聊名单`"
              placeholder="QQ 号，逗号或空白分隔；留空表示显式空（全拦）"
              @input="update(row.entry, draft => { draft.privateValue = ($event.target as HTMLInputElement).value })"
            />
            <span v-else-if="row.draft.privateKind === 2" class="cell-sub">显式全收：不再看基线白名单。</span>
            <span v-if="row.draft.privateKind === 1 && row.draft.privateValue.trim().length === 0" class="cell-sub">
              显式空名单 = 全拦名单外所有（和「全收」相反）；要全收请把模式改成「全收」。
            </span>
          </div>

          <div class="policy-field policy-actions">
            <span class="meta-label">动作</span>
            <select
              class="config-input"
              :value="row.draft.actionKind"
              :disabled="locked"
              :aria-label="`${row.title} 动作名单模式`"
              @change="update(row.entry, draft => { draft.actionKind = Number(($event.target as HTMLSelectElement).value) })"
            >
              <option v-for="(label, value) in actionLabels" :key="value" :value="Number(value)">{{ label }}</option>
            </select>
            <input
              v-if="row.draft.actionKind === 1"
              class="config-input"
              type="text"
              :value="row.draft.actions"
              :disabled="locked"
              :aria-label="`${row.title} 动作名单`"
              :placeholder="actionHint"
              @input="update(row.entry, draft => { draft.actions = ($event.target as HTMLInputElement).value })"
            />
            <span class="cell-sub">可用动作：{{ actionHint }}；留空表示一条动作都不允许。</span>
          </div>
        </div>

        <details class="policy-features">
          <summary>功能覆盖（{{ explicitCount(row.draft) }} 项显式，共 {{ POLICY_FEATURES.length }} 项）</summary>
          <div class="feature-grid">
            <label v-for="feature in POLICY_FEATURES" :key="feature.id" class="policy-field feature">
              <span class="meta-label">{{ feature.label }}</span>
              <select
                class="config-input"
                :value="featureValue(row.draft, feature.id)"
                :disabled="locked"
                :aria-label="`${row.title} 功能 ${feature.label}`"
                @change="setFeature(row.entry, feature.id, ($event.target as HTMLSelectElement).value)"
              >
                <option value="inherit">跟随基线</option>
                <option value="on">开</option>
                <option value="off">关</option>
              </select>
            </label>
          </div>
        </details>
      </fieldset>
    </div>
  </div>
</template>

<style scoped>
.policy-editor { display: flex; flex-direction: column; gap: 0.75rem; }
.policy-rows { display: flex; flex-direction: column; gap: 0.75rem; }
.policy-row {
  border: 1px solid var(--border, #d8dee9);
  border-radius: 8px;
  padding: 0.75rem;
  display: flex;
  flex-direction: column;
  gap: 0.5rem;
}
.policy-row.dirty { border-color: var(--accent, #2f6fed); }
.policy-title { font-weight: 600; padding: 0 0.25rem; }
.policy-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr));
  gap: 0.5rem 0.75rem;
}
.policy-field { display: flex; flex-direction: column; gap: 0.25rem; }
.policy-list, .policy-actions { gap: 0.35rem; }
.policy-features summary { cursor: pointer; }
.feature-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(8rem, 1fr));
  gap: 0.5rem;
  margin-top: 0.5rem;
}
</style>
