import { describe, expect, it } from 'vitest'
import { ZbaRequestError, type ZbaPluginCatalogItem, type ZbaPluginOperation } from '../../src/api/zba'
import {
  catalogEntry,
  pluginKeyInvalidated,
  pluginOperationKey,
  pluginOperationNotice,
  pluginRecoveryNotice,
  pluginSettingEdits,
  pluginSettingsDirty,
  pluginSettingsKey,
  resetSettingDrafts
} from '../../src/features/capabilities/plugin-marketplace'
import { createPluginSettingsDraft, type PluginSettingsDraft } from '../../src/features/capabilities/declared-plugin-settings'
import { syntheticManifest, syntheticSettings } from '../fixtures/provider-marketplace'

function item(overrides: Partial<ZbaPluginCatalogItem> = {}): ZbaPluginCatalogItem {
  return { manifest: syntheticManifest, enabled: false, status: 'available', restartRequired: false, ...overrides }
}
function operation(overrides: Partial<ZbaPluginOperation> = {}): ZbaPluginOperation {
  return { id: 'op-1', pluginId: syntheticManifest.id, kind: 'install', status: 'staged', restartRequired: false, ...overrides }
}
function requestError(status: number, code: string): ZbaRequestError {
  return new ZbaRequestError(status, code, code)
}

describe('plugin marketplace view model', () => {
  it('falls back to manifest id when the server omits display metadata', () => {
    const entry = catalogEntry(item())
    expect(entry.id).toBe('example.plugin')
    expect(entry.name).toBe('example.plugin')
    expect(entry.category).toBe('未分类')
    expect(entry.installedVersion).toBeUndefined()
    expect(entry.pendingVersion).toBeUndefined()
  })

  it('shows a staged version only when it differs from the installed one', () => {
    expect(catalogEntry(item({ status: 'staged', installedVersion: '0.9.0' })).pendingVersion).toBe('1.0.0')
    expect(catalogEntry(item({ status: 'staged', installedVersion: '1.0.0' })).pendingVersion).toBeUndefined()
    expect(catalogEntry(item({ status: 'installed', installedVersion: '1.0.0' })).pendingVersion).toBeUndefined()
    expect(catalogEntry(item({ status: 'staged', installedVersion: null })).pendingVersion).toBe('1.0.0')
  })

  it('maps uninstall statuses to the pending-uninstall state', () => {
    const entry = catalogEntry(item({ status: 'pending_uninstall', installedVersion: '1.0.0' }))
    expect(entry.pendingUninstall).toBe(true)
    expect(entry.pendingVersion).toBeUndefined()
  })

  it('drops empty tags and blank display strings', () => {
    const entry = catalogEntry(item({ name: '  ', summary: ' ', category: ' ', author: '', tags: ['', 'ok'] }))
    expect(entry.name).toBe('example.plugin')
    expect(entry.summary).toBe('服务端未提供说明。')
    expect(entry.author).toBeUndefined()
    expect(entry.tags).toEqual(['ok'])
  })
})

describe('idempotency keys', () => {
  it('builds distinguishable stable keys for operation and settings writes', () => {
    expect(pluginOperationKey('install', 'example.plugin', '1.0.0', 'a'.repeat(32), 100)).toBe('plugin:install:example.plugin@1.0.0:100:' + 'a'.repeat(32))
    expect(pluginSettingsKey('example.plugin', 'b'.repeat(32), 200)).toBe('plugin:settings:example.plugin:200:' + 'b'.repeat(32))
    expect(pluginOperationKey('install', 'x', '1') === pluginOperationKey('install', 'x', '1')).toBe(false)
  })
})

describe('operation receipts', () => {
  it('never reports a failed operation as accepted', () => {
    const notice = pluginOperationNotice(operation({ status: 'failed', errorCode: 'hash_mismatch' }))
    expect(notice.tone).toBe('bad')
    expect(notice.message).toContain('没有完成')
    expect(notice.message).toContain('hash_mismatch')
  })
  it('asks for a restart when the server flags one', () => {
    expect(pluginOperationNotice(operation({ status: 'restart_required', restartRequired: true })).message).toContain('重启')
    expect(pluginOperationNotice(operation({ kind: 'uninstall', status: 'restart_required', restartRequired: true })).message).toContain('卸载已受理')
    expect(pluginOperationNotice(operation({ kind: 'update' })).message).toContain('更新已受理')
  })
  it('tells the caller to reconcile with the same key after a lookup miss', () => {
    expect(pluginRecoveryNotice(requestError(404, 'plugin_operation_not_found'))?.message).toContain('同一操作键')
    expect(pluginRecoveryNotice(requestError(409, 'idempotency_conflict'))?.message).toContain('换一个操作键')
    // 只认冻结线点名的 code：其它 404/409 交给通用错误文案。
    expect(pluginRecoveryNotice(requestError(404, 'other_missing'))).toBeNull()
    expect(pluginRecoveryNotice(new Error('boom'))).toBeNull()
  })
  it('only invalidates the stored key on a real idempotency conflict', () => {
    expect(pluginKeyInvalidated(requestError(409, 'idempotency_conflict'))).toBe(true)
    expect(pluginKeyInvalidated(requestError(409, 'other_conflict'))).toBe(false)
    expect(pluginKeyInvalidated(requestError(404, 'plugin_operation_not_found'))).toBe(false)
  })
})

describe('declared settings projection', () => {
  it('sends only real changes and never echoes a secret', () => {
    const draft = createPluginSettingsDraft(syntheticSettings, { caption: '旧值', limit: '5' })
    expect(pluginSettingsDirty(draft)).toBe(false)
    draft.limit = { kind: 'set', value: '9' }
    draft.secret = { kind: 'set', value: 'synthetic-secret-value' }
    draft.format = { kind: 'delete', value: '' }
    expect(pluginSettingEdits(syntheticSettings, draft)).toEqual([
      { key: 'limit', kind: 1, value: '9' },
      { key: 'secret', kind: 1, value: 'synthetic-secret-value' },
      { key: 'format', kind: 2 }
    ])
    expect(pluginSettingsDirty(draft)).toBe(true)
  })

  it('clears every secret draft back to keep after a save', () => {
    const draft: PluginSettingsDraft = { secret: { kind: 'set', value: 'synthetic-secret-value' }, limit: { kind: 'set', value: '9' } }
    resetSettingDrafts(draft, ['secret', 'limit'])
    expect(draft.secret).toEqual({ kind: 'keep', value: '' })
    expect(draft.limit).toEqual({ kind: 'keep', value: '' })
  })
})
