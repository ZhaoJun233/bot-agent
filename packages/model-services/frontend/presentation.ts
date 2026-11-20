/** Package-owned presentation metadata for system.model-services. */
export const modelServicesPresentation = Object.freeze({
  pluginKey: 'models',
  kind: 'system' as const,
  title: '模型服务',
  summary: '主模型连接、模型选择、采样参数与 API 凭据',
  fallbackSections: ['model' as const],
  secrets: [0 as const],
  providers: true
})
