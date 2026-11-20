/** Package-owned presentation metadata for preset.feature.poke. */
export const pokePresentation = Object.freeze({
  pluginKey: 'preset.feature.poke',
  title: '戳一戳',
  summary: '开关与冷却',
  enableField: { section: 'behavior' as const, key: 'enablePoke' },
  fields: ['enablePoke', 'pokeCooldownSeconds'].map((key) => ({ section: 'behavior' as const, key }))
})
