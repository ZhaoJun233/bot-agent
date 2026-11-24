/** Package-owned presentation metadata for preset.feature.vibes. */
export const vibesPresentation = Object.freeze({
  pluginKey: 'preset.feature.vibes',
  title: '氛围与心情',
  summary: '心情保留时长',
  fields: ['moodTtlSeconds'].map((key) => ({ section: 'behavior' as const, key }))
})
