import { describe, expect, it } from 'vitest'
import { ZbaClient } from '../../src/api/zba'
import { isPluginSourceDiscovery } from '../../src/api/plugin-import'
import { parsePluginJson } from '../../src/features/capabilities/plugin-json-import'
import { discovery, installPreview } from '../fixtures/plugin-import'
import { syntheticManifest } from '../fixtures/provider-marketplace'

function transport(body: unknown, status = 200) {
  const calls: Array<{ url: string; init: RequestInit }> = []
  const fetchImpl: typeof fetch = async (input, init = {}) => { calls.push({ url: String(input), init }); return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }) }
  return { client: new ZbaClient({ baseUrl: '/zba/v1', fetchImpl }), calls }
}
describe('read-only plugin discovery and preview wire', () => {
  it('discovers multiple exact candidates without fetching their URLs or carrying write acknowledgements', async () => {
    const { client, calls } = transport(discovery()); const sourceUrl = 'https://github.com/example-org/example-plugin'
    expect((await client.discoverPluginSource(sourceUrl)).candidates).toHaveLength(3)
    expect(calls).toHaveLength(1); expect(calls[0]?.url).toBe('/zba/v1/plugins/source/discover')
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ sourceUrl })
  })
  it('previews repository selection without serializing a client manifest', async () => {
    const { client, calls } = transport(installPreview({ source: { kind: 'repository', url: 'https://github.com/example-org/example-plugin', revision: 'b'.repeat(40) } }))
    const selection = { origin: 'repository' as const, discoveryId: 'discovery-example', pluginId: syntheticManifest.id, version: '1.0.0', expectedRevision: 'rev-1' }
    expect((await client.previewPlugin(selection)).enabledAfterInstall).toBe(false)
    expect(calls).toHaveLength(1); expect(calls[0]?.url).toBe('/zba/v1/plugins/preview'); expect(JSON.parse(String(calls[0]?.init.body))).toEqual(selection)
  })
  it.each(['catalog', 'json'] as const)('previews %s with technical manifest and no trust confirmation', async origin => {
    const { client, calls } = transport(installPreview({ source: { kind: origin } }))
    await client.previewPlugin({ origin, manifest: syntheticManifest, expectedRevision: 'rev-1' })
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ origin, manifest: syntheticManifest, expectedRevision: 'rev-1' })
  })
  it.each([
    { enabledAfterInstall: true }, { previewId: '' }, { expiresAt: 'not-a-date' }, { source: { kind: 'builtin' } }, { installedVersion: null },
    { checks: [{ code: 'blocked', status: 'blocked', message: 'blocked' }], canInstall: true }, { checks: [{ code: 'x', status: 'unknown', message: 'x' }] }
  ])('rejects malformed or contradictory preview %j', async invalid => {
    const { client } = transport({ ...installPreview(), ...invalid })
    await expect(client.previewPlugin({ origin: 'catalog', manifest: syntheticManifest, expectedRevision: 'rev-1' })).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('rejects duplicate and oversized candidate lists', () => {
    const candidate = discovery().candidates[0]!
    expect(isPluginSourceDiscovery(discovery({ candidates: [candidate, candidate] }))).toBe(false)
    expect(isPluginSourceDiscovery(discovery({ candidates: Array.from({ length: 1025 }, (_, i) => ({ ...candidate, version: String(i) })) }))).toBe(false)
    expect(isPluginSourceDiscovery(discovery({ candidates: Array.from({ length: 1024 }, (_, i) => ({ ...candidate, version: String(i) })) }))).toBe(true)
  })
  it('attaches previewId to the exact acknowledged install request', async () => {
    const { client, calls } = transport({ revision: 'rev-2', operation: { id: 'operation-example', pluginId: syntheticManifest.id, kind: 'install', status: 'installed', restartRequired: false } })
    await client.installPlugin({ manifest: syntheticManifest, expectedRevision: 'rev-1', previewId: 'preview-example', trustAcknowledged: true, idempotencyKey: 'install-example' })
    expect(JSON.parse(String(calls[0]?.init.body))).toMatchObject({ previewId: 'preview-example', manifest: syntheticManifest, trustAcknowledged: true })
  })
  it('does not retry legacy install after preview-required rejection', async () => {
    const { client, calls } = transport({ error: { code: 'plugin_preview_required', message: 'Preview required' } }, 409)
    await expect(client.installPlugin({ manifest: syntheticManifest, expectedRevision: 'rev-1', trustAcknowledged: true, idempotencyKey: 'install-example' })).rejects.toMatchObject({ code: 'plugin_preview_required' })
    expect(calls).toHaveLength(1)
  })
})
describe('JSON technical manifest boundary', () => {
  it('accepts the complete technical manifest including entryType', () => { expect(parsePluginJson(JSON.stringify(syntheticManifest))).toEqual(syntheticManifest) })
  it.each(['metadata', 'runtime', 'sourceKind', 'review', 'previewId', 'trustAcknowledged'])('rejects injected %s', key => {
    expect(() => parsePluginJson(JSON.stringify({ ...syntheticManifest, [key]: true }))).toThrow('非技术字段')
  })
  it('rejects malformed JSON, oversized content and malformed dependency', () => {
    expect(() => parsePluginJson('{')).toThrow('JSON'); expect(() => parsePluginJson(' '.repeat(2 * 1024 * 1024 + 1))).toThrow('2 MiB')
    expect(() => parsePluginJson(JSON.stringify({ ...syntheticManifest, dependencies: [{ id: 'example.dep', version: 3 }] }))).toThrow('清单')
  })
})