<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import type {
  ZbaConversationProfileView,
  ZbaConversationToolTier,
  ZbaDriverKey
} from '../../api/zba'
import {
  CONVERSATION_PROFILE_FEATURES,
  CONVERSATION_PROFILE_PRESETS,
  CONVERSATION_TOOL_TIERS,
  conversationKeyHint,
  conversationProfileBaseline,
  conversationProfileDirty,
  conversationProfileId,
  emptyConversationProfileDraft,
  validateConversationKeyPrefix,
  type ConversationProfileDrafts,
  type ConversationProfilePreset,
  type ConversationProfileRowDraft
} from './conversation-profile-draft'

const props = withDefaults(defineProps<{
  platformId: string
  driver: ZbaDriverKey | null
  entries: ZbaConversationProfileView[]
  modelValue: ConversationProfileDrafts
  locked?: boolean
  maxMessagesCeiling?: number | null
}>(), { locked: false, maxMessagesCeiling: null })

const emit = defineEmits<{ 'update:modelValue': [value: ConversationProfileDrafts] }>()

const newKeyInput = ref('')
const newLabelInput = ref('')
const addError = ref<string | null>(null)

watch([() => props.locked, () => props.platformId], () => {
  newKeyInput.value = ''
  newLabelInput.value = ''
  addError.value = null
})

const hint = computed(() => conversationKeyHint(props.platformId))

interface DisplayRow {
  id: string
  draft: ConversationProfileRowDraft
  displayTitle: string
  displayKey: string
  isNew: boolean
  isDeleted: boolean
  dirty: boolean
}

const rows = computed<DisplayRow[]>(() => {
  const result: DisplayRow[] = []
  const seen = new Set<string>()
  for (const item of props.entries) {
    const id = conversationProfileId(item.key)
    seen.add(id)
    const baseline = conversationProfileBaseline(item)
    const pending = props.modelValue[id]
    if (pending?.kind === 'delete') {
      result.push({
        id,
        draft: baseline,
        displayTitle: item.displayLabel || item.conversationKeyDisplay,
        displayKey: item.conversationKeyDisplay,
        isNew: false,
        isDeleted: true,
        dirty: true
      })
      continue
    }
    const draft = pending?.kind === 'upsert' ? pending.draft : baseline
    result.push({
      id,
      draft,
      displayTitle: item.displayLabel || item.conversationKeyDisplay,
      displayKey: item.conversationKeyDisplay,
      isNew: false,
      isDeleted: false,
      dirty: conversationProfileDirty(pending?.kind === 'upsert' ? pending.draft : undefined, baseline)
    })
  }
  for (const [id, entry] of Object.entries(props.modelValue)) {
    if (seen.has(id) || entry.kind !== 'upsert') continue
    if (entry.draft.key.driver.platformId !== props.platformId) continue
    result.push({
      id,
      draft: entry.draft,
      displayTitle: entry.draft.displayLabel.trim() || entry.draft.key.conversationKey,
      displayKey: entry.draft.key.conversationKey,
      isNew: true,
      isDeleted: false,
      dirty: true
    })
  }
  return result
})

function addProfile(preset?: ConversationProfilePreset): void {
  if (props.locked || !props.driver) return
  const trimmedKey = newKeyInput.value.trim()
  const err = validateConversationKeyPrefix(props.platformId, trimmedKey)
  if (err) {
    addError.value = err
    return
  }
  const key = { driver: { ...props.driver }, conversationKey: trimmedKey }
  const id = conversationProfileId(key)
  if (rows.value.some((row) => row.id === id && !row.isDeleted)) {
    addError.value = '该会话档案已存在，请在下方列表中直接编辑。'
    return
  }
  addError.value = null
  let draft = emptyConversationProfileDraft(key, newLabelInput.value.trim())
  if (preset) draft = preset.apply(draft)
  emit('update:modelValue', { ...props.modelValue, [id]: { kind: 'upsert', draft } })
  newKeyInput.value = ''
  newLabelInput.value = ''
}

function mutateRow(row: DisplayRow, mutate: (draft: ConversationProfileRowDraft) => void): void {
  if (props.locked || row.isDeleted) return
  const clone: ConversationProfileRowDraft = {
    ...row.draft,
    key: { driver: { ...row.draft.key.driver }, conversationKey: row.draft.key.conversationKey },
    features: row.draft.features.map((item) => ({ ...item }))
  }
  mutate(clone)
  const next = { ...props.modelValue }
  const existing = props.entries.find((item) => conversationProfileId(item.key) === row.id)
  if (existing && !conversationProfileDirty(clone, conversationProfileBaseline(existing))) {
    delete next[row.id]
  } else {
    next[row.id] = { kind: 'upsert', draft: clone }
  }
  emit('update:modelValue', next)
}

