import type { PluginSettingDefinition } from './plugin-manifest'

export interface PluginSettingDraft { kind: 'keep' | 'set' | 'delete'; value: string }
export type PluginSettingsDraft = Record<string, PluginSettingDraft>

/** Always construct declared keys only; secret plaintext never comes from a read projection. */
export function createPluginSettingsDraft(definitions: PluginSettingDefinition[], values: Record<string, string> = {}): PluginSettingsDraft {
  return Object.fromEntries(definitions.map((field) => [field.key, {
    kind: 'keep', value: field.type === 'secret' ? '' : values[field.key] ?? field.defaultValue ?? (field.type === 'boolean' ? 'false' : '')
  }]))
}
export function pluginSettingsErrors(definitions: PluginSettingDefinition[], draft: PluginSettingsDraft): string[] {
  const errors: string[] = []
  for (const field of definitions) {
    const edit = draft[field.key]
    if (!edit || edit.kind === 'keep') continue
    if (edit.kind === 'delete') { if (field.required) errors.push(field.label + ' 为必填项，不能清除。'); continue }
    if (field.required && !edit.value.trim()) errors.push(field.label + ' 不能为空。')
    if (field.type === 'number' && (!edit.value.trim() || !Number.isFinite(Number(edit.value)))) errors.push(field.label + ' 必须是有限数值。')
    if (field.type === 'boolean' && edit.value !== 'true' && edit.value !== 'false') errors.push(field.label + ' 必须是开或关。')
    if (field.type === 'select' && !(field.choices ?? []).includes(edit.value)) errors.push(field.label + ' 必须从声明的选项中选择。')
    if (field.type === 'secret' && !edit.value) errors.push(field.label + ' 为空；保留已有密钥请选择保留。')
  }
  return errors
}
export function clearPluginSecretDrafts(definitions: PluginSettingDefinition[], draft: PluginSettingsDraft): void {
  for (const field of definitions) if (field.type === 'secret') draft[field.key] = { kind: 'keep', value: '' }
}
