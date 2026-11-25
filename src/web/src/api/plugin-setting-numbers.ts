export interface PluginSettingNumberConstraints {
  minimum?: number | null
  maximum?: number | null
  integer?: boolean
}

const record = (value: unknown): value is Record<string, unknown> =>
  typeof value === 'object' && value !== null && !Array.isArray(value)
const bound = (value: unknown): boolean => value == null || typeof value === 'number' && Number.isFinite(value)

export function isSettingNumberConstraints(value: unknown): value is PluginSettingNumberConstraints {
  return record(value) && Object.keys(value).every(key => ['minimum', 'maximum', 'integer'].includes(key))
    && bound(value.minimum) && bound(value.maximum)
    && (value.integer === undefined || typeof value.integer === 'boolean')
    && !(typeof value.minimum === 'number' && typeof value.maximum === 'number' && value.minimum > value.maximum)
}

/** Use JSON integer syntax so the saved text is accepted by the same server rule. */
export function validSettingNumberText(text: string, constraints: PluginSettingNumberConstraints): boolean {
  let value: unknown
  try { value = JSON.parse(text) } catch { return false }
  return typeof value === 'number' && Number.isFinite(value)
    && (constraints.minimum == null || value >= constraints.minimum)
    && (constraints.maximum == null || value <= constraints.maximum)
    && (!constraints.integer || Number.isSafeInteger(value) && /^-?(0|[1-9][0-9]*)$/.test(text.trim()))
}

export function hasValidSettingNumberConstraints(setting: Record<string, unknown>): boolean {
  const constraints = setting.numberConstraints
  return constraints == null || setting.type === 'number' && isSettingNumberConstraints(constraints)
    && (setting.defaultValue == null || typeof setting.defaultValue === 'string'
      && validSettingNumberText(setting.defaultValue, constraints))
}

export function canonicalSettingNumberConstraints(value: PluginSettingNumberConstraints) {
  return { minimum: value.minimum ?? null, maximum: value.maximum ?? null, integer: value.integer ?? false }
}

/** Inspect valid JSON keys before accepting JSON.parse's duplicate-key normalization. */
export function hasDuplicatePluginDeclarationKeys(input: string): boolean {
  type Frame = { object: boolean; constrained: boolean; bindings: boolean; key: string | null; nextKey: boolean; keys: Set<string> }
  const frames: Frame[] = []
  for (let index = 0; index < input.length; index++) {
    const token = input[index]
    const parent = frames.at(-1)
    if (token === '{' || token === '[') {
      const name = parent?.key?.toLowerCase()
      frames.push({ object: token === '{', constrained: token === '{' &&
        (name === 'numberconstraints' || name === 'configuration' || parent?.bindings === true),
        bindings: token === '[' && name === 'bindings' && parent?.constrained === true,
        key: null, nextKey: token === '{', keys: new Set() })
    } else if (token === '}' || token === ']') frames.pop()
    else if (token === ',' && parent?.object) { parent.nextKey = true; parent.key = null }
    else if (token === '"') {
      const start = index
      while (++index < input.length) {
        if (input.charCodeAt(index) === 92) index++ // JSON escape: consume its following character.
        else if (input[index] === '"') break
      }
      if (parent?.object && parent.nextKey) {
        const key: string = JSON.parse(input.slice(start, index + 1))
        if ((parent.constrained || ['configuration', 'numberconstraints'].includes(key.toLowerCase()))
          && parent.keys.has(key.toLowerCase())) return true
        parent.keys.add(key.toLowerCase()); parent.key = key; parent.nextKey = false
      }
    }
  }
  return false
}

export function settingNumberHint(value: PluginSettingNumberConstraints): string {
  const kind = value.integer ? '整数' : '有限数值'
  const range = value.minimum != null && value.maximum != null ? `${value.minimum} 至 ${value.maximum}`
    : value.minimum != null ? `至少 ${value.minimum}` : value.maximum != null ? `至多 ${value.maximum}` : ''
  return range ? `${kind}，${range}` : kind
}
