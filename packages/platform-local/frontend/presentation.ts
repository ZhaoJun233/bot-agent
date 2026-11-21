/** Package-owned presentation metadata for system.platform-local. */
export const platformLocalPresentation = Object.freeze({
  pluginKey: 'system.platform-local',
  kind: 'system' as const,
  platformId: 'local',
  policyPlatformId: 'local',
  icon: 'terminal' as const,
  title: '本地调试通道',
  summary: '本地合成回归与面板调试通道（白名单为空默认关闭）',
  credentialHint: '本地调试通道无需外部接入凭据；配置本地通道 ID 后即可启用合成调试与回归验证。',
  fields: [
    { section: 'platform' as const, key: 'localChannelIds' }
  ],
  secrets: [] as const,
  fieldLabels: Object.freeze({
    localChannelIds: '本地通道 ID'
  }),
  fieldHints: Object.freeze({
    localChannelIds: '逗号分隔的本地调试通道 ID；留空时本地调试通道默认关闭。'
  }),
  policyCopy: Object.freeze({
    groupListLabel: '允许的本地群通道 ID（逗号或空格分隔）',
    privateListLabel: '允许的本地私聊 ID（逗号或空格分隔）',
    groupPlaceholder: '例如 local-group-1',
    privatePlaceholder: '例如 local-user-1'
  })
})
