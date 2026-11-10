import { describe, expect, it } from 'vitest'
import { hasMatchingReview, isBuiltinPluginList, isPluginPresentation } from '../../src/api/plugin-presentation'
import { ZbaClient, type ZbaPluginOperation } from '../../src/api/zba'
import { builtinCatalogEntry, catalogEntry, mergePluginCatalog, pluginOperationNotice } from '../../src/features/capabilities/plugin-marketplace'
import { filterPluginCatalog, pluginPackageStatus, pluginReviewed } from '../../src/features/capabilities/plugin-catalog'
import { builtin, packageItem, presentation } from '../fixtures/plugin-presentation'
import { syntheticManifest } from '../fixtures/provider-marketplace'

const reviewMatches = (metadata = presentation()) => hasMatchingReview(metadata, syntheticManifest.version, syntheticManifest.sha256)
describe('frozen presentation contract and provenance', () => {
  it('accepts legacy transport plus additive builtin lists without fabricated manifests', async () => {
    const catalog = { revision: 'r1', source: 'bundled', trustNotice: '来源不代表审计。', categories: [], plugins: [], builtinPlugins: [builtin()], metadataNotice: '使用宿主内置说明。' }
    const client = new ZbaClient({ baseUrl: 'https://example.com', fetchImpl: async () => new Response(JSON.stringify(catalog)) })
    const result = await client.pluginCatalog()
    expect(result.builtinPlugins?.[0]).not.toHaveProperty('manifest')
    expect(result.metadataNotice).toBe(catalog.metadataNotice)
    expect((await client.installedPlugins()).builtinPlugins).toEqual(catalog.builtinPlugins)
    expect(isBuiltinPluginList(undefined)).toBe(true)
    expect(isBuiltinPluginList([{ ...builtin(), enabled: 'false' }])).toBe(false)
  })
  it('requires complete display metadata but permits null builtin audit hashes', () => {
    expect(isPluginPresentation(presentation())).toBe(true)
    expect(isPluginPresentation({ ...presentation(), usage: [] })).toBe(false)
    expect(isPluginPresentation({ ...presentation(), tags: [1] })).toBe(false)
    expect(isBuiltinPluginList([builtin()])).toBe(true)
    expect(reviewMatches(builtin().metadata)).toBe(false)
  })
  it('matches exact version, artifact hash and immutable source commit', () => {
    expect(reviewMatches()).toBe(true)
    const value = presentation()
    for (const patch of [{ version: '2.0.0' }, { sha256: 'c'.repeat(64) }, { reviewer: '' }, { sourceRevision: 'main' }, { sourceRevision: 'b'.repeat(41) }, { reviewedAt: '2026-01-01T00:00:00' }, { reviewedAt: '2026-01-01T00:00:00Z junk' }])
      expect(reviewMatches(presentation({ review: { ...value.review, ...patch } }))).toBe(false)
    expect(reviewMatches(presentation({ review: { ...value.review, sourceRevision: 'b'.repeat(64) } }))).toBe(true)
  })
  it.each(['2026-02-30T00:00:00Z', '2025-02-29T00:00:00Z', '1900-02-29T00:00:00Z', '2026-04-31T00:00:00Z'])(
    'rejects impossible calendar dates: %s', reviewedAt => {
      const value = presentation()
      expect(reviewMatches(presentation({ review: { ...value.review, reviewedAt } }))).toBe(false)
    })
  it.each(['2024-02-29T00:00:00Z', '2000-02-29T12:30:45.123+08:00', '2026-02-28T23:59:59-08:00'])(
    'accepts real dates including Gregorian leap years: %s', reviewedAt => {
      const value = presentation()
      expect(reviewMatches(presentation({ review: { ...value.review, reviewedAt } }))).toBe(true)
    })
  it('rejects a trailing newline in source provenance', () => {
    const value = presentation()
    expect(reviewMatches(presentation({ review: { ...value.review, sourceRevision: 'b'.repeat(40) + '\n' } }))).toBe(false)
  })
  it('rejects a trailing newline in the review timestamp', () => {
    const value = presentation()
    expect(reviewMatches(presentation({ review: { ...value.review, reviewedAt: '2026-01-01T00:00:00Z\n' } }))).toBe(false)
  })
  it('rejects identical artifact and review hashes with trailing newlines', () => {
    const value = presentation()
    const sha256 = syntheticManifest.sha256 + '\n'
    expect(hasMatchingReview(presentation({ review: { ...value.review, sha256 } }), syntheticManifest.version, sha256)).toBe(false)
  })
  it('does not inherit catalog review when resolving a different source artifact', () => {
    const source = catalogEntry(packageItem({ metadata: undefined, manifest: { ...syntheticManifest, sha256: 'c'.repeat(64) } }))
    const rows = mergePluginCatalog([packageItem()], null, [source])
    expect(pluginReviewed(rows[0]!)).toBe(false)
  })
})
describe('unified registry and package discovery', () => {
  it('preserves offline builtin state and prevents package identity collisions', () => {
    const rows = mergePluginCatalog([packageItem({ manifest: { ...syntheticManifest, id: builtin().pluginId } })],
      { revision: 'r1', plugins: [], builtinPlugins: [builtin({ enabled: true })] }, [], [builtin()])
    expect(rows).toHaveLength(1)
    expect(rows[0]).toMatchObject({ kind: 'builtin', enabled: true })
    expect(rows[0]).not.toHaveProperty('manifest')
    expect(pluginPackageStatus(rows[0]!)).toBe('已内置 · 已启用')
  })
  it('retains rich catalog text while installed registry owns state and version', () => {
    const rich = builtin({ metadata: { ...builtin().metadata, description: '丰富的中文功能说明' } })
    const installed = { revision: 'r1', plugins: [], builtinPlugins: [builtin({ enabled: true })] }
    const same = mergePluginCatalog([], installed, [], [rich])
    expect(same[0]?.metadata?.description).toBe('丰富的中文功能说明')
    expect(same[0]?.enabled).toBe(true)
    const changed = mergePluginCatalog([], { ...installed, builtinPlugins: [builtin({ version: '2.0.0' })] }, [], [rich])
    expect(changed[0]?.metadata?.description).toBe(builtin().metadata.description)
  })
  it('honors an empty authoritative builtin snapshot', () => {
    expect(mergePluginCatalog([], { revision: 'r1', plugins: [], builtinPlugins: [] }, [], [builtin()])).toEqual([])
  })
  it('searches Chinese names, IDs and tags, then composes source/runtime/review/state filters', () => {
    const rows = [builtinCatalogEntry(builtin()), catalogEntry(packageItem())]
    expect(filterPluginCatalog(rows, '联网', 'all').map(row => row.id)).toEqual([builtin().pluginId])
    expect(filterPluginCatalog(rows, 'preset.feature', 'all')).toHaveLength(1)
    expect(filterPluginCatalog(rows, '演示', 'all')).toHaveLength(1)
    expect(filterPluginCatalog(rows, '', '互动', { source: 'official', runtime: 'dotnet', review: 'reviewed', state: 'available' })).toHaveLength(1)
    expect(filterPluginCatalog(rows, '', 'all', { source: 'builtin', state: 'disabled' })).toHaveLength(1)
    expect(filterPluginCatalog(rows, '', 'all', { source: 'package', review: 'unreviewed' })).toHaveLength(0)
  })
  it('keeps legacy entries usable and unreviewed', () => {
    const row = catalogEntry(packageItem({ metadata: undefined }))
    expect(row.name).toBe(syntheticManifest.id)
    expect(pluginReviewed(row)).toBe(false)
  })
  it('separates staged, hot application, restart and enabled state', () => {
    expect(pluginPackageStatus(catalogEntry(packageItem({ status: 'staged' })))).toContain('等待热应用')
    expect(pluginPackageStatus(catalogEntry(packageItem({ status: 'staged', restartRequired: true })))).toContain('需重启宿主')
    expect(pluginPackageStatus(catalogEntry(packageItem({ status: 'installed', installedVersion: '1.0.0', enabled: false })))).toContain('已停用')
  })
})
describe('operation receipts do not invent success', () => {
  const operation: ZbaPluginOperation = { id: 'op-synthetic', pluginId: 'example.plugin', kind: 'install', status: 'installed', restartRequired: false }
  it('only confirms the matching achieved target state', () => {
    expect(pluginOperationNotice(operation).tone).toBe('ok')
    expect(pluginOperationNotice({ ...operation, status: 'not_installed' }).tone).toBe('warn')
    expect(pluginOperationNotice({ ...operation, kind: 'uninstall' }).tone).toBe('warn')
    expect(pluginOperationNotice({ ...operation, kind: 'uninstall', status: 'not_installed' }).tone).toBe('ok')
  })
  it('keeps staging, reload, restart and failures distinct', () => {
    expect(pluginOperationNotice({ ...operation, status: 'staged' }).message).toContain('暂存')
    expect(pluginOperationNotice({ ...operation, status: 'reload_required' }).message).toContain('等待热重载')
    expect(pluginOperationNotice({ ...operation, restartRequired: true }).message).toContain('重启 BotAgent 后生效')
    expect(pluginOperationNotice({ ...operation, status: 'failed', errorCode: 'hash_mismatch' }).tone).toBe('bad')
  })
})
