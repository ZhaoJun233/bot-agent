import { describe, expect, it } from 'vitest'
import { ZbaClient } from '../../src/api/zba'
import { mergePluginCatalog } from '../../src/features/capabilities/plugin-marketplace'
import { filterPluginCatalog, pluginAcquisitionUnavailable, pluginPackageStatus } from '../../src/features/capabilities/plugin-catalog'
import { syntheticManifest } from '../fixtures/provider-marketplace'

const failure = { id: syntheticManifest.id, code: 'plugin_metadata_unreadable', fingerprint: 'sha256:' + 'a'.repeat(64) }
const row = { manifest: syntheticManifest, status: 'metadata_unreadable', enabled: false, restartRequired: false }
const body = { revision: 'r1', plugins: [row], source: 'https://example.com/index.json', trustNotice: 'synthetic', categories: [], metadataFailures: [failure] }
function client(response: unknown): ZbaClient {
  return new ZbaClient({ fetchImpl: async () => new Response(JSON.stringify(response), { headers: { 'Content-Type': 'application/json' } }) })
}

describe('unreadable plugin directory observations', () => {
  it('accepts safe observations without discarding the usable catalog', async () => {
    expect((await client(body).pluginCatalog()).metadataFailures).toEqual([failure])
    expect((await client(body).installedPlugins()).metadataFailures).toEqual([failure])
    expect((await client({ ...body, metadataFailures: undefined }).installedPlugins()).plugins).toHaveLength(1)
  })

  it.each([
    { metadataFailures: [{ ...failure, id: '../private-path' }] },
    { metadataFailures: [{ ...failure, code: 'arbitrary parser error' }] },
    { metadataFailures: [{ ...failure, fingerprint: 'raw metadata text' }] },
    { metadataFailures: null },
  ])('rejects malformed directory diagnostics: %j', async ({ metadataFailures }) => {
    await expect(client({ ...body, metadataFailures }).pluginCatalog()).rejects.toMatchObject({ code: 'invalid_json' })
    await expect(client({ ...body, metadataFailures }).installedPlugins()).rejects.toMatchObject({ code: 'invalid_json' })
  })

  it('does not reclassify a damaged installed package as available for installation', () => {
    const rows = mergePluginCatalog([{ ...row, status: 'available', installedVersion: '0.9.0', enabled: true }],
      { revision: 'r1', plugins: [], metadataFailures: [failure, { ...failure, id: 'unlisted.fault' }] }, [])
    expect(rows).toHaveLength(1)
    expect(rows[0]?.installedVersion).toBeUndefined()
    expect(rows[0]?.enabled).toBeUndefined()
    expect(pluginAcquisitionUnavailable(rows[0]!)).toBe(true)
    expect(pluginPackageStatus(rows[0]!)).toContain('安装记录不可读')
    expect(filterPluginCatalog(rows, '', 'all', { state: 'available' })).toEqual([])
  })
})
