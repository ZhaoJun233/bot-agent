/** Package-owned presentation metadata for system.platform-* capabilities. */
export const platformPresentation = Object.freeze({
  pluginKey: 'platform',
  kind: 'system' as const,
  title: '平台接入与访问策略',
  summary: '私域、官方与飞书接入、白名单及飞书凭据',
  fallbackSections: ['platform' as const],
  secrets: [3 as const, 4 as const, 5 as const]
})
