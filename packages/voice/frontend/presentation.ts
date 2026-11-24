/** Package-owned presentation metadata for preset.feature.voice. */
export const voicePresentation = Object.freeze({
  pluginKey: 'preset.feature.voice',
  title: '语音合成与识别',
  summary: 'TTS 语音回复合成、云端入站语音转写（STT）与音色参数',
  enableField: { section: 'voice' as const, key: 'enableVoice' },
  sections: ['voice' as const],
  secrets: [2 as const]
})
