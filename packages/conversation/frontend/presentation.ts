/** Package-owned presentation metadata for system.conversation. */
export const conversationPresentation = Object.freeze({
  pluginKey: 'conversation',
  kind: 'system' as const,
  title: '对话功能',
  summary: '主动开口、分句、消息过滤与审批',
  fallbackSections: ['behavior' as const, 'music' as const, 'voice' as const]
})
