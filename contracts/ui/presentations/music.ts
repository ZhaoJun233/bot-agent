/** Package-owned presentation metadata for preset.feature.music. */
export const musicPresentation = Object.freeze({
  pluginKey: 'preset.feature.music',
  title: '点歌与音乐解析',
  summary: '点歌开关、音源、下载与音频分析',
  enableField: { section: 'music' as const, key: 'enableMusic' },
  sections: ['music' as const]
})
