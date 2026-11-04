import type {
  ZbaConfigurationPatch,
  ZbaConfigurationView,
  ZbaFieldEdit,
  ZbaPatchSection,
  ZbaProviderNameEdit,
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

/**
 * 供应商改名的草稿（S4-L1）。
 *
 * key 是供应商的登记键（例如 `primary`），值是用户输入的真名。
 * 基线与提交都只认 `nameRaw`：`display` 是脱敏串，拿它当基线会让
 * 「没改过」和「改成了占位名」分不出来。
 */
export type ProviderNameDrafts = Record<string, string>

/** 供应商的改名基线：可改名投影带 nameRaw，其它形态没有可写值。 */
export function providerNameBaseline(field: ZbaReadField): string {
  if (field.visibility !== 2) return ''
  return field.nameRaw ?? ''
}

/**
 * 把供应商改名草稿合并进 patch。
 *
 * 与标量字段一样只提交**真正改过**的项：没改的供应商一个字节都不发，
 * 否则每次保存都会把整张供应商表重写成 Clear/Set，把别人的改动覆盖掉。
 */
export function applyProviderNames(
  patch: ZbaConfigurationPatch,
  drafts: ProviderNameDrafts,
  view: ZbaConfigurationView
): string[] {
  const errors: string[] = []
  const edits: ZbaProviderNameEdit[] = []
  for (const [key, raw] of Object.entries(drafts)) {
    const provider = view.registered.providers.find((item) => item.key === key)
    if (provider === undefined) {
      errors.push(`供应商 ${key}：不存在`)
      continue
    }
    // 只有 visibility=2（可改名投影）才带 nameRaw；别的形态没有真名可写，
    // 硬发一个 nameRaw 会把「看不到的值」当成改名提交上去。
    if (provider.name.visibility !== 2) {
      errors.push(`供应商 ${key}：这一版读回的名称不可改名`)
      continue
    }
    const baseline = providerNameBaseline(provider.name)
    if (raw === baseline) continue
    // 空名会被后端拒（Name 允许为空串但界面不该交上去）；这里当校验错误处理。
    if (raw.trim().length === 0) {
      errors.push(`供应商 ${key}：名称不能为空`)
      continue
    }
    // 真名与脱敏展示串相同 = 用户把占位符当成了名字（PNL-11 明确禁止写回）。
    if (raw === provider.name.display && raw !== baseline) {
      errors.push(`供应商 ${key}：这是脱敏后的展示名，不是真名，不能写回`)
      continue
    }
    edits.push({
      key,
      // ProviderMetadataPatch 是闭合记录：六个字段都得出现，没改的用 Keep。
      changes: {
        nameRaw: { kind: 1, value: raw },
        priority: { kind: 0 },
        baseUrl: { kind: 0 },
        modelName: { kind: 0 },
        enabled: { kind: 0 },
        credential: { kind: 0 }
      }
    })
  }
  if (edits.length > 0) {
    patch.registered = { plugins: [], policies: [], providers: edits }
  }
  return errors
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

/** 字段在某个快照里的可比较形态：可见性 + 值（隐藏字段只比可见性与展示串）。 */
function fieldSignature(field: ZbaReadField): string {
  return JSON.stringify([field.visibility, field.visibility === 1 ? (field.value ?? null) : field.display])
}

/**
 * 冲突后重新读取基线：找出**对方**改过、且本地草稿也改了的字段。
 *
 * 比较的是「草稿起草时的基线」与「新基线」，不是草稿与新基线——
 * 后者会把用户自己改过的每个字段都误报成冲突。
 * 没有起草基线（`base === null`）时无从证明对方没改，保守地全部列出。
 */
export function conflictingPaths(
  draft: DraftMap,
  base: ZbaConfigurationView | null,
  next: ZbaConfigurationView
): string[] {
  return Object.keys(draft).filter((path) => {
    const parts = splitPath(path)
    if (parts === null) return true
    const after = editableField(next, parts[0], parts[1])
    if (after === undefined) return true
    if (base === null) return true
    const before = editableField(base, parts[0], parts[1])
    if (before === undefined) return true
    return fieldSignature(before) !== fieldSignature(after)
  })
}

/** 供应商改名的冲突：起草基线与新基线的 nameRaw（或可见性）不同。 */
export function conflictingProviderNames(
  drafts: ProviderNameDrafts,
  base: ZbaConfigurationView | null,
  next: ZbaConfigurationView
): string[] {
  return Object.keys(drafts).filter((key) => {
    const after = next.registered.providers.find((item) => item.key === key)
    if (after === undefined || base === null) return true
    const before = base.registered.providers.find((item) => item.key === key)
    if (before === undefined) return true
    return before.name.visibility !== after.name.visibility
      || providerNameBaseline(before.name) !== providerNameBaseline(after.name)
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