function applyPresetToRow(row: DisplayRow, preset: ConversationProfilePreset): void {
  mutateRow(row, (draft) => {
    Object.assign(draft, preset.apply(draft))
  })
}

function toggleDelete(row: DisplayRow): void {
  if (props.locked) return
  const next = { ...props.modelValue }
  if (row.isNew || row.isDeleted) {
    delete next[row.id]
  } else {
    next[row.id] = { kind: 'delete', key: row.draft.key }
  }
  emit('update:modelValue', next)
}

function featureState(row: ConversationProfileRowDraft, featureId: number): string {
  const found = row.features.find((item) => item.feature === featureId)
  return found === undefined ? 'inherit' : found.enabled ? 'on' : 'off'
}

function setFeature(row: DisplayRow, featureId: number, value: string): void {
  mutateRow(row, (draft) => {
    draft.features = draft.features.filter((item) => item.feature !== featureId)
    if (value === 'on' || value === 'off') draft.features.push({ feature: featureId, enabled: value === 'on' })
  })
}
</script>

<template>
  <div class="profile-editor" data-testid="conversation-profile-editor">
    <div class="profile-editor-head">
      <div>
        <strong>会话定制档案（按群 / 私聊覆写）</strong>
        <span class="hot-tag">热更新 · 免重启</span>
      </div>
      <p class="profile-guide">
        仅覆盖当前平台（<code>{{ platformId }}</code>）下指定会话的风格、节奏、能力与工具档位，不跨平台串台；平台策略显式停用的能力不可被会话档案复活。
        <template v-if="maxMessagesCeiling">
          单会话上下文条数同时受全局历史留存上限（{{ maxMessagesCeiling }} 条）约束。
        </template>
      </p>
    </div>

    <div v-if="!locked && driver" class="profile-add-bar" data-testid="conversation-profile-add">
      <div class="profile-add-inputs">
        <label class="profile-field">
          <span>会话键（{{ hint.rule }}）</span>
          <input
            v-model="newKeyInput"
            type="text"
            :placeholder="hint.placeholder"
            data-testid="conversation-profile-new-key"
          />
        </label>
        <label class="profile-field">
          <span>备注名（可选，展示经脱敏）</span>
          <input
            v-model="newLabelInput"
            type="text"
            placeholder="例如：项目协作群"
            data-testid="conversation-profile-new-label"
          />
        </label>
      </div>
      <div class="profile-add-actions">
        <button type="button" class="tonal" data-testid="conversation-profile-add-blank" @click="addProfile()">
          + 新增空白档案
        </button>
        <button
          v-for="preset in CONVERSATION_PROFILE_PRESETS"
          :key="preset.id"
          type="button"
          class="preset-btn"
          :title="preset.summary"
          :data-testid="'conversation-profile-add-preset-' + preset.id"
          @click="addProfile(preset)"
        >
          + {{ preset.label }}
        </button>
      </div>
      <p v-if="addError" class="profile-error" role="alert" data-testid="conversation-profile-add-error">{{ addError }}</p>
    </div>

    <p v-if="rows.length === 0" class="empty-state" data-testid="conversation-profile-empty">
      当前平台还没有会话定制档案；所有会话默认继承全局与平台策略。
    </p>

    <fieldset
      v-for="row in rows"
      :key="row.id"
      class="profile-card"
      :class="{ dirty: row.dirty, deleted: row.isDeleted }"
      :disabled="locked"
      :data-testid="'conversation-profile-row-' + row.draft.key.conversationKey"
    >
      <legend>
        <span>{{ row.displayTitle }}</span>
        <code class="key-pill">{{ row.displayKey }}</code>
        <span v-if="row.isNew" class="badge-tag">新增</span>
        <span v-else-if="row.isDeleted" class="badge-tag badge-danger">待删除</span>
        <span v-else-if="row.dirty" class="badge-tag">未保存</span>
      </legend>

      <div class="profile-row-bar">
        <div v-if="!row.isDeleted && !locked" class="preset-row">
          <span class="preset-label">一键预填：</span>
          <button
            v-for="preset in CONVERSATION_PROFILE_PRESETS"
            :key="preset.id"
            type="button"
            class="preset-btn"
            :title="preset.summary"
            :data-testid="'conversation-profile-preset-' + preset.id + '-' + row.draft.key.conversationKey"
            @click="applyPresetToRow(row, preset)"
          >
            {{ preset.label }}
          </button>
        </div>
        <button
          type="button"
          class="danger-link"
          :data-testid="'conversation-profile-delete-' + row.draft.key.conversationKey"
          @click="toggleDelete(row)"
        >
          {{ row.isNew ? '移除草稿' : row.isDeleted ? '撤销删除' : '删除档案' }}
        </button>
      </div>

      <template v-if="!row.isDeleted">
        <div class="profile-grid">
          <label class="profile-field">
            <span>备注名（真名编辑）</span>
            <input
              type="text"
              :value="props.locked ? row.displayTitle : row.draft.displayLabel"
              :placeholder="row.displayTitle"
              :disabled="props.locked"
              :data-testid="'conversation-profile-label-' + row.draft.key.conversationKey"
              @input="mutateRow(row, d => d.displayLabel = ($event.target as HTMLInputElement).value)"
            />
          </label>
          <label class="profile-field">
            <span>工具权限档位</span>
            <select
              :value="row.draft.toolTier"
              :data-testid="'conversation-profile-tier-' + row.draft.key.conversationKey"
              @change="mutateRow(row, d => d.toolTier = Number(($event.target as HTMLSelectElement).value) as ZbaConversationToolTier)"
            >
              <option v-for="tier in CONVERSATION_TOOL_TIERS" :key="tier.value" :value="tier.value">
                {{ tier.label }}
              </option>
            </select>
          </label>
        </div>

        <details class="profile-more" open>
          <summary>表达风格与人设（留空 = 继承全局）</summary>
          <div class="profile-grid">
            <label class="profile-field profile-span-2">
              <span>补充人设（追加到全局人设末尾，≤ 600 字）</span>
              <textarea
                rows="2"
                :value="row.draft.personaAppend"
                placeholder="例如：简明直接、结论先行"
                :data-testid="'conversation-profile-persona-append-' + row.draft.key.conversationKey"
                @input="mutateRow(row, d => d.personaAppend = ($event.target as HTMLTextAreaElement).value)"
              />
            </label>
            <label class="profile-field profile-span-2">
              <span>覆写人设（替换全局人设，与补充人设合计 ≤ 1200 字）</span>
              <textarea
                rows="2"
                :value="row.draft.personaOverride"
                placeholder="留空则沿用全局人设"
                @input="mutateRow(row, d => d.personaOverride = ($event.target as HTMLTextAreaElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>动作旁白过滤</span>
              <select
                :value="row.draft.filterActionNarration"
                @change="mutateRow(row, d => d.filterActionNarration = ($event.target as HTMLSelectElement).value as 'inherit' | 'on' | 'off')"
              >
                <option value="inherit">继承全局</option>
                <option value="on">开启过滤</option>
                <option value="off">关闭过滤</option>
              </select>
            </label>
            <label class="profile-field">
              <span>语音字数上限（10..300）</span>
              <input
                type="number"
                min="10"
                max="300"
                :value="row.draft.voiceMaxChars"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.voiceMaxChars = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>语音积极性（0..100）</span>
              <input
                type="number"
                min="0"
                max="100"
                :value="row.draft.voiceEagerness"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.voiceEagerness = ($event.target as HTMLInputElement).value)"
              />
            </label>
          </div>
        </details>

        <details class="profile-more">
          <summary>回复节奏、上下文与主动发言（留空 = 继承全局）</summary>
          <div class="profile-grid">
            <label class="profile-field">
              <span>群插话概率（0..100%）</span>
              <input
                type="number"
                min="0"
                max="100"
                :value="row.draft.groupInterjectionProbability"
                placeholder="继承全局"
                :data-testid="'conversation-profile-prob-' + row.draft.key.conversationKey"
                @input="mutateRow(row, d => d.groupInterjectionProbability = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>群插话冷却（0..3600 秒）</span>
              <input
                type="number"
                min="0"
                max="3600"
                :value="row.draft.groupInterjectionCooldownSeconds"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.groupInterjectionCooldownSeconds = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>群续话等待（0..30 秒）</span>
              <input
                type="number"
                min="0"
                max="30"
                :value="row.draft.groupContinuationWaitSeconds"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.groupContinuationWaitSeconds = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>私聊续话等待（0..30 秒）</span>
              <input
                type="number"
                min="0"
                max="30"
                step="0.5"
                :value="row.draft.privateContinuationWaitSeconds"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.privateContinuationWaitSeconds = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>发言适宜度阈值（0..100）</span>
              <input
                type="number"
                min="0"
                max="100"
                :value="row.draft.suitabilityThreshold"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.suitabilityThreshold = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>上下文条数（4..60）</span>
              <input
                type="number"
                min="4"
                max="60"
                :value="row.draft.maxContextMessages"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.maxContextMessages = ($event.target as HTMLInputElement).value)"
              />
            </label>
            <label class="profile-field">
              <span>冷场主动发言</span>
              <select
                :value="row.draft.proactiveEnabled"
                @change="mutateRow(row, d => d.proactiveEnabled = Number(($event.target as HTMLSelectElement).value))"
              >
                <option :value="0">继承全局</option>
                <option :value="1">启用</option>
                <option :value="2">禁用</option>
              </select>
            </label>
            <label class="profile-field">
              <span>主动发言安静时长（30..3600 秒）</span>
              <input
                type="number"
                min="30"
                max="3600"
                :value="row.draft.proactiveQuietSeconds"
                placeholder="继承全局"
                @input="mutateRow(row, d => d.proactiveQuietSeconds = ($event.target as HTMLInputElement).value)"
              />
            </label>
          </div>
        </details>

        <details class="profile-more">
          <summary>能力功能覆盖（仅列当前阶段已落地能力）</summary>
          <div class="profile-grid">
            <label v-for="feature in CONVERSATION_PROFILE_FEATURES" :key="feature.id" class="profile-field">
              <span>{{ feature.label }}</span>
              <select
                :value="featureState(row.draft, feature.id)"
                :data-testid="'conversation-profile-feature-' + feature.id + '-' + row.draft.key.conversationKey"
                @change="setFeature(row, feature.id, ($event.target as HTMLSelectElement).value)"
              >
                <option value="inherit">继承全局</option>
                <option value="on">本会话启用</option>
                <option value="off">本会话关闭</option>
              </select>
            </label>
          </div>
        </details>
      </template>
    </fieldset>
  </div>
