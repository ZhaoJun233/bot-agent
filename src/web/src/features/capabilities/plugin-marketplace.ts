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
import type { PluginCatalogEntry } from './plugin-catalog'
import type { PluginSettingDraft, PluginSettingsDraft } from './declared-plugin-settings'

/**
 * 插件市场的纯逻辑：传输形状 → 视图模型、幂等键、回执措辞、设置编辑投影。
 *
 * 这里刻意不碰 DOM、不碰网络：页面只负责「什么时候调用」，判断放在这里，
 * 才能真正对「结果未知时该复用哪个键」「404 与 409 分别怎么办」写单测。
 */

/** 服务端目录条目 → 视图模型；展示元数据缺失时回落到 manifest.id。 */
export function catalogEntry(item: ZbaPluginCatalogItem): PluginCatalogEntry {
  const status = (item.status ?? '').toLowerCase()
  const installedVersion = item.installedVersion ?? undefined
  // 卸载是暂存态而不是版本差异：状态串里出现 uninstall/remov 就按「待卸载」展示。
  const pendingUninstall = /uninstall|remov/.test(status)
  const staged = status === 'staged' || status === 'restart_required' || status === 'pending'
  const pendingVersion = !pendingUninstall && staged && item.manifest.version !== installedVersion
    ? item.manifest.version
    : undefined
  const tags = (item.tags ?? []).filter((tag) => typeof tag === 'string' && tag.length > 0)
  return {
    id: item.manifest.id,
    name: item.name?.trim() || item.manifest.id,
    summary: item.summary?.trim() || '服务端未提供说明。',
    category: item.category?.trim() || '未分类',
    author: item.author?.trim() || undefined,
    tags: tags.length > 0 ? tags : undefined,
    repository: item.repository,
    manifest: item.manifest,
    installedVersion,
    pendingVersion,
    pendingUninstall: pendingUninstall || undefined
  }
}

/** One row per plugin ID; the installed projection owns package state, not the index/source. */
export function mergePluginCatalog(catalog: ZbaPluginCatalogItem[], installed: ZbaInstalledPlugins | null, sources: PluginCatalogEntry[]): PluginCatalogEntry[] {
  const entries = new Map<string, PluginCatalogEntry>()
  for (const item of catalog) entries.set(item.manifest.id, catalogEntry(item))
  for (const source of sources) entries.set(source.id, { ...entries.get(source.id), ...source })
  for (const item of installed?.plugins ?? []) {
    const state = catalogEntry(item)
    const entry = entries.get(state.id) ?? state
    entries.set(state.id, { ...entry, installedVersion: state.installedVersion, pendingVersion: state.pendingVersion, pendingUninstall: state.pendingUninstall })
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

export interface PluginNotice {
  tone: 'ok' | 'warn' | 'bad'
  message: string
}

/**
 * 回执措辞。status 只有 staged|restart_required|failed 三种终态：
 * 前两种是「已经写进去了」，failed 才是「没写成」，必须说清楚，否则用户会重复提交。
 */
export function pluginOperationNotice(operation: ZbaPluginOperation): PluginNotice {
  const what = operation.kind === 'install' ? '安装' : operation.kind === 'update' ? '更新' : '卸载'
  if (operation.status === 'failed') {
    const code = operation.errorCode?.trim()
    return { tone: 'bad', message: what + '没有完成' + (code ? '（' + code + '）' : '') + '：请读取最新目录确认状态后再试。' }
  }
  if (operation.status === 'restart_required' || operation.restartRequired) {
    return { tone: 'warn', message: what + '已受理，重启 BotAgent 后生效。' }
  }
  return { tone: 'ok', message: what + '已受理，将在服务端就绪后生效。' }
}

/**
 * 结果未知时的恢复指引：404 说明这个键还没有回执（继续用同一个键核对），
 * 409 说明这个键已经绑定了另一份变更（必须作废重来，绝不能换键重提同一份变更）。
 */
export function pluginRecoveryNotice(cause: unknown): PluginNotice | null {
  if (!(cause instanceof ZbaRequestError)) return null
  // 只认冻结线点名的两个 code：409 也可能是版本冲突，那时旧键仍然有效。
  if (cause.code === 'plugin_operation_not_found') {
    return { tone: 'warn', message: '这个操作键还没有回执：服务端可能尚未受理。请稍后用同一操作键重新核对，不要换键重复提交。' }
  }
  if (cause.code === 'idempotency_conflict') {
    return { tone: 'warn', message: '这个操作键已经用于另一份变更，已被服务端作废：请重新读取目录、确认内容后换一个操作键提交。' }
  }
  return null
}

/** 幂等键冲突后必须换键：调用方据此清掉保存的旧键。 */
export function pluginKeyInvalidated(cause: unknown): boolean {
  return cause instanceof ZbaRequestError && cause.code === 'idempotency_conflict'
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
