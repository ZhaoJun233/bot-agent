/** Package-owned presentation metadata for preset.feature.voice. */
export const voicePresentation = Object.freeze({
  pluginKey: 'preset.feature.voice',
  title: '语音合成',
  summary: 'TTS 服务、音色与语音参数',
  enableField: { section: 'voice' as const, key: 'enableVoice' },
  sections: ['voice' as const],
  secrets: [2 as const]
})
