import {
  ZbaRequestError,
  type ZbaPluginCatalogItem,
  type ZbaInstalledPlugins,
  type ZbaPluginOperation,
  type ZbaPluginOperationKind,
  type ZbaPluginSettingEdit
} from '../../api/zba'
import { newNonce, nowSeconds } from '../configuration/draft'
import type { PluginSettingDefinition } from './plugin-manifest'
import type { BuiltinCatalogEntry, PackageCatalogEntry, PluginCatalogEntry } from './plugin-catalog'
import { isPluginPresentation, type ZbaBuiltinPlugin } from '../../api/plugin-presentation'
import type { PluginSettingDraft, PluginSettingsDraft } from './declared-plugin-settings'

/**
 * 插件市场的纯逻辑：传输形状 → 视图模型、幂等键、回执措辞、设置编辑投影。
 *
 * 这里刻意不碰 DOM、不碰网络：页面只负责「什么时候调用」，判断放在这里，
 * 才能真正对「结果未知时该复用哪个键」「404 与 409 分别怎么办」写单测。
 */

/** Missing metadata remains a usable legacy package, never a reviewed one. */
export function catalogEntry(item: ZbaPluginCatalogItem): PackageCatalogEntry {
  const status = (item.status ?? '').toLowerCase()
  const installedVersion = item.installedVersion ?? undefined
  const pendingUninstall = /uninstall|remov/.test(status)
  const staged = ['staged', 'restart_required', 'reload_required', 'pending'].includes(status)
  const metadata = isPluginPresentation(item.metadata) && item.metadata.sourceKind !== 'builtin' && item.metadata.runtime !== 'builtin' ? item.metadata : undefined
  return {
    kind: 'package', id: item.manifest.id, isCompatible: item.isCompatible, compatibilityMessage: item.compatibilityMessage,
    governance: item.governance, installedGovernance: item.installedGovernance,
    name: metadata?.name.trim() || item.name?.trim() || item.manifest.id,
    summary: metadata?.summary.trim() || item.summary?.trim() || '服务端未提供说明。',
    category: metadata?.category.trim() || item.category?.trim() || '未分类',
    author: metadata?.author.trim() || item.author?.trim() || undefined,
    tags: (metadata?.tags ?? item.tags ?? []).filter(tag => tag.trim()),
    repository: metadata?.repository || item.repository, metadata,
    manifest: item.manifest, previousManifest: item.previousManifest, pendingManifest: item.pendingManifest, installedVersion, enabled: item.enabled, status, restartRequired: item.restartRequired,
    pendingVersion: !pendingUninstall && staged && item.manifest.version !== installedVersion ? item.manifest.version : undefined,
    pendingUninstall: pendingUninstall || undefined
  }
}

export function builtinCatalogEntry(item: ZbaBuiltinPlugin): BuiltinCatalogEntry {
  return { kind: 'builtin', id: item.pluginId, version: item.version, enabled: item.enabled,
    name: item.metadata.name, summary: item.metadata.summary, category: item.metadata.category,
    author: item.metadata.author, repository: item.metadata.repository, tags: item.metadata.tags, metadata: item.metadata }
}

/** Registry entries win ID collisions; installed projection owns state, index owns discovery. */
export function mergePluginCatalog(catalog: ZbaPluginCatalogItem[], installed: ZbaInstalledPlugins | null, sources: PluginCatalogEntry[], builtins: ZbaBuiltinPlugin[] = []): PluginCatalogEntry[] {
  const entries = new Map<string, PluginCatalogEntry>()
  for (const item of catalog) entries.set(item.manifest.id, catalogEntry(item))
  // A resolved source replaces presentation too: never inherit another artifact's audit badge.
  for (const source of sources) entries.set(source.id, source)
  for (const item of installed?.plugins ?? []) {
    const state = catalogEntry(item)
    const candidate = entries.get(state.id)
    const entry = candidate?.kind === 'builtin' ? state : candidate ?? state
    entries.set(state.id, { ...entry, installedGovernance: state.installedGovernance, installedVersion: state.installedVersion, previousManifest: state.previousManifest, pendingManifest: state.pendingManifest, enabled: state.enabled,
      pendingVersion: state.pendingVersion, pendingUninstall: state.pendingUninstall, restartRequired: state.restartRequired, status: state.status })
  }
  // An available installed snapshot is authoritative even when its built-in list is empty.
  for (const item of installed?.builtinPlugins ?? builtins) {
    const discovered = builtins.find(candidate => candidate.pluginId === item.pluginId && candidate.version === item.version)
    entries.set(item.pluginId, builtinCatalogEntry({ ...item, metadata: discovered?.metadata ?? item.metadata }))
  }
  return [...entries.values()]
}

/** 同一个「插件 + 版本 + 动作」在重试时必须复用同一个键，所以键由调用方保存。 */
export function pluginOperationKey(
  kind: ZbaPluginOperationKind,
  pluginId: string,
  version: string,
  nonce: string = newNonce(),
  issuedUnixSeconds: number = nowSeconds()
): string {
  return 'plugin:' + kind + ':' + pluginId + '@' + version + ':' + issuedUnixSeconds + ':' + nonce
}

