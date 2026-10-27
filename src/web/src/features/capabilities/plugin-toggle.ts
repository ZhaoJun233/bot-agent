import {
  ZbaClient,
  ZbaNetworkError,
  ZbaRequestError,
  type ZbaConfigurationView,
  type ZbaReadField
} from '../../api/zba'
import { toUiError, type UiError } from '../../shared/ui/ui-error'
import { buildPatch, createOperationKey, fieldPath, isEditable, newNonce, nowSeconds } from '../configuration/draft'
import { pluginSettings, type PluginSettingsSpec } from './plugin-settings'

/**
 * 插件卡片上的「启用」开关。
 *
 * 写路径与配置页、设置弹窗同一条：先 read 拿版本，再带 CAS 版本 + 操作键 save。
 * 开关是幂等的布尔写（目标值固定），所以结果未知时可以用同一个操作键核对，
 * 核对不出结论就交给用户刷新——不自动重发。
 */
export interface PluginSwitchState {
  /** null = 快照里没有这个字段或不回显值。 */
  enabled: boolean | null
  /** 当前身份能不能改（字段可编辑）。 */
  editable: boolean
}

export type PluginToggleResult =
  | { kind: 'saved'; enabled: boolean }
  | { kind: 'noop'; enabled: boolean }
  | { kind: 'conflict' }
  | { kind: 'unconfirmed'; key: string }
  | { kind: 'auth'; error: UiError }
  | { kind: 'error'; error: UiError }

function enableFieldOf(spec: PluginSettingsSpec, view: ZbaConfigurationView): ZbaReadField | undefined {
  const ref = spec.enableField
  if (!ref) return undefined
  return (view[ref.section] as Record<string, ZbaReadField> | undefined)?.[ref.key]
}

export function switchState(spec: PluginSettingsSpec, view: ZbaConfigurationView): PluginSwitchState {
  const field = enableFieldOf(spec, view)
  if (!field || field.visibility !== 1 || typeof field.value !== 'boolean') return { enabled: null, editable: false }
  return { enabled: field.value, editable: isEditable(field) && field.rule?.editable !== false }
}

/** 一次读出所有登记插件的开关状态，按插件 ID 索引。 */
export function readSwitches(view: ZbaConfigurationView): Record<string, PluginSwitchState> {
  const result: Record<string, PluginSwitchState> = {}
  for (const spec of pluginSettings) if (spec.enableField) result[spec.pluginKey] = switchState(spec, view)
  return result
}

export async function setPluginEnabled(
  client: ZbaClient,
  spec: PluginSettingsSpec,
  enabled: boolean
): Promise<PluginToggleResult> {
  const ref = spec.enableField
  if (!ref) return { kind: 'error', error: { code: 'no_enable_field', message: '这个插件没有启用开关。' } }
  let key: string | null = null
  try {
    const snapshot = await client.readConfiguration(1)
    const view = snapshot.view
    if (!view) return { kind: 'error', error: { code: 'view_unavailable', message: '读不到当前设置，稍后再试。' } }
    const state = switchState(spec, view)
    if (state.enabled === null || !state.editable) {
      return { kind: 'error', error: { code: 'not_editable', message: '这个开关当前不能修改。' } }
    }
    if (state.enabled === enabled) return { kind: 'noop', enabled }
    const built = buildPatch({ [fieldPath(ref.section, ref.key)]: { kind: 'set', raw: enabled ? 'true' : 'false' } }, view)
    if (built.errors.length > 0) {
      return { kind: 'error', error: { code: 'patch_invalid', message: built.errors.join('；') } }
    }
    key = createOperationKey(snapshot.generation, nowSeconds(), newNonce())
    const result = await client.saveConfiguration({
      contractRevision: snapshot.contractRevision,
      expectedVersion: snapshot.version,
      key,
      changes: built.patch,
      secrets: []
    })
    if (result.code === 12 || ((result.code === 0 || result.code === 13) && !result.receipt)) {
      return { kind: 'unconfirmed', key }
    }
    if (result.code === 0 || result.code === 13) return { kind: 'saved', enabled }
    if (result.code === 3) return { kind: 'conflict' }
    return { kind: 'error', error: { code: 'save_' + result.code, message: '保存没有成功（code ' + result.code + '），开关没有变。' } }
  } catch (cause) {
    if (cause instanceof ZbaRequestError && cause.status === 401) return { kind: 'auth', error: toUiError(cause) }
    const unknown = key !== null && (cause instanceof ZbaNetworkError
      || (cause instanceof ZbaRequestError && (cause.status >= 500
        || (cause.code === 'invalid_json' && cause.status >= 200 && cause.status < 300))))
    if (unknown && key !== null) return { kind: 'unconfirmed', key }
    return { kind: 'error', error: toUiError(cause) }
  }
}

/** 结果未知时用同一个操作键核对：true 已生效 / false 确认没生效 / null 仍无结论。 */
export async function confirmToggle(client: ZbaClient, key: string): Promise<boolean | null> {
  try {
    const result = await client.findConfiguration(key)
    if (result.code === 0 && result.receipt) return true
    if (result.code === 14 || result.code === 6 || result.code === 5) return false
    return null
  } catch {
    return null
  }
}
