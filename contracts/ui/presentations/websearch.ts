/** Transitional distribution registration; schema and values are read from the approved backend owner. */
export const researchPresentation = Object.freeze({
  pluginKey: 'preset.feature.websearch',
  title: '联网搜索与链接预览',
  summary: '搜索源、结果条数、超时与链接预览',
  editor: { type: 'declared' as const },
  enableField: { section: 'behavior' as const, key: 'enableWebSearch' },
  fields: ['enableWebSearch', 'webSearchUseModelSearch', 'webSearchSources', 'webSearchMaxResults',
    'webSearchTimeoutSeconds', 'webSearchCooldownSeconds', 'enableLinkPreview', 'linkPreviewMax',
    'linkPreviewTimeoutSeconds'].map(key => ({ section: 'behavior' as const, key }))
})