export function pluginSettingsKey(pluginId: string, nonce: string = newNonce(), issuedUnixSeconds: number = nowSeconds()): string {
  return 'plugin:settings:' + pluginId + ':' + issuedUnixSeconds + ':' + nonce
}

/** A matching but uncertain receipt is not permission to release the original operation key. */
export function assertPluginReceipt(operation: ZbaPluginOperation, pluginId: string, kind: unknown): void {
  if (operation.pluginId !== pluginId || operation.kind !== kind) throw new ZbaRequestError(200, 'invalid_json', 'Operation receipt target mismatch')
  if (operation.errorCode === 'plugin_operation_unknown' || operation.errorCode === 'plugin_operation_in_progress')
    throw new ZbaRequestError(503, operation.errorCode, '操作尚未确认，请继续使用原操作键核对。')
}

export interface PluginNotice {
  tone: 'ok' | 'warn' | 'bad'
  message: string
}

/**
 * 区分已热应用、已暂存、兼容宿主需重启以及失败。
 * 热应用失败可能已保存配置或暂存包，不能宣称所有写入都已回滚。
 */
export function pluginOperationNotice(operation: ZbaPluginOperation): PluginNotice {
  const what = operation.kind === 'install' ? '安装' : operation.kind === 'update' ? '更新' : operation.kind === 'rollback' ? '回退' : '卸载'
  if (operation.status === 'failed') {
    const code = operation.errorCode?.trim()
    return { tone: 'bad', message: what + '没有完成' + (code ? '（' + code + '）' : '') + '：请读取最新目录确认状态后再试。' }
  }
  if (operation.status === 'restart_required' || operation.restartRequired) {
    return { tone: 'warn', message: what + '已受理，重启 BotAgent 后生效。' }
  }
  if (operation.status === 'reload_required' || operation.status === 'staged')
    return { tone: 'warn', message: what + '已暂存，等待热重载；旧实例保持运行。' }
  if (operation.status === 'installed' && operation.kind !== 'uninstall')
    return { tone: 'ok', message: what + '已热应用，无需重启 BotAgent。' }
  if (operation.status === 'not_installed' && operation.kind === 'uninstall')
    return { tone: 'ok', message: '卸载已完成，当前未安装。' }
  return { tone: 'warn', message: what + '回执与目标状态不一致，请核对宿主状态；不能确认已完成。' }
}

/**
 * 结果未知时的恢复指引：404 说明这个键还没有回执（继续用同一个键核对），
 * 409 无法核对原请求关联，仍保留原键，不能换键重复提交。
 */
export function pluginRecoveryNotice(cause: unknown): PluginNotice | null {
  if (!(cause instanceof ZbaRequestError)) return null
  // 只认冻结线点名的两个 code：409 也可能是版本冲突，那时旧键仍然有效。
  if (cause.code === 'plugin_preview_refresh_required') return { tone: 'warn', message: '来源、目录或预览已变化/过期；没有继续安装。请重新发现并预览，再确认风险。' }
  if (cause.code === 'plugin_operation_unknown' || cause.code === 'plugin_operation_in_progress') return { tone: 'warn', message: '操作结果仍未知：请保留原操作键继续核对，不要换键重复提交。' }
  if (cause.code === 'plugin_reload_failed') return { tone: 'warn', message: '变更已保存，但热重载失败，旧实例保持不变。请修正后重试。' }
  if (cause.code === 'plugin_operation_not_found') {
    return { tone: 'warn', message: '这个操作键还没有回执：服务端可能尚未受理。请稍后用同一操作键重新核对，不要换键重复提交。' }
  }
  if (cause.code === 'idempotency_conflict') {
    return { tone: 'warn', message: '原操作键的回执与请求关联无法核对，结果仍未知。请保留原操作键并人工核对，不要换键重复提交。' }
  }
  return null
}

/** 清单声明草稿 → wire 编辑：keep 不进请求，delete 不带值，其余带值。 */
export function pluginSettingEdits(definitions: PluginSettingDefinition[], draft: PluginSettingsDraft): ZbaPluginSettingEdit[] {
  const edits: ZbaPluginSettingEdit[] = []
  for (const field of definitions) {
    const edit = draft[field.key]
    if (!edit || edit.kind === 'keep') continue
    if (edit.kind === 'delete') {
      edits.push({ key: field.key, kind: 2 })
      continue
    }
    edits.push({ key: field.key, kind: 1, value: edit.value })
  }
  return edits
}

/** 草稿里是否有真实改动（keep 不算）。 */
export function pluginSettingsDirty(draft: PluginSettingsDraft): boolean {
  return Object.values(draft).some((edit) => edit.kind !== 'keep')
}

/** 保存成功后把密钥草稿清回 keep：响应里没有明文，草稿里也不能留。 */
export function resetSettingDrafts(draft: PluginSettingsDraft, keys: string[]): void {
  for (const key of keys) {
    const edit: PluginSettingDraft | undefined = draft[key]
    if (edit) draft[key] = { kind: 'keep', value: '' }
  }
}