/** Package-owned presentation metadata for preset.feature.profiles. */
export const profilesPresentation = Object.freeze({
  pluginKey: 'preset.feature.profiles',
  title: '人物画像',
  summary: '画像摘要、巡检与字数预算',
  enableField: { section: 'behavior' as const, key: 'enableProfileSummary' },
  fields: [
    'enableProfileSummary',
    'profileSummaryThreshold',
    'profileSummaryIntervalSeconds',
    'profileSummaryLines',
    'profileSummaryMaxChars',
    'maxProfileChars',
    'profileLookupCount'
  ].map((key) => ({ section: 'behavior' as const, key }))
})
