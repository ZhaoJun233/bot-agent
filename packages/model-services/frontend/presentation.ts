/** Package-owned routing & sampling presentation metadata for system.model-services. */
export const modelServicesPresentation = Object.freeze({
  pluginKey: 'models',
  kind: 'system' as const,
  title: '路由与采样参数',
  summary: '快速回复档位、思考深度与采样温度参数（API 根地址、默认模型名称与 API Key 请在对应模型供应商卡片中配置）',
  fallbackSections: ['model' as const],
  secrets: [] as const,
  providers: false
})

/** Package-owned provider endpoint & primary API key ownership (managed via ProviderEditorDialog). */
export const modelProvidersPresentation = Object.freeze({
  pluginKey: 'model-providers',
  kind: 'system' as const,
  editor: { type: 'declared' as const },
  title: '模型供应商接入与凭据',
  summary: '各模型供应商的 API 根地址、默认模型名称与 API Key（统一在模型供应商卡片的「编辑配置」中管理）',
  fields: [
    { section: 'model' as const, key: 'modelBaseUrl' },
    { section: 'model' as const, key: 'model' }
  ],
  secrets: [0 as const],
  secretLabels: Object.freeze({ 0: '主模型 API Key' }),
  fieldLabels: Object.freeze({
    modelBaseUrl: '模型接口地址',
    model: '聊天模型'
  })
})
