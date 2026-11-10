import { describe, expect, it } from 'vitest'
import { isPluginInstallPreview, isPluginSourceDiscovery } from '../../src/api/plugin-import'
import { isPluginManifest } from '../../src/features/capabilities/plugin-manifest'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import { samePreviewManifest } from '../../src/features/capabilities/plugin-preview-candidate'
import { discovery, installPreview } from '../fixtures/plugin-import'
import { presentation } from '../fixtures/plugin-presentation'
import { syntheticManifest } from '../fixtures/provider-marketplace'

const community = presentation({ sourceKind: 'community', review: { status: 'unreviewed', version: syntheticManifest.version, sha256: null } })
describe('untrusted import metadata and expiry validation', () => {
  it.each(['2026-02-30T00:00:00Z', '2026-02-29T00:00:00Z', '2026-01-01', '2026-01-01T00:00:00Z\n', '2026-01-01T00:00:00'])('rejects noncanonical or impossible expiry %j', expiresAt => {
    expect(isPluginSourceDiscovery(discovery({ expiresAt }))).toBe(false)
    expect(isPluginInstallPreview(installPreview({ expiresAt }), isPluginManifest)).toBe(false)
  })
  it.each(['2028-02-29T00:00:00Z', '2028-02-29T12:30:00.123+08:00'])('accepts real leap dates and explicit offsets %s', expiresAt => {
    expect(isPluginSourceDiscovery(discovery({ expiresAt }))).toBe(true)
    expect(isPluginInstallPreview(installPreview({ expiresAt }), isPluginManifest)).toBe(true)
  })
  it.each([
    { ...community, sourceKind: 'official' as const },
    { ...community, runtime: 'builtin' as const },
    { ...community, review: presentation().review },
  ])('rejects self-asserted repository/JSON trust %j', metadata => {
    expect(isPluginSourceDiscovery(discovery({ candidates: [{ pluginId: syntheticManifest.id, version: syntheticManifest.version, metadata }] }))).toBe(false)
    for (const kind of ['repository', 'json'] as const) {
      expect(isPluginInstallPreview(installPreview({ source: { kind }, metadata }), isPluginManifest)).toBe(false)
    }
  })
  it('retains trusted catalog presentation but external candidates stay community/unreviewed', () => {
    expect(isPluginInstallPreview(installPreview({ metadata: presentation() }), isPluginManifest)).toBe(true)
    expect(isPluginSourceDiscovery(discovery({ candidates: [{ pluginId: syntheticManifest.id, version: syntheticManifest.version, metadata: community }] }))).toBe(true)
    expect(isPluginInstallPreview(installPreview({ source: { kind: 'repository' }, metadata: community }), isPluginManifest)).toBe(true)
  })
})
describe('complete technical candidate equivalence', () => {
  it('round-trips host defaults and null omission without changing candidate identity', () => {
    const { targetFramework: _framework, apiVersion: _api, ...technical } = syntheticManifest
    const input = parsePluginJson(JSON.stringify({ ...technical, permissions: null, dependencies: null, minimumHostVersion: null,
      settings: [{ key: 'caption', label: '标题', type: 'string', defaultValue: null, choices: null }] }))
    const wire = { ...input, targetFramework: 'net10.0', apiVersion: 1, minimumHostVersion: undefined, permissions: [], dependencies: [],
      settings: [{ key: 'caption', label: '标题', type: 'string' as const, required: false }] }
    expect(input.targetFramework).toBe('net10.0'); expect(input.apiVersion).toBe(1)
    expect(samePreviewManifest(input, wire)).toBe(true)
    expect(samePreviewManifest({ ...input, settings: null }, { ...wire, settings: [] })).toBe(true)
  })
  it('normalizes property order and SHA letter case only, not permission meaning', () => {
    const input = { ...syntheticManifest, sha256: 'a'.repeat(64), permissions: ['send'] }
    expect(samePreviewManifest(input, { ...input, sha256: 'A'.repeat(64) })).toBe(true)
    expect(samePreviewManifest(input, { ...input, permissions: ['read'] })).toBe(false)
  })
})