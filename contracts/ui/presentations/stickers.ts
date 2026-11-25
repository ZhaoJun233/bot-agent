/** Package-owned presentation metadata for preset.feature.stickers. */
export const stickersPresentation = Object.freeze({
  pluginKey: 'preset.feature.stickers',
  title: '表情包',
  summary: '表情收录、候选与冷却',
  enableField: { section: 'behavior' as const, key: 'enableStickers' },
  fields: [
    'enableStickers',
    'stickerCandidates',
    'stickerCooldownSeconds',
    'stickerCurateIntervalSeconds',
    'stickerLibraryMax'
  ].map((key) => ({ section: 'behavior' as const, key }))
})
