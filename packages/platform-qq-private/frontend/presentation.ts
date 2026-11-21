/** Package-owned presentation metadata for system.platform-qq-private. */
export const platformQqPrivatePresentation = Object.freeze({
  pluginKey: 'system.platform-qq-private',
  kind: 'system' as const,
  platformId: 'qq.private',
  policyPlatformId: 'qq.private',
  icon: 'qq' as const,
  title: 'QQ 私域通道',
  summary: 'OneBot / NapCat 私域连接、私聊开关、群/私聊白名单与访问策略',
  credentialHint: 'OneBot / NapCat 连接地址与访问令牌由部署环境变量提供；此处管理私域对话开关、白名单与实例策略。',
  fields: [
    { section: 'platform' as const, key: 'privateChatEnabled' },
    { section: 'platform' as const, key: 'whitelistGroups' },
    { section: 'platform' as const, key: 'whitelistPrivates' },
    { section: 'platform' as const, key: 'messageWhitelist' }
  ],
  secrets: [] as const,
  fieldLabels: Object.freeze({
    privateChatEnabled: '私聊总开关',
    whitelistGroups: '群聊白名单',
    whitelistPrivates: '私聊白名单',
    messageWhitelist: '共用白名单（兼容）'
  }),
  fieldHints: Object.freeze({
    whitelistGroups: '留空或 * 接收所有 QQ 群消息；填写群号则只接收名单内群。私聊名单独立生效。',
    whitelistPrivates: '留空或 * 接收所有 QQ 私聊，不再继承共用名单；仍遵守平台聊天开关与安全策略。',
    messageWhitelist: '兼容旧配置的批量设置入口。保存会同时设置 QQ 私域群聊和私聊名单；之后两份名单各自生效。'
  }),
  policyCopy: Object.freeze({
    groupListLabel: '允许的 QQ 群号（逗号或空格分隔）',
    privateListLabel: '允许的 QQ 号（逗号或空格分隔）',
    groupPlaceholder: '例如 10001, 10002',
    privatePlaceholder: '例如 20001, 20002'
  })
})
