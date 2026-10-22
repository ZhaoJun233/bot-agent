import type {
  ZbaConfigurationPatch,
  ZbaConfigurationView,
  ZbaFieldEdit,
  ZbaPatchSection,
  ZbaReadField,
  ZbaSecretEdit,
  ZbaSecretSlot
} from '../../api/zba'

/** 一个字段的草稿：Set 保存用户输入的原始文本，Clear 表示显式清除。 */
export interface DraftSet {
  kind: 'set'
  raw: string
}

export interface DraftClear {
  kind: 'clear'
}

export type DraftEdit = DraftSet | DraftClear

/** key 是 `<section>.<field>`；只保存用户明确改过的字段。 */
export type DraftMap = Record<string, DraftEdit>

export type FieldValueKind = 'string' | 'number' | 'boolean'

export interface PatchBuildResult {
  patch: ZbaConfigurationPatch
  errors: string[]
}

export type SecretAction =
  | { kind: 'keep' }
  | { kind: 'replace'; value: string }
  | { kind: 'delete' }

export type SecretDrafts = Partial<Record<ZbaSecretSlot, SecretAction>>

export function fieldPath(section: ZbaPatchSection, key: string): string {
  return `${section}.${key}`
}

function splitPath(path: string): [ZbaPatchSection, string] | null {
  const dot = path.indexOf('.')
  if (dot <= 0 || dot === path.length - 1) return null
  const section = path.slice(0, dot)
  const key = path.slice(dot + 1)
  const sections: ZbaPatchSection[] = ['behavior', 'model', 'agent', 'platform', 'voice', 'music', 'reporting']
  if (!sections.includes(section as ZbaPatchSection)) return null
  return [section as ZbaPatchSection, key]
}

export function editableField(
  view: ZbaConfigurationView,
  section: ZbaPatchSection,
  key: string
): ZbaReadField | undefined {
  return view[section]?.[key]
}

/** 字段值的编辑形态；数组/对象这一版不提供编辑控件。 */
export function valueKind(field: ZbaReadField): FieldValueKind | null {
  if (field.visibility !== 1) return null
  const value = field.value
  if (typeof value === 'string') return 'string'
  if (typeof value === 'number') return 'number'
  if (typeof value === 'boolean') return 'boolean'
  return null
}

export function isEditable(field: ZbaReadField): boolean {
  return valueKind(field) !== null
}

/** 把当前快照值转成输入框文本；用于展示与“是否修改”比较。 */
export function baselineRaw(field: ZbaReadField): string {
  if (field.visibility !== 1) return ''
  const value = field.value
  if (typeof value === 'string') return value
  if (typeof value === 'number') return String(value)
  if (typeof value === 'boolean') return value ? 'true' : 'false'
  if (value === null || value === undefined) return ''
  return JSON.stringify(value)
}

export function draftRaw(draft: DraftMap, path: string, field: ZbaReadField): string {
  const edit = draft[path]
  if (edit?.kind === 'set') return edit.raw
  return baselineRaw(field)
}

export function isDirty(draft: DraftMap, path: string, field: ZbaReadField): boolean {
  const edit = draft[path]
  if (!edit) return false
  if (edit.kind === 'clear') return true
  return edit.raw !== baselineRaw(field)
}

/**
 * 把草稿转换成 save/validate 的 `changes`。
 *
 * 只发送用户明确修改过的字段；Keep 不发送，隐藏/不可编辑字段永远不会被写回。
 */
export function buildPatch(draft: DraftMap, view: ZbaConfigurationView): PatchBuildResult {
  const patch: ZbaConfigurationPatch = {}
  const errors: string[] = []

  for (const [path, edit] of Object.entries(draft)) {
    const parts = splitPath(path)
    if (parts === null) {
      errors.push(`${path}：字段路径无效`)
      continue
    }
    const [section, key] = parts
    const field = editableField(view, section, key)
    if (field === undefined) {
      errors.push(`${path}：字段不存在`)
      continue
    }

    let value: ZbaFieldEdit
    if (edit.kind === 'clear') {
      value = { kind: 2 }
    } else {
      const kind = valueKind(field)
      if (kind === null) {
        errors.push(`${path}：该类型暂不支持编辑`)
        continue
      }
      if (kind === 'number') {
        const number = Number(edit.raw)
        if (edit.raw.trim() === '' || !Number.isFinite(number)) {
          errors.push(`${path}：需要数字`)
          continue
        }
        value = { kind: 1, value: number }
      } else if (kind === 'boolean') {
        if (edit.raw !== 'true' && edit.raw !== 'false') {
          errors.push(`${path}：需要 true/false`)
          continue
        }
        value = { kind: 1, value: edit.raw === 'true' }
      } else {
        value = { kind: 1, value: edit.raw }
      }
    }

    const sectionPatch = patch[section] ?? {}
    sectionPatch[key] = value
    patch[section] = sectionPatch
  }

  return { patch, errors }
}

export function buildSecrets(drafts: SecretDrafts): { secrets: ZbaSecretEdit[]; errors: string[] } {
  const secrets: ZbaSecretEdit[] = []
  const errors: string[] = []

  for (const [slotText, action] of Object.entries(drafts)) {
    if (action === undefined || action.kind === 'keep') continue
    const slot = Number(slotText) as ZbaSecretSlot
    if (!Number.isInteger(slot) || slot < 0 || slot > 5) {
      errors.push(`槽位 ${slotText}：无效`)
      continue
    }
    if (action.kind === 'delete') {
      secrets.push({ slot, kind: 2 })
      continue
    }
    if (action.value.length === 0) {
      errors.push(`槽位 ${slot}：替换值不能为空`)
      continue
    }
    secrets.push({ slot, kind: 1, value: action.value })
  }

  return { secrets, errors }
}

/** 冲突后重新读取基线：草稿值与新基线不同的字段，说明对方改过同一字段。 */
export function conflictingPaths(draft: DraftMap, view: ZbaConfigurationView): string[] {
  return Object.keys(draft).filter((path) => {
    const parts = splitPath(path)
    if (parts === null) return true
    const field = editableField(view, parts[0], parts[1])
    if (field === undefined) return true
    const edit = draft[path]
    if (edit === undefined) return false
    if (edit.kind === 'clear') return field.value !== undefined && field.value !== null
    return edit.raw !== baselineRaw(field)
  })
}

/** `cfg1:<generation>:<issuedUnixSeconds>:<nonce>`；generation/nonce 都是 32 位十六进制。 */
export function createOperationKey(generation: string, issuedUnixSeconds: number, nonce: string): string {
  return `cfg1:${generation}:${issuedUnixSeconds}:${nonce}`
}

export function newNonce(): string {
  const bytes = new Uint8Array(16)
  crypto.getRandomValues(bytes)
  return Array.from(bytes, (byte) => byte.toString(16).padStart(2, '0')).join('')
}

export function nowSeconds(): number {
  return Math.floor(Date.now() / 1000)
}
