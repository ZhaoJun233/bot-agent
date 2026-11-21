<script setup lang="ts">
import { computed } from 'vue'
import type { ZbaConfigurationRuntimeEffect, ZbaPlatformPolicyEntryView } from '../../api/zba'
import type { PlatformPolicyCopy } from '../capabilities/plugin-settings'
import { POLICY_ACTIONS, POLICY_FEATURES, policyBaseline, policyDirty, policyKey, policyTitle, splitActions, type PolicyDrafts, type PolicyRowDraft } from './policy-draft'
import { actionLabels, overrideCount, policyScope } from './policy-presentation'
import PolicyListControl from './PolicyListControl.vue'
import { runtimeEffectPresentation } from './runtime-effects'

const props = withDefaults(defineProps<{
  entries: ZbaPlatformPolicyEntryView[]
  effectiveEntries?: ZbaPlatformPolicyEntryView[]
  runtimeEffects?: ZbaConfigurationRuntimeEffect[]
  policyCopy?: Readonly<PlatformPolicyCopy>
  modelValue: PolicyDrafts
  locked?: boolean
}>(), { locked: false, effectiveEntries: () => [] })

const emit = defineEmits<{ 'update:modelValue': [value: PolicyDrafts] }>()

const rows = computed(() => props.entries.map(entry => ({
  entry,
  title: policyTitle(entry.target),
  // Each field.display is already the server-masked running result; value is the saved edit baseline.
  effective: props.effectiveEntries.find(item => policyKey(item.target) === policyKey(entry.target)) ?? entry,
  draft: props.modelValue[policyKey(entry.target)] ?? policyBaseline(entry),
  dirty: policyDirty(props.modelValue[policyKey(entry.target)], policyBaseline(entry)),
  overrides: overrideCount(entry, props.entries)
})))

function update(entry: ZbaPlatformPolicyEntryView, mutate: (row: PolicyRowDraft) => void): void {
  if (props.locked) return
  const key = policyKey(entry.target)
  const baseline = policyBaseline(entry)
  const current = props.modelValue[key] ?? baseline
  const row = { ...current, features: current.features.map(item => ({ ...item })) }
  mutate(row)
  const next = { ...props.modelValue }
  if (policyDirty(row, baseline)) next[key] = row
  else delete next[key]
  emit('update:modelValue', next)
}

function setActionKind(entry: ZbaPlatformPolicyEntryView, nextKind: number): void {
  update(entry, row => {
    row.actionKind = nextKind
    // 切回「使用默认规则」(0) 时必须清空已选动作名单，避免隐藏复选框后触发校验死锁。
    if (nextKind === 0) row.actions = ''
  })
}

function featureValue(row: PolicyRowDraft, feature: number): string {
  const found = row.features.find(item => item.feature === feature)
  return found === undefined ? 'inherit' : found.enabled ? 'on' : 'off'
}

function setFeature(entry: ZbaPlatformPolicyEntryView, feature: number, value: string): void {
  update(entry, row => {
    row.features = row.features.filter(item => item.feature !== feature)
    if (value === 'on' || value === 'off') row.features.push({ feature, enabled: value === 'on' })
  })
}

function setAction(entry: ZbaPlatformPolicyEntryView, action: string, enabled: boolean): void {
  update(entry, row => {
    const actions = splitActions(row.actions).filter(item => item !== action)
    if (enabled) actions.push(action)
    row.actions = actions.join(', ')
  })
}
</script>

