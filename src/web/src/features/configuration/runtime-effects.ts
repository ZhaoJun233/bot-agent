import type { ZbaConfigurationRuntimeEffect, ZbaPolicyTarget, ZbaReadField } from '../../api/zba'
import { policyKey } from './policy-draft'

export interface RuntimeEffectPresentation {
  state: ZbaConfigurationRuntimeEffect['state']
  label: string
  tone: string
}

export function runtimeEffectPresentation(
  effects: ZbaConfigurationRuntimeEffect[] | undefined,
  field: ZbaReadField,
  target?: ZbaPolicyTarget
): RuntimeEffectPresentation {
  const effect = effects?.find(item => item.field.field === field.rule.id && (target
    ? item.field.object?.kind === 'policy' && policyKey(item.field.object.target) === policyKey(target)
    : item.field.object == null))
  const state = effect?.state ?? 'unknown'
  switch (state) {
    case 'current': return { state, label: '当前已生效', tone: 'ok' }
    case 'restartRequired': return { state, label: effect?.restartTarget === 'botAgent' ? '已保存，需重启机器人服务' : '已保存，等待重启；对象待确认', tone: 'warn' }
    case 'applyFailed': return { state, label: '已保存，运行应用失败；当前状态待核对', tone: 'bad' }
    default: return { state: 'unknown', label: '当前生效情况待确认', tone: 'warn' }
  }
}