</template>

<style scoped>
.profile-editor {
  display: grid;
  gap: 1rem;
  min-width: 0;
}
.profile-editor-head {
  display: grid;
  gap: 0.35rem;
}
.profile-editor-head > div {
  display: flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
}
.hot-tag,
.badge-tag {
  padding: 2px 8px;
  border-radius: 999px;
  background: var(--accent-soft);
  color: var(--accent-deep);
  font-size: var(--text-xs);
  font-weight: 600;
}
.badge-danger {
  background: var(--bad-bg);
  color: var(--bad-text);
}
.profile-guide {
  margin: 0;
  font-size: var(--text-sm);
  color: var(--text-muted);
  line-height: 1.55;
}
.profile-add-bar {
  display: grid;
  gap: 0.75rem;
  padding: 0.9rem 1rem;
  border: 1px dashed var(--border);
  border-radius: var(--radius-lg);
  background: var(--surface-subtle, var(--surface));
}
.profile-add-inputs,
.profile-grid {
  display: grid;
  grid-template-columns: repeat(2, minmax(0, 1fr));
  gap: 0.75rem;
}
.profile-span-2 {
  grid-column: 1 / -1;
}
.profile-add-actions,
.preset-row {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}
.profile-row-bar {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}
.preset-label {
  font-size: var(--text-xs);
  color: var(--text-soft);
}
.preset-btn {
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--surface);
  color: var(--text);
  font-size: var(--text-xs);
  cursor: pointer;
}
.preset-btn:hover:not(:disabled) {
  border-color: var(--accent-brand);
  color: var(--accent-deep);
}
.danger-link {
  border: none;
  background: transparent;
  color: var(--bad-text);
  font-size: var(--text-xs);
  cursor: pointer;
  padding: 4px 6px;
}
.profile-error {
  margin: 0;
  font-size: var(--text-xs);
  color: var(--bad-text);
}
.profile-card {
  display: grid;
  gap: 0.85rem;
  padding: 1rem;
  border: 1px solid var(--border);
  border-radius: var(--radius-lg);
  background: var(--surface);
  margin: 0;
  min-width: 0;
}
.profile-card.dirty {
  border-color: var(--accent-brand);
  background: var(--accent-tint);
}
.profile-card.deleted {
  opacity: 0.75;
  border-style: dashed;
}
legend {
  display: inline-flex;
  align-items: center;
  flex-wrap: wrap;
  gap: 8px;
  font-weight: 650;
  font-size: var(--text-sm);
  padding: 0 6px;
}
.key-pill {
  font-size: var(--text-xs);
  padding: 1px 6px;
  border-radius: 6px;
  background: var(--surface-subtle, rgba(128, 128, 128, 0.12));
}
.profile-field {
  display: grid;
  gap: 0.35rem;
  min-width: 0;
  font-size: var(--text-xs);
  font-weight: 600;
  color: var(--text-muted);
}
.profile-field input,
.profile-field select,
.profile-field textarea {
  width: 100%;
  min-width: 0;
  font: inherit;
  font-weight: 400;
}
.profile-more {
  border-top: 1px solid var(--border);
  padding-top: 0.65rem;
}
.profile-more summary {
  cursor: pointer;
  font-size: var(--text-xs);
  font-weight: 600;
  color: var(--text-muted);
  margin-bottom: 0.6rem;
}
@media (max-width: 720px) {
  .profile-add-inputs,
  .profile-grid {
    grid-template-columns: 1fr;
  }
}
</style>
