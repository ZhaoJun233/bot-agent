/** Package-owned presentation metadata for system.platform-qq-official. */
export const platformQqOfficialPresentation = Object.freeze({
  pluginKey: 'system.platform-qq-official',
  kind: 'system' as const,
  platformId: 'qq.official',
  policyPlatformId: 'qq.official',
  icon: 'qq' as const,
  supportsMessagingInspection: true,
  title: 'QQ 官方开放平台',
  summary: '官方 AppID、沙箱环境、群/私聊白名单与 OpenID 隔离通道策略',
  credentialHint: 'QQ 官方 AppSecret 仅从部署环境变量（BOTAGENT_OFFICIAL_APP_SECRET / QQCHAT_OFFICIAL_APP_SECRET）读取，Web 面板不接收也不回显该密钥。',
  fields: [
    { section: 'platform' as const, key: 'officialEnabled' },
    { section: 'platform' as const, key: 'officialAppId' },
    { section: 'platform' as const, key: 'officialChatEnabled' },
    { section: 'platform' as const, key: 'officialSandbox' },
    { section: 'platform' as const, key: 'officialWhitelistGroups' },
    { section: 'platform' as const, key: 'officialWhitelistPrivates' }
  ],
  secrets: [] as const,
  fieldLabels: Object.freeze({
    officialEnabled: '启用官方通道',
    officialAppId: '官方 AppID',
    officialChatEnabled: '官方对话开关',
    officialSandbox: '官方沙箱环境',
    officialWhitelistGroups: '官方群白名单',
    officialWhitelistPrivates: '官方私聊白名单'
  }),
  fieldHints: Object.freeze({
    officialWhitelistGroups: '留空或 * 接收所有官方群消息；填写群 OpenID 则只接收名单内群。',
    officialWhitelistPrivates: '留空或 * 接收所有官方私聊消息；填写用户 OpenID 则只接收名单内私聊。'
  }),
  policyCopy: Object.freeze({
    groupListLabel: '允许的群 OpenID（逗号或空格分隔）',
    privateListLabel: '允许的用户 OpenID（逗号或空格分隔）',
    groupPlaceholder: '例如 group_openid_1',
    privatePlaceholder: '例如 user_openid_1'
  })
})
