import type { MessagingCondition, MessagingConditionState } from '../../api/messaging-capabilities'

const names: Record<string, string> = {
  text: '文本回复', image: '图片回复', voice: '语音回复', quote: '引用关系',
  send: '工具发送当前会话', recall: '撤回机器人自己的消息',
  role: '角色查询', bot_state: '机器人状态查询', ban: '禁言与解除', kick: '移出成员',
  markdown: 'Markdown 消息', keyboard: '交互按钮', menu: '自定义菜单'
}
const states: Record<MessagingConditionState, string> = { 0: '条件满足', 1: '策略限制', 2: '待核实', 3: '暂不可用' }
const reasons: Record<string, string> = {
  platform_policy_satisfied: '当前平台策略检查通过。实际执行仍会检查插件、会话、发送窗口、额度和媒体凭据。',
  platform_policy_unavailable: '暂时无法读取平台策略，不能判断此项条件。',
  application_grant_unverified: '尚未取得此应用的官方权限证据。凭据已配置或连接成功都不能证明获得权限。',
  conversation_not_selected: '未选择具体会话；回复窗口、额度、群限制和引用关系尚未核验。',
  actor_not_selected: '未选择具体操作者，成员身份与当前操作资格尚未核验。',
  own_message_proof_required: '只支持撤回当前会话中两分钟内由机器人发送且有持久化证明的消息；需选定消息后核验。',
  implementation_deferred: '此项尚未交付，等待平台权限确认及对应实现与验收。',
  platform_unregistered: '此驱动尚未加载，暂时无法执行消息动作。',
  platform_disconnected: '此驱动当前未连接，恢复后可重新读取。',
  ambiguous_platform_instance: '同一驱动身份对应多个实例，需先排除冲突。',
  platform_chat_disabled: '平台接入或聊天开关当前未启用。',
  platform_capability_unsupported: '当前适配器不支持此能力。',
  feature_disabled: '此项被平台功能开关关闭。',
  action_not_allowed: '此工具动作未包含在当前平台允许的动作列表中。'
}
export const messagingName = (id: string): string => names[id] ?? '未识别能力'
export const conditionLabel = (condition: MessagingCondition): string => states[condition.state]
export const conditionReason = (condition: MessagingCondition): string => reasons[condition.reasonCode] ?? '服务端返回了新的状态原因，请核对当前版本。'
