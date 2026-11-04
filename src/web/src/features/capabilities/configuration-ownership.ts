import type { ZbaPatchSection, ZbaSecretSlot } from '../../api/zba'
import { ownerOf, pluginSettings } from './plugin-settings'

/** Storage partitions are wire contracts. Each field has one UI owner, independent of those partitions. */
export interface CapabilityConfiguration {
  id: string; title: string; summary: string; sections: ZbaPatchSection[]
  pluginKey?: string; secrets: ZbaSecretSlot[]; providers?: boolean
}
export const basicFields = new Set([
  'aiModeEnabled', 'botPersona', 'groupCooldownSeconds', 'privateCooldownSeconds',
  'maxConcurrentReplies', 'maxContextMessages', 'maxMessagesPerConversation'
])
export function configurationCapabilities(): CapabilityConfiguration[] {
  // Resolve against the current registry, including extensions registered after module loading.
  return [
    ...pluginSettings.filter(spec => spec.pluginKey !== 'preset.feature.token-quota').map(spec => ({
      id: spec.pluginKey, pluginKey: spec.pluginKey, title: spec.title, summary: spec.summary,
      sections: [...new Set([...(spec.sections ?? []), ...(spec.fields ?? []).map(field => field.section), ...(spec.enableField ? [spec.enableField.section] : [])])],
      secrets: spec.pluginKey === 'preset.feature.voice' ? [2 as ZbaSecretSlot] : []
    })),
    { id: 'conversation', title: '对话与参与策略', summary: '主动回复、参与节奏、分句、消息过滤与审批', sections: ['behavior'], secrets: [] },
    { id: 'models', title: '模型服务', summary: '主模型连接、模型选择、采样参数与 API 凭据', sections: ['model'], secrets: [0], providers: true },
    { id: 'agent', title: 'Agent 与工具', summary: '任务执行、服务器连接、工具范围与 Agent 密钥', sections: ['agent'], secrets: [1] },
    { id: 'platform', title: '平台接入与访问策略', summary: '私域、官方与飞书接入、白名单及飞书凭据', sections: ['platform'], secrets: [3, 4, 5] },
    { id: 'reporting', title: '健康报告', summary: '报告开关、发送时间与接收范围', sections: ['reporting'], secrets: [] }
  ]
}
export function configurationFor(id: string): CapabilityConfiguration | undefined {
  return configurationCapabilities().find(item => item.id === id)
}
export function fieldOwner(section: string, key: string): string {
  const plugin = ownerOf(section, key)
  if (plugin) return plugin.pluginKey
  if (section === 'behavior') return basicFields.has(key) ? 'basic' : 'conversation'
  return ({ model: 'models', agent: 'agent', platform: 'platform', reporting: 'reporting' } as Record<string, string>)[section] ?? section
}
export function ownsConfiguration(scope: string, section: string, key: string): boolean {
  return fieldOwner(section, key) === scope
}
