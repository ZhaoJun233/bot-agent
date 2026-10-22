/**
 * 冻结 wire 样例的最小校验器。
 *
 * 只用于测试：按精确 key 集合检查对象（未知/缺失字段立即失败），并拒绝
 * `null` 值（ZbaJson 的 DefaultIgnoreCondition.WhenWritingNull 会省略 null）。
 * JSON 文本另做重复 key 检查；JSON.parse 会静默保留最后一个重复 key。
 *
 * `record` 用于“字段名由后端决定、但每个值形状固定”的字典（例如配置 view
 * 的 8 个分区）；`json` 用于值类型随字段变化的载荷（例如 ReadField.value），
 * 其 `null` 仍由 `assertNoNulls` 拒绝。
 */

export type WireShape =
  | { type: 'object'; fields: Record<string, WireShape>; optional?: readonly string[] }
  | { type: 'record'; values: WireShape; min?: number }
  | { type: 'array'; items: WireShape }
  | { type: 'string' }
  | { type: 'number' }
  | { type: 'boolean' }
  | { type: 'null' }
  | { type: 'json' }

function fail(path: string, message: string): never {
  throw new Error(`${path}: ${message}`)
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

export function assertWire(value: unknown, shape: WireShape, path = '$'): void {
  switch (shape.type) {
    case 'object': {
      if (!isRecord(value)) fail(path, 'expected object')
      const optional = new Set(shape.optional ?? [])
      for (const key of Object.keys(value)) {
        if (!(key in shape.fields)) fail(`${path}.${key}`, 'unknown field')
      }
      for (const [key, child] of Object.entries(shape.fields)) {
        if (!(key in value)) {
          if (optional.has(key)) continue
          fail(`${path}.${key}`, 'missing field')
        }
        assertWire(value[key], child, `${path}.${key}`)
      }
      return
    }
    case 'record': {
      if (!isRecord(value)) fail(path, 'expected object')
      const entries = Object.entries(value)
      if (shape.min !== undefined && entries.length < shape.min) {
        fail(path, `expected at least ${shape.min} entries`)
      }
      for (const [key, child] of entries) assertWire(child, shape.values, `${path}.${key}`)
      return
    }
    case 'array':
      if (!Array.isArray(value)) fail(path, 'expected array')
      value.forEach((item, index) => assertWire(item, shape.items, `${path}[${index}]`))
      return
    case 'json':
      if (value === undefined) fail(path, 'expected json value')
      return
    case 'string':
      if (typeof value !== 'string') fail(path, 'expected string')
      return
    case 'number':
      if (typeof value !== 'number' || !Number.isFinite(value)) fail(path, 'expected number')
      return
    case 'boolean':
      if (typeof value !== 'boolean') fail(path, 'expected boolean')
      return
    case 'null':
      if (value !== null) fail(path, 'expected null')
      return
  }
}

export function assertNoNulls(value: unknown, path = '$'): void {
  if (value === null) fail(path, 'null must be omitted on the wire')
  if (Array.isArray(value)) {
    value.forEach((item, index) => assertNoNulls(item, `${path}[${index}]`))
    return
  }
  if (isRecord(value)) {
    for (const [key, child] of Object.entries(value)) assertNoNulls(child, `${path}.${key}`)
  }
}

/**
 * 扫描 JSON 文本并在任意层级拒绝重复 key。
 * 只做重复 key 检测；合法性由调用方的 JSON.parse 兜底。
 */
export function assertNoDuplicateKeys(text: string, source = 'wire'): void {
  let index = 0
  const length = text.length

  const error = (message: string): never => {
    throw new Error(`${source}: ${message} at offset ${index}`)
  }

  const skipWhitespace = (): void => {
    while (index < length && (text[index] === ' ' || text[index] === '\t'
      || text[index] === '\r' || text[index] === '\n')) index += 1
  }

  const parseString = (): string => {
    if (text[index] !== '"') error('expected string')
    const start = index
    index += 1
    let escaped = false
    while (index < length) {
      const current = text[index]
      if (escaped) {
        escaped = false
        index += 1
        continue
      }
      if (current === '\\') {
        escaped = true
        index += 1
        continue
      }
      if (current === '"') {
        index += 1
        return JSON.parse(text.slice(start, index)) as string
      }
      index += 1
    }
    return error('unterminated string')
  }

  const parseNumber = (): void => {
    const match = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(text.slice(index))
    if (match === null) {
      error('invalid number')
      return
    }
    index += match[0].length
  }

  const parseLiteral = (literal: string): boolean => {
    if (!text.startsWith(literal, index)) return false
    index += literal.length
    return true
  }

  const parseValue = (path: string): void => {
    skipWhitespace()
    const current = text[index]
    if (current === '{') {
      parseObject(path)
      return
    }
    if (current === '[') {
      parseArray(path)
      return
    }
    if (current === '"') {
      parseString()
      return
    }
    if (current === '-' || (current !== undefined && current >= '0' && current <= '9')) {
      parseNumber()
      return
    }
    if (parseLiteral('true') || parseLiteral('false') || parseLiteral('null')) return
    return error('invalid value')
  }

  const parseObject = (path: string): void => {
    index += 1
    skipWhitespace()
    if (text[index] === '}') {
      index += 1
      return
    }

    const keys = new Set<string>()
    while (true) {
      skipWhitespace()
      const key = parseString()
      if (keys.has(key)) error(`duplicate key ${path}.${key}`)
      keys.add(key)

      skipWhitespace()
      if (text[index] !== ':') error('expected ":"')
      index += 1
      parseValue(`${path}.${key}`)

      skipWhitespace()
      if (text[index] === ',') {
        index += 1
        continue
      }
      if (text[index] === '}') {
        index += 1
        return
      }
      error('expected "," or "}"')
    }
  }

  const parseArray = (path: string): void => {
    index += 1
    skipWhitespace()
    if (text[index] === ']') {
      index += 1
      return
    }

    let item = 0
    while (true) {
      parseValue(`${path}[${item}]`)
      item += 1
      skipWhitespace()
      if (text[index] === ',') {
        index += 1
        continue
      }
      if (text[index] === ']') {
        index += 1
        return
      }
      error('expected "," or "]"')
    }
  }

  skipWhitespace()
  parseValue('$')
  skipWhitespace()
  if (index !== length) error('trailing characters')
}
