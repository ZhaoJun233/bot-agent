import type { ZbaPlatformPolicyEntryView, ZbaReadField } from '../../api/zba'
import { policyBaseline, policyKey, policyTitle, type PolicyDrafts } from './policy-draft'

export const actionLabels: Record<string, string> = {
  like: '点赞', poke: '戳一戳', emoji_like: '表情回应', recall: '撤回消息', ban: '禁言',
  kick: '移出群聊', card: '修改群名片', group_name: '修改群名', leave: '退出群聊', send: '发送消息'
}
export function policySource(field: ZbaReadField<unknown>): string {
  return ['内置默认', '用户配置', '环境变量', '旧配置兼容', '来源待确认'][field.effectiveSource] ?? '来源待确认'
}
export function policyStored(field: ZbaReadField<unknown>): string {
  return field.userMode === 0 ? '尚未设置，使用默认规则' : field.userMode === 1 ? '已选择使用默认规则' : '已单独设置'
}
/** Only format the server-masked display; never use editable value in a preview. */
export function policyDisplay(field: ZbaReadField<unknown> | undefined): string {
  if (!field) return '暂未取得当前生效结果'
  return field.display.replaceAll('[allow-all]', '允许所有人').replaceAll('[explicit-empty]', '拒绝所有人（名单为空）')
    .replaceAll('[inherit]', '使用默认规则').replaceAll('[explicit]', '只允许名单中的人') || '空名单'
}
export function policyScope(entry: ZbaPlatformPolicyEntryView): string {
  return entry.target.kind === 'accountDefault'
    ? '影响此账号下使用默认规则的机器人及其群聊、私聊；单独设置的机器人不受对应默认项影响。'
    : '仅影响此机器人接收的群聊与私聊。'
}
export function overrideCount(entry: ZbaPlatformPolicyEntryView, entries: ZbaPlatformPolicyEntryView[]): number {
  if (entry.target.kind !== 'accountDefault') return 0
  const target = entry.target
  return entries.filter(other => other.target.kind === 'instance'
    && other.target.key.platformId === target.platformId && other.target.key.accountScope === target.accountScope
    && [other.enabled, other.chatEnabled, other.groupWhitelist, other.privateWhitelist, other.allowedActions, other.features]
      .some(field => field.userMode === 2)).length
}
export function draftListSummary(kind: number, value: string): string {
  if (kind === 0) return '使用默认规则；实际名单以保存后读取结果为准'
  if (kind === 2) return '允许所有人'
  if (kind === 3 || !value.trim()) return '拒绝所有人（明确空名单）'
  return '只允许名单中的人'
}
export function policyDenials(drafts: PolicyDrafts, entries: ZbaPlatformPolicyEntryView[]): string[] {
  const warnings: string[] = []
  for (const entry of entries) {
    const row = drafts[policyKey(entry.target)]
    if (!row) continue
    const before = policyBaseline(entry)
    for (const channel of ['group', 'private'] as const) {
      const kind = row[channel === 'group' ? 'groupKind' : 'privateKind']
      const value = row[channel === 'group' ? 'groupValue' : 'privateValue']
      const previousKind = before[channel === 'group' ? 'groupKind' : 'privateKind']
      const previousValue = before[channel === 'group' ? 'groupValue' : 'privateValue']
      if ((kind === 3 || kind === 1 && !value.trim()) && (kind !== previousKind || value !== previousValue)) {
        warnings.push(policyTitle(entry.target) + '：' + (channel === 'group' ? '群聊' : '私聊') + '将拒绝所有人')
      }
    }
  }
  return warnings
}
