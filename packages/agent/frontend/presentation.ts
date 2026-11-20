/** Package-owned presentation metadata for system.agent. */
export const agentPresentation = Object.freeze({
  pluginKey: 'agent',
  kind: 'system' as const,
  title: 'Agent 与工具',
  summary: '任务执行、服务器连接、工具范围与 Agent 密钥',
  fallbackSections: ['agent' as const],
  secrets: [1 as const]
})
