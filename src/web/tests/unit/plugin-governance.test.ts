import { describe, expect, it } from 'vitest'
import { governanceLabel, governanceSourceLabel, isPluginGovernance, isPluginGovernanceSource, type PluginGovernance, type PluginGovernanceSource } from '../../src/api/plugin-governance'
import { isPluginInstallPreview } from '../../src/api/plugin-import'
import { isPluginLifecyclePreview } from '../../src/api/plugin-lifecycle'
import { ZbaClient } from '../../src/api/zba'
import { isPluginManifest } from '../../src/features/capabilities/plugin-manifest'
import { mergePluginCatalog } from '../../src/features/capabilities/plugin-marketplace'
import { installPreview } from '../fixtures/plugin-import'
import { currentManifest, installedRow, lifecyclePreview, nextManifest } from '../fixtures/plugin-lifecycle'

const time = '2026-11-06T00:00:00Z'
const revoked: PluginGovernance = { status: 'revoked', reason: 'Synthetic advisory', updatedAt: time, blocked: true, match: 'sha256' }
const active: PluginGovernance = { status: 'active', reason: null, updatedAt: null, blocked: false, match: 'none' }
const source: PluginGovernanceSource = { status: 'fresh', source: 'https://example.com/governance.json', generatedAt: time, checkedAt: time, errorCode: null }

describe('additive governance contract', () => {
  it.each(['active', 'deprecated', 'withdrawn', 'revoked', 'unknown'] as const)('accepts distinct lifecycle %s', status => {
    expect(isPluginGovernance({ ...revoked, status, blocked: status === 'revoked' })).toBe(true)
  })
  it.each([null, {}, { ...revoked, blocked: false }, { ...active, blocked: true }, { ...revoked, status: ['revoked'] }, { ...revoked, match: ['sha256'] }, { ...revoked, updatedAt: '2026-02-30T00:00:00Z' }])('rejects malformed lifecycle %j', value => {
    expect(isPluginGovernance(value)).toBe(false)
  })
  it.each(['fresh', 'cached', 'unavailable'] as const)('accepts source status %s', status => {
    expect(isPluginGovernanceSource({ ...source, status })).toBe(true)
  })
  it.each([{ ...source, status: ['fresh'] }, { ...source, generatedAt: null }, { ...source, checkedAt: null }, { ...source, errorCode: 'failed' }, { ...source, checkedAt: 'never' }])('rejects misleading fresh source %j', value => {
    expect(isPluginGovernanceSource(value)).toBe(false)
  })
  it('keeps legacy responses valid but labels missing governance unknown', () => {
    expect(isPluginInstallPreview(installPreview(), isPluginManifest)).toBe(true)
    expect(governanceLabel()).toBe('治理状态未知')
    expect(governanceSourceLabel()).toContain('不能视为已通过检查')
    expect(governanceLabel(active)).toContain('不等于安全审核')
    expect(governanceLabel({ ...active, status: 'withdrawn' })).toContain('不等于安全撤销')
    expect(governanceSourceLabel({ ...source, status: 'cached' })).toContain('不能确认未出现新撤销')
  })
  it('accepts native advisory warning checks without dropping blocking checks', () => {
    const warning = { code: 'governance', status: 'warning', message: 'Synthetic offline governance warning' }
    const install = { ...installPreview(), checks: [warning] }
    const update = { ...lifecyclePreview({ kind: 'uninstall', expectedRevision: 'r1', pluginId: currentManifest.id }), checks: [warning] }
    expect(isPluginInstallPreview(install, isPluginManifest)).toBe(true)
    expect(isPluginLifecyclePreview(update, isPluginManifest)).toBe(true)
    expect(isPluginInstallPreview({ ...install, checks: [{ ...warning, status: 'unexpected' }] }, isPluginManifest)).toBe(false)
    expect(isPluginLifecyclePreview({ ...update, checks: [{ ...warning, status: 'unexpected' }] }, isPluginManifest)).toBe(false)
    expect(isPluginInstallPreview({ ...install, checks: [warning, { ...warning, code: 'revoked', status: 'blocked' }] }, isPluginManifest)).toBe(false)
    expect(isPluginLifecyclePreview({ ...update, checks: [warning, { ...warning, code: 'revoked', status: 'blocked' }] }, isPluginManifest)).toBe(false)
  })
  it('never accepts revoked install or rollback as confirmable', () => {
    expect(isPluginInstallPreview(installPreview({ governance: revoked, governanceSource: source }), isPluginManifest)).toBe(false)
    expect(isPluginInstallPreview(installPreview({ governance: revoked, canInstall: false }), isPluginManifest)).toBe(true)
    const rollback = lifecyclePreview({ kind: 'rollback', expectedRevision: 'r1', pluginId: currentManifest.id }, { targetGovernance: revoked })
    expect(isPluginLifecyclePreview(rollback, isPluginManifest)).toBe(false)
    expect(isPluginLifecyclePreview({ ...rollback, canApply: false }, isPluginManifest)).toBe(true)
  })
  it('allows explicit removal of an installed revoked artifact', () => {
    const remove = lifecyclePreview({ kind: 'uninstall', expectedRevision: 'r1', pluginId: currentManifest.id }, { currentGovernance: revoked, targetGovernance: null })
    expect(isPluginLifecyclePreview(remove, isPluginManifest)).toBe(true)
  })
  it('preserves installed warning separately from clean upgrade candidate', () => {
    const rows = mergePluginCatalog([installedRow({ manifest: nextManifest, governance: active })], { revision: 'r1', plugins: [installedRow({ installedGovernance: revoked })] }, [])
    const row = rows[0]
    expect(row?.kind).toBe('package')
    if (row?.kind !== 'builtin') {
      expect(row?.governance).toEqual(active)
      expect(row?.installedGovernance).toEqual(revoked)
      expect(row?.enabled).toBe(true)
    }
  })
  it('decodes governance on catalog and installed envelopes and rejects malformed additions', async () => {
    const payload = { revision: 'r1', source: 'bundled', trustNotice: 'Synthetic', categories: [], plugins: [installedRow({ governance: active, installedGovernance: revoked })], governanceSource: source }
    const client = new ZbaClient({ baseUrl: 'https://example.com', fetchImpl: async () => new Response(JSON.stringify(payload)) })
    expect((await client.pluginCatalog()).governanceSource).toEqual(source)
    expect((await client.installedPlugins()).plugins[0]?.installedGovernance).toEqual(revoked)
    const invalid = new ZbaClient({ baseUrl: 'https://example.com', fetchImpl: async () => new Response(JSON.stringify({ ...payload, governanceSource: { ...source, status: 'trusted' } })) })
    await expect(invalid.pluginCatalog()).rejects.toMatchObject({ code: 'invalid_json' })
    await expect(invalid.installedPlugins()).rejects.toMatchObject({ code: 'invalid_json' })
  })
})
