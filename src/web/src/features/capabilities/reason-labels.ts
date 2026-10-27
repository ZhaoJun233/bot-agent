/**
 * 能力描述符里 reasonCode 的人话说明。
 *
 * 原始码仍以小字保留（title），方便对照服务端日志排障；未收录的码回退显示原码。
 */
export const reasonLabels: Record<string, string> = {
  restart_required: '需要重启服务后才能执行',
  registration_required: '还没有登记',
  projection_unavailable: '运行状态暂时读不到',
  driver_not_loaded: '驱动尚未加载',
  driver_not_ready: '驱动还没就绪',
  platform_disabled: '所在平台已关闭',
  mcp_disabled: 'MCP 已关闭',
  configuration_required: '需要先完成配置',
  configuration_not_bound: '还没有绑定配置',
  configuration_missing: '缺少必要配置',
  configuration_unavailable: '配置暂时不可用',
  chat_disabled: '对话已关闭',
  feishu_disabled: '飞书通道已关闭',
  feature_disabled: '功能开关已关闭',
  credential_required: '需要先配置凭据',
  credential_missing: '缺少凭据',
  adapter_unavailable: '适配器不可用',
  not_supported: '这个能力不支持该操作',
  unsupported: '这个能力不支持该操作'
}

export function reasonLabel(code: string): string {
  if (!code) return '暂不可用'
  return reasonLabels[code] ?? code
}
