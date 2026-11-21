import { describe, expect, it } from 'vitest'
import type {
  ZbaConfigurationReadResult,
  ZbaInstalledPlugins,
  ZbaManagedDescriptor,
  ZbaManagedRuntimeState,
  ZbaPluginCatalogItem,
  ZbaPluginPackageManifest
} from '../../src/api/zba'
import { buildDomainCatalog, isBuiltinFeaturePluginId, isSystemPackageId } from '../../src/features/capabilities/domain-catalog'

function pluginDescriptor(id: string, runtimeState: ZbaManagedRuntimeState = 4): ZbaManagedDescriptor {
  return {
    key: { kind: 'plugin', key: id },
    displayName: 'plugin',
    runtimeState,
    desiredEnabled: true,
    credentialConfigured: false,
    descriptorRevision: '3B25BF2C',
    actions: [
      { action: 1, supported: false, reasonCode: 'configuration_required' },
      { action: 2, supported: false, reasonCode: 'configuration_required' }
    ]
  }
}

function baseManifest(id: string, extra?: Partial<ZbaPluginPackageManifest>): ZbaPluginPackageManifest {
  return {
    id,
    version: '1.2.3',
    sha256: 'a'.repeat(64),
    downloadUrl: `https://example.com/${id}.zip`,
    apiVersion: 1,
    adoption: id.startsWith('system.') ? 'cold' : 'online',
    ...extra
  }
}

function installedPackage(id: string, overrides?: Partial<ZbaPluginCatalogItem>): ZbaPluginCatalogItem {
  return {
    manifest: baseManifest(id, overrides?.manifest),
    installedVersion: overrides?.installedVersion ?? '1.2.3',
    enabled: true,
    status: 'running',
    restartRequired: false,
    ...overrides
  } as ZbaPluginCatalogItem
}

describe('buildDomainCatalog', () => {
  const systemIds = [
    'system.model-services',
    'system.platform-qq-private',
    'system.platform-qq-official',
    'system.platform-feishu',
    'system.platform-local',
    'system.conversation',
    'system.agent',
    'system.mcp-tools',
    'system.health-reporting'
  ]

  const presetFeatureIds = [
    'preset.feature.token-quota',
    'preset.feature.music',
    'preset.feature.voice',
    'preset.feature.stickers',
    'preset.feature.poke',
    'preset.feature.vibes',
    'preset.feature.profiles',
    'preset.feature.websearch'
  ]

  it('partitions all 17 builtin packages, providers, drivers, and external plugins without leaking system.* into plugins', () => {
    const descriptors: ZbaManagedDescriptor[] = [
      ...systemIds.map(id => pluginDescriptor(id)),
      ...presetFeatureIds.map(id => pluginDescriptor(id)),
      pluginDescriptor('community.weather.py'),
      {
        key: { kind: 'provider', key: 'primary' },
        displayName: 'Main Model',
        runtimeState: 4,
        desiredEnabled: true,
        credentialConfigured: true,
        descriptorRevision: '3B25BF2C',
        actions: [{ action: 4, supported: true, reasonCode: '' }]
      },
      {
        key: { kind: 'driver', key: { platformId: 'qq.private', accountScope: 'acc-1', instanceId: 'default' } },
        displayName: 'QQ Private',
        runtimeState: 4,
        desiredEnabled: true,
        credentialConfigured: false,
        descriptorRevision: '3B25BF2C',
        actions: [
          { action: 1, supported: true, reasonCode: '' },
          { action: 2, supported: true, reasonCode: '' }
        ]
      }
    ]

    const installed: ZbaInstalledPlugins = {
      plugins: [
        ...systemIds.map(id => installedPackage(id)),
        ...presetFeatureIds.map(id => installedPackage(id, {
          installedVersion: '0.9.0',
          manifest: baseManifest(id, {
            version: '0.9.0',
            adoption: 'online',
            ...(id === 'preset.feature.token-quota' || id === 'preset.feature.stickers'
              ? {
                  ui: {
                    contractVersion: 1,
                    entry: 'index.html',
                    title: 'UI',
                    assets: [{ path: 'index.html', sha256: 'a'.repeat(64), contentType: 'text/html; charset=utf-8' }]
                  }
                }
              : {})
          })
        })),
        installedPackage('community.weather.py', {
          name: '天气查询扩展',
          summary: '基于 Python 的天气查询插件',
          installedVersion: '2.0.1',
          manifest: baseManifest('community.weather.py', {
            version: '2.0.1',
            apiVersion: 2,
            adoption: 'online',
            runtime: { kind: 'python', entrypoint: 'main.py' } as unknown as ZbaPluginPackageManifest['runtime']
          })
        })
      ]
    } as unknown as ZbaInstalledPlugins

    const config = {
      view: {
        registered: {
          providers: [
            { key: 'primary', name: { display: '主力推理供应商', value: '主力推理供应商' } }
          ]
        },
        platform: {
          platformPolicies: {
            value: [
              {
                target: { kind: 'instance', key: { platformId: 'qq.private', accountScope: 'acc-1', instanceId: 'default' } }
              }
            ]
          }
        }
      }
    } as unknown as ZbaConfigurationReadResult

    const catalog = buildDomainCatalog(descriptors, config, installed)

    // 1. Model Providers
    expect(catalog.providers).toHaveLength(1)
    expect(catalog.providers[0]!.title).toBe('主力推理供应商')
    expect(catalog.modelServicesPackage?.id).toBe('system.model-services')
    expect(catalog.modelServicesPackage?.title).toBe('模型路由与服务引擎')

    // 2. Message Platforms merged with driverPackage + policy
    expect(catalog.platforms).toHaveLength(1)
    expect(catalog.platforms[0]!.platformId).toBe('qq.private')
    expect(catalog.platforms[0]!.driverPackage?.id).toBe('system.platform-qq-private')
    expect(catalog.platforms[0]!.policy).toBeDefined()

    // 3. System packages: all 9 present with Chinese display titles
    expect(catalog.systemPackages).toHaveLength(9)
    for (const pkg of catalog.systemPackages) {
      expect(isSystemPackageId(pkg.id)).toBe(true)
      expect(pkg.title).not.toMatch(/^(model-services|platform-|plugin)$/)
    }

    // 4. Plugins: strictly excludes system.*, contains 8 preset features + 1 external Python plugin
    expect(catalog.plugins).toHaveLength(9)
    expect(catalog.plugins.some(item => isSystemPackageId(item.id))).toBe(false)

    const quota = catalog.plugins.find(item => item.id === 'preset.feature.token-quota')!
    expect(quota.origin).toBe('builtin-feature')
    expect(quota.hasRuntimeUi).toBe(true)
    expect(quota.version).toBe('0.9.0')
    expect(quota.adoption).toBe('online')

    const stickers = catalog.plugins.find(item => item.id === 'preset.feature.stickers')!
    expect(stickers.hasRuntimeUi).toBe(true)

    const weather = catalog.plugins.find(item => item.id === 'community.weather.py')!
    expect(weather.origin).toBe('extension')
    expect(weather.runtime).toBe('python')
    expect(weather.title).toBe('天气查询扩展')
    expect(weather.version).toBe('2.0.1')
    expect(isBuiltinFeaturePluginId(weather.id)).toBe(false)
  })
})
