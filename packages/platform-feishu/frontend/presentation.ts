/** Package-owned presentation metadata for system.platform-feishu. */
export const platformFeishuPresentation = Object.freeze({
  pluginKey: 'system.platform-feishu',
  kind: 'system' as const,
  platformId: 'feishu',
  policyPlatformId: 'feishu',
  icon: 'feishu' as const,
  title: '飞书机器人通道',
  summary: '飞书应用凭证、Webhook / WebSocket 长连接、AES 解密、富媒体与 Markdown 卡片策略',
  credentialMode: 'required' as const,
  credentialHint: '长连接模式仅需 App ID + App Secret；Webhook 模式需额外配置 Encrypt Key 或 Verification Token 之一。',
  fields: [
    { section: 'platform' as const, key: 'feishuEnabled' },
    { section: 'platform' as const, key: 'feishuAppId' },
    { section: 'platform' as const, key: 'feishuApiBase' },
    { section: 'platform' as const, key: 'feishuWhitelist' },
    { section: 'platform' as const, key: 'feishuWhitelistPrivates' }
  ],
  secrets: [3 as const, 4 as const, 5 as const],
  secretLabels: Object.freeze({
    3: '飞书 App Secret',
    4: '飞书 Encrypt Key',
    5: '飞书验证 Token'
  }),
  fieldLabels: Object.freeze({
    feishuEnabled: '启用飞书通道',
    feishuAppId: '飞书 App ID',
    feishuApiBase: '飞书 API 地址',
    feishuWhitelist: '飞书群白名单',
    feishuWhitelistPrivates: '飞书私聊白名单'
  }),
  fieldHints: Object.freeze({
    feishuWhitelist: '填写允许的飞书群聊 chat_id（逗号或空格分隔）；留空默认不放行群聊。',
    feishuWhitelistPrivates: '留空时跟随飞书群白名单；如需私聊全拦，请在下方访问策略条目中设为「拒绝所有人」。'
  }),
  policyCopy: Object.freeze({
    groupListLabel: '允许的飞书群 chat_id（逗号或空格分隔）',
    privateListLabel: '允许的飞书用户 open_id（逗号或空格分隔）',
    groupPlaceholder: '例如 oc_xxxxxxxxxxxxxxxx',
    privatePlaceholder: '例如 ou_xxxxxxxxxxxxxxxx'
  })
})