<template>
  <div class="policy-editor" data-testid="config-policy-editor">
    <p class="config-guide">选择影响的通道或账号，配置接收范围与允许动作。使用默认规则将继续跟随上级策略；接收消息不会自动授予管理权限。</p>
    <p v-if="!entries.length" class="empty-state">还没有单独设置：所有通道均使用默认规则。</p>
    <fieldset
      v-for="row in rows"
      :key="policyKey(row.entry.target)"
      class="policy-row"
      :class="{ dirty: row.dirty }"
      :disabled="locked"
      :data-testid="'config-policy-' + policyKey(row.entry.target)"
    >
      <legend>
        {{ row.title }}
        <span v-if="row.dirty" class="dirty-tag">未保存</span>
      </legend>
      <p class="scope">{{ policyScope(row.entry) }}</p>
      <p v-if="row.overrides" class="warning">{{ row.overrides }} 个机器人已有单独设置；修改账号默认不会覆盖它们的对应规则。</p>
      <div class="policy-grid">
        <label class="policy-field">
          机器人接收消息
          <select :value="row.draft.enabled" @change="update(row.entry, draft => draft.enabled = Number(($event.target as HTMLSelectElement).value))">
            <option :value="0">使用默认规则</option>
            <option :value="1">启用</option>
            <option :value="2">停用</option>
          </select>
        </label>
        <label class="policy-field">
          自动聊天
          <select :value="row.draft.chatEnabled" @change="update(row.entry, draft => draft.chatEnabled = Number(($event.target as HTMLSelectElement).value))">
            <option :value="0">使用默认规则</option>
            <option :value="1">允许聊天</option>
            <option :value="2">关闭聊天</option>
          </select>
        </label>
        <PolicyListControl
          label="群聊接收范围"
          :list-label="props.policyCopy?.groupListLabel"
          :placeholder="props.policyCopy?.groupPlaceholder"
          :kind="row.draft.groupKind"
          :value="row.draft.groupValue"
          :stored="row.entry.groupWhitelist"
          :effective="row.effective.groupWhitelist"
          :runtime="runtimeEffectPresentation(props.runtimeEffects, row.entry.groupWhitelist, row.entry.target)"
          :testid="'policy-group-' + policyKey(row.entry.target)"
          @mode="value => update(row.entry, draft => draft.groupKind = value)"
          @text="value => update(row.entry, draft => draft.groupValue = value)"
        />
        <PolicyListControl
          label="私聊接收范围"
          :list-label="props.policyCopy?.privateListLabel"
          :placeholder="props.policyCopy?.privatePlaceholder"
          :kind="row.draft.privateKind"
          :value="row.draft.privateValue"
          :stored="row.entry.privateWhitelist"
          :effective="row.effective.privateWhitelist"
          :runtime="runtimeEffectPresentation(props.runtimeEffects, row.entry.privateWhitelist, row.entry.target)"
          :testid="'policy-private-' + policyKey(row.entry.target)"
          @mode="value => update(row.entry, draft => draft.privateKind = value)"
          @text="value => update(row.entry, draft => draft.privateValue = value)"
        />
      </div>

      <label class="policy-field">
        可执行的动作
        <select :value="row.draft.actionKind" @change="setActionKind(row.entry, Number(($event.target as HTMLSelectElement).value))">
          <option :value="0">使用默认规则</option>
          <option :value="1">单独选择动作</option>
        </select>
      </label>
      <div v-if="row.draft.actionKind === 1" class="actions">
        <label v-for="action in POLICY_ACTIONS" :key="action">
          <input
            type="checkbox"
            :checked="splitActions(row.draft.actions).includes(action)"
            @change="setAction(row.entry, action, ($event.target as HTMLInputElement).checked)"
          />
          {{ actionLabels[action] }}
        </label>
      </div>
      <p v-if="row.draft.actionKind === 1 && !splitActions(row.draft.actions).length" class="warning">
        未选择动作：此通道将不会执行这些动作。
      </p>

      <details class="policy-more">
        <summary>更多功能开关</summary>
        <div class="policy-grid">
          <label v-for="feature in POLICY_FEATURES" :key="feature.id" class="policy-field">
            {{ feature.label }}
            <select :value="featureValue(row.draft, feature.id)" @change="setFeature(row.entry, feature.id, ($event.target as HTMLSelectElement).value)">
              <option value="inherit">使用默认规则</option>
              <option value="on">启用</option>
              <option value="off">停用</option>
            </select>
          </label>
        </div>
      </details>

      <details class="policy-more">
        <summary>高级：策略标识与规则明细</summary>
        <p>策略标识：<code>{{ policyKey(row.entry.target) }}</code></p>
        <p>动作标识：{{ POLICY_ACTIONS.join(' / ') }}</p>
        <pre>{{ row.entry.target }}</pre>
      </details>
    </fieldset>
  </div>
</template>

<style scoped>
.policy-editor,
.policy-row {
  display: grid;
  gap: 1rem;
  min-width: 0;
}
.policy-row {
  padding: 1.125rem;
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  background: var(--surface);
  margin: 0;
}
.policy-row.dirty {
  border-color: var(--accent-brand);
  background: var(--accent-tint);
}
legend {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  font-weight: 650;
  font-size: var(--text-md);
  max-width: 100%;
  overflow-wrap: anywhere;
  padding: 0 6px;
}
.dirty-tag {
  padding: 2px 7px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--accent-deep);
  font-size: var(--text-xs);
  font-weight: 600;
}
.policy-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 1rem;
}
.policy-field {
  display: grid;
  gap: 0.4rem;
  min-width: 0;
  font-size: var(--text-sm);
  font-weight: 600;
  color: var(--text-muted);
}
.policy-field select {
  width: 100%;
  min-width: 0;
  font: inherit;
  font-weight: 400;
}
p {
  margin: 0;
  font-size: var(--text-sm);
  line-height: 1.6;
  overflow-wrap: anywhere;
}
.scope {
  color: var(--text-soft);
}
.warning {
  color: var(--warn-text);
  font-weight: 500;
}
.actions {
  display: flex;
  gap: 0.75rem 1.25rem;
  flex-wrap: wrap;
}
.actions label {
  display: flex;
  align-items: center;
  gap: 0.45rem;
  font-size: var(--text-sm);
}
.policy-more summary {
  cursor: pointer;
  padding: 0.35rem 0;
  font-size: var(--text-sm);
  color: var(--text-muted);
  font-weight: 500;
}
pre {
  white-space: pre-wrap;
  overflow-wrap: anywhere;
  font-size: var(--text-xs);
}
@media (max-width: 640px) {
  .policy-grid {
    grid-template-columns: minmax(0, 1fr);
  }
  .policy-row {
    padding: 0.875rem;
  }
}
</style>
