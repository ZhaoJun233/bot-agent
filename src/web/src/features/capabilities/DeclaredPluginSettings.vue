<script setup lang="ts">
import type { PluginSettingDefinition } from './plugin-manifest'
import type { PluginSettingDraft, PluginSettingsDraft } from './declared-plugin-settings'
const props = defineProps<{
  definitions: PluginSettingDefinition[]
  draft: PluginSettingsDraft
  configuredSecrets?: Record<string, boolean>
  disabled?: boolean
}>()
const emit = defineEmits<{ change: [key: string, value: PluginSettingDraft] }>()
function input(field: PluginSettingDefinition, event: Event): void {
  const target = event.target as HTMLInputElement | HTMLSelectElement
  const value = field.type === 'boolean' ? String((target as HTMLInputElement).checked) : target.value
  emit('change', field.key, { kind: 'set', value })
}
function mode(field: PluginSettingDefinition, event: Event): void {
  const value = (event.target as HTMLSelectElement).value
  if (value !== 'keep' && value !== 'set' && value !== 'delete') return
  emit('change', field.key, { kind: value, value: value === 'set' ? props.draft[field.key]?.value ?? '' : '' })
}
</script>
<template>
  <fieldset class="declared-fields" :disabled="props.disabled" data-testid="marketplace-declared-settings">
    <legend>插件声明的设置</legend>
    <p class="field-help">仅渲染声明式字段，不加载插件提供的 UI 脚本。密钥永不回显。</p>
    <div v-for="field in props.definitions" :key="field.key" class="declared-field">
      <template v-if="field.type === 'secret'">
        <label :for="'plugin-secret-mode-' + field.key">{{ field.label }}{{ field.required ? ' *' : '' }}</label>
        <span class="field-help">{{ props.configuredSecrets?.[field.key] ? '已配置' : '未配置' }}（不显示内容）</span>
        <select :id="'plugin-secret-mode-' + field.key" :value="props.draft[field.key]?.kind ?? 'keep'" :data-testid="'plugin-setting-' + field.key + '-mode'" @change="mode(field, $event)">
          <option value="keep">保留现有密钥</option><option value="set">替换密钥</option><option value="delete" :disabled="field.required">删除密钥</option>
        </select>
        <label v-if="props.draft[field.key]?.kind === 'set'" :for="'plugin-setting-' + field.key">新密钥</label>
        <input v-if="props.draft[field.key]?.kind === 'set'" :id="'plugin-setting-' + field.key" type="password" autocomplete="new-password" spellcheck="false" :data-testid="'plugin-setting-' + field.key" :value="props.draft[field.key]?.value ?? ''" @input="input(field, $event)">
      </template>
      <label v-else-if="field.type === 'boolean'" class="boolean-setting">
        <input type="checkbox" :data-testid="'plugin-setting-' + field.key" :checked="props.draft[field.key]?.value === 'true'" @change="input(field, $event)">{{ field.label }}{{ field.required ? ' *' : '' }}
      </label>
      <template v-else>
        <label :for="'plugin-setting-' + field.key">{{ field.label }}{{ field.required ? ' *' : '' }}</label>
        <select v-if="field.type === 'select'" :id="'plugin-setting-' + field.key" :data-testid="'plugin-setting-' + field.key" :value="props.draft[field.key]?.value ?? ''" @change="input(field, $event)">
          <option value="" disabled>请选择</option><option v-for="choice in field.choices ?? []" :key="choice" :value="choice">{{ choice }}</option>
        </select>
        <input v-else :id="'plugin-setting-' + field.key" :type="field.type === 'number' ? 'number' : 'text'" :step="field.type === 'number' ? 'any' : undefined" :data-testid="'plugin-setting-' + field.key" :value="props.draft[field.key]?.value ?? ''" autocomplete="off" @input="input(field, $event)">
      </template>
    </div>
    <p v-if="!props.definitions.length" class="field-help">此插件没有声明可编辑设置。</p>
  </fieldset>
</template>
<style scoped>
.declared-fields { border: 1px solid var(--border); border-radius: 8px; padding: 18px; margin: 12px 0; display: grid; gap: 20px; min-width: 0; background: var(--surface); }
.declared-fields legend { padding: 0 8px; font-weight: 600; font-size: 13px; color: var(--text); }
.declared-field { display: grid; gap: 7px; min-width: 0; }
.declared-field + .declared-field { border-top: 1px solid var(--border); padding-top: 16px; }
.declared-field label { font-size: 12px; font-weight: 600; color: var(--text-muted); overflow-wrap: anywhere; }
.declared-field input:not([type=checkbox]), .declared-field select { min-width: 0; width: 100%; min-height: 36px; border-radius: 8px; }
.field-help { color: var(--text-soft); font-size: 12px; margin: 0; line-height: 1.65; }
.boolean-setting { display: flex; gap: 10px; align-items: center; min-height: 36px; }.boolean-setting input { accent-color: var(--accent-deep); }
@media (max-width: 767px) { .declared-fields { padding: 14px; }.declared-field input:not([type=checkbox]), .declared-field select, .boolean-setting { min-height: 44px; } }
</style>
