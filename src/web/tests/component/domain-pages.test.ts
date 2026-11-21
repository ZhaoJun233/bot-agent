import { afterEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import {
  ZbaClient,
  ZbaRequestError,
  type ZbaConfigurationReadResult,
  type ZbaInstalledPlugins,
  type ZbaManagedDescriptor,
  type ZbaTraceSummary
} from '../../src/api/zba'
import ProvidersPage from '../../src/features/providers/ProvidersPage.vue'
import PlatformsPage from '../../src/features/platforms/PlatformsPage.vue'
import PluginsPage from '../../src/features/plugins/PluginsPage.vue'
import SystemConfigurationPage from '../../src/features/configuration/SystemConfigurationPage.vue'
import TracesPage from '../../src/features/traces/TracesPage.vue'
import RuntimeLogsPage from '../../src/features/logs/RuntimeLogsPage.vue'
import { beginOperation, clearPendingOperations } from '../../src/shared/sync/pending-operations'
import configFixture from '../fixtures/configuration-read-editable-result.json'

const mounted: ReturnType<typeof mount>[] = []
afterEach(() => {
  clearPendingOperations()
  mounted.splice(0).forEach(w => w.unmount())
})

const sampleDescriptors: ZbaManagedDescriptor[] = [
  {
    key: { kind: 'provider', key: 'primary' },
    displayName: 'Primary Model',
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
  },
  {
    key: { kind: 'plugin', key: 'system.model-services' },
    displayName: 'plugin',
    runtimeState: 4,
    desiredEnabled: true,
    credentialConfigured: false,
    descriptorRevision: '3B25BF2C',
    actions: [
      { action: 1, supported: false, reasonCode: 'configuration_required' },
      { action: 2, supported: false, reasonCode: 'configuration_required' }
    ]
  },
  {
    key: { kind: 'plugin', key: 'system.platform-qq-private' },
    displayName: 'plugin',
    runtimeState: 4,
    desiredEnabled: true,
    credentialConfigured: false,
    descriptorRevision: '3B25BF2C',
    actions: []
  },
  {
    key: { kind: 'plugin', key: 'preset.feature.token-quota' },
    displayName: 'plugin',
    runtimeState: 4,
    desiredEnabled: true,
    credentialConfigured: false,
    descriptorRevision: '3B25BF2C',
    actions: [
      { action: 1, supported: false, reasonCode: 'configuration_required' },
      { action: 2, supported: false, reasonCode: 'configuration_required' }
    ]
  }
]

const sampleInstalled = {
  plugins: [
    {
      manifest: { id: 'system.model-services', version: '1.0.0', sha256: 'a'.repeat(64), downloadUrl: 'https://example.com/a.zip', apiVersion: 1, adoption: 'cold' },
      installedVersion: '1.0.0',
      enabled: true,
      status: 'running',
      restartRequired: false
    },
    {
      manifest: { id: 'system.platform-qq-private', version: '1.0.0', sha256: 'b'.repeat(64), downloadUrl: 'https://example.com/b.zip', apiVersion: 1, adoption: 'cold' },
      installedVersion: '1.0.0',
      enabled: true,
      status: 'running',
      restartRequired: false
    },
    {
      manifest: {
        id: 'preset.feature.token-quota',
        version: '0.8.0',
        sha256: 'c'.repeat(64),
        downloadUrl: 'https://example.com/c.zip',
        apiVersion: 1,
        adoption: 'online',
        ui: { entry: 'index.html', title: 'Token 配额', slot: 'settings', assets: ['index.html'] }
      },
      installedVersion: '0.8.0',
      enabled: true,
      status: 'running',
      restartRequired: false
    }
  ]
} as unknown as ZbaInstalledPlugins

function createDomainClient(): ZbaClient {
  const client = new ZbaClient({ token: 'synthetic-domain-token' })
  vi.spyOn(client, 'listCapabilities').mockResolvedValue({
    code: 0,
    items: sampleDescriptors,
    nextCursor: null,
    descriptorRevision: '3B25BF2C'
  })
  vi.spyOn(client, 'installedPlugins').mockResolvedValue(sampleInstalled)
  vi.spyOn(client, 'readConfiguration').mockResolvedValue(
    structuredClone(configFixture) as unknown as ZbaConfigurationReadResult
  )
  vi.spyOn(client, 'listEvents').mockResolvedValue({
    items: [],
    nextSequence: 1,
    resyncRequired: false,
    generatedAt: '2026-11-20T00:00:00Z'
  })
  return client
}

describe('Domain-split pages (Providers / Platforms / Plugins / SystemConfiguration)', () => {
  it('ProvidersPage displays model providers, API URLs, live credential source, and pure routing drawer', async () => {
    const client = createDomainClient()
    const wrapper = mount(ProvidersPage, {
      props: { client, mode: 'admin', writesEnabled: true, active: true },
      attachTo: document.body,
      global: { stubs: { teleport: true } }
    })
    mounted.push(wrapper)
    await flushPromises()
    const text = wrapper.text()
    expect(text).toContain('模型供应商')
    expect(text).toContain('路由与采样参数')
    expect(text).toContain('新增模型供应商')
    expect(text).toContain('Primary Model')
    expect(text).toContain('主供应商')
    expect(text).toContain('API 地址')
    expect(text).toContain('路由引擎')
    expect(text).toContain('v1.0.0')

    // 打开路由与采样参数抽屉：不再混入 modelBaseUrl、model、秘密槽位 0 与旧供应商表
    await wrapper.get('[data-testid="providers-routing-settings"]').trigger('click')
    await flushPromises()
    const dialog = wrapper.get('[data-testid="ability-configuration-dialog"]')
    expect(dialog.find('[data-testid="config-field-model"]').exists()).toBe(false)
    expect(dialog.find('[data-testid="config-field-modelBaseUrl"]').exists()).toBe(false)
    expect(dialog.find('[data-testid="config-secrets"]').exists()).toBe(false)
    expect(dialog.find('[data-testid="config-nav-registered"]').exists()).toBe(false)
    expect(dialog.find('[data-testid="config-providers"]').exists()).toBe(false)
  })

  it('PlatformsPage merges driver with system.platform-* and opens decoupled per-driver settings drawers', async () => {
    const fullConfig = structuredClone(configFixture) as unknown as ZbaConfigurationReadResult
    const makeField = (id: number, value: unknown) => ({
      visibility: 1 as const,
      display: String(value),
      value,
      userMode: 2 as const,
      effectiveSource: 1 as const,
      applyState: 1 as const,
      rule: { id, effect: 0 as const, canClear: true, editable: true, legacyNeedsReview: false }
    })
    Object.assign(fullConfig.view!.platform, {
      feishuEnabled: makeField(56, false),
      feishuAppId: makeField(55, 'cli_test'),
      officialEnabled: makeField(91, false),
      localChannelIds: makeField(67, ''),
      privateChatEnabled: makeField(104, true)
    })
    const templatePolicy = fullConfig.view!.platform.platformPolicies.value![0]!
    const qqPolicy = structuredClone(templatePolicy)
    qqPolicy.target = { kind: 'instance', key: { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' } }
    const feishuPolicy = structuredClone(templatePolicy)
    feishuPolicy.target = { kind: 'instance', key: { platformId: 'feishu', accountScope: 'default', instanceId: 'feishu-main' } }
    fullConfig.view!.platform.platformPolicies.value = [qqPolicy, feishuPolicy]

    const client = new ZbaClient({ token: 'synthetic-platform-token' })
    vi.spyOn(client, 'listCapabilities').mockResolvedValue({
      code: 0,
      items: [
        {
          key: { kind: 'driver', key: { platformId: 'qq.private', accountScope: 'legacy', instanceId: '' } },
          displayName: 'QQ Private',
          runtimeState: 4,
          desiredEnabled: true,
          credentialConfigured: false,
          descriptorRevision: '3B25BF2C',
          actions: [{ action: 3, supported: true, reasonCode: '' }]
        },
        {
          key: { kind: 'driver', key: { platformId: 'feishu', accountScope: 'default', instanceId: 'feishu-main' } },
          displayName: 'Feishu',
          runtimeState: 1,
          desiredEnabled: false,
          credentialConfigured: false,
          descriptorRevision: '3B25BF2C',
          actions: []
        },
        {
          key: { kind: 'driver', key: { platformId: 'qq.official', accountScope: 'legacy', instanceId: '' } },
          displayName: 'QQ Official',
          runtimeState: 1,
          desiredEnabled: false,
          credentialConfigured: false,
          descriptorRevision: '3B25BF2C',
          actions: []
        },
        {
          key: { kind: 'plugin', key: 'system.platform-qq-private' },
          displayName: 'plugin',
          runtimeState: 4,
          desiredEnabled: true,
          credentialConfigured: false,
          descriptorRevision: '3B25BF2C',
          actions: []
        },
        {
          key: { kind: 'plugin', key: 'system.platform-feishu' },
          displayName: 'plugin',
          runtimeState: 4,
          desiredEnabled: true,
          credentialConfigured: false,
          descriptorRevision: '3B25BF2C',
          actions: []
        }
      ],
      nextCursor: null,
      descriptorRevision: '3B25BF2C'
    })
    vi.spyOn(client, 'installedPlugins').mockResolvedValue(sampleInstalled)
    vi.spyOn(client, 'readConfiguration').mockResolvedValue(fullConfig)
    vi.spyOn(client, 'listEvents').mockResolvedValue({
      items: [],
      nextSequence: 1,
      resyncRequired: false,
      generatedAt: '2026-11-20T00:00:00Z'
    })

    // Seed an unconfirmed driver configuration operation on local to verify recovery banner matching
    beginOperation(client, { key: 'cfg1:synthetic-gen:1:synthetic-nonce', kind: 'configuration', target: 'config:system.platform-local' })

    const wrapper = mount(PlatformsPage, {
      props: { client, mode: 'admin', writesEnabled: true, active: true },
      attachTo: document.body
    })
    mounted.push(wrapper)
    await flushPromises()
    const text = wrapper.text()
    expect(text).toContain('消息平台')
    expect(text).toContain('QQ 私域')
    expect(text).toContain('OneBot')
    expect(text).toContain('legacy（默认实例）')
    expect(text).not.toContain('3B25BF2C')
    // No global aggregate button in header when all platform configs belong to displayed drivers
    expect(wrapper.find('[data-testid="platforms-open-settings"]').exists()).toBe(false)
    expect(wrapper.find('[data-testid="platforms-open-fallback-settings"]').exists()).toBe(false)
    // Only QQ Official declares messaging inspection
    expect(wrapper.find('[data-testid="cap-messaging-driver:qq.official:legacy:"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="cap-messaging-driver:feishu:default:feishu-main"]').exists()).toBe(false)
    // Recovery banner matches per-driver config target
    expect(wrapper.get('[data-testid="platforms-recovery-banner"]').text()).toContain('本地调试通道')

    // Click Feishu card -> only Feishu fields, secrets, and policies appear
    await wrapper.get('[data-testid="platform-policy-feishu"]').trigger('click')
    await flushPromises()
    let dialog = document.body.querySelector('[data-testid="ability-configuration-dialog"]')!
    expect(dialog).not.toBeNull()
    expect(dialog.textContent).toContain('飞书机器人通道')
    expect(dialog.querySelector('[data-testid="config-field-feishuEnabled"]')).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-field-whitelistGroups"]')).toBeNull()
    expect(dialog.querySelector('[data-testid="config-secrets"]')?.textContent).toContain('飞书 App Secret')
    expect(dialog.querySelector('[data-testid="config-policy-instance:feishu:default:feishu-main"]')).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-policy-instance:qq.private:legacy:"]')).toBeNull()
    dialog.querySelector<HTMLButtonElement>('[data-testid="ability-configuration-dialog-close"]')!.click()
    await flushPromises()

    // Click QQ Private card -> only QQ Private fields & policies appear, no Feishu secrets
    await wrapper.get('[data-testid="platform-policy-qq.private"]').trigger('click')
    await flushPromises()
    dialog = document.body.querySelector('[data-testid="ability-configuration-dialog"]')!
    expect(dialog).not.toBeNull()
    expect(dialog.textContent).toContain('QQ 私域通道')
    expect(dialog.querySelector('[data-testid="config-field-whitelistGroups"]')).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-field-feishuEnabled"]')).toBeNull()
    expect(dialog.querySelector('[data-testid="config-secrets"]')).toBeNull()
    expect(dialog.querySelector('[data-testid="config-policy-instance:qq.private:legacy:"]')).not.toBeNull()
    expect(dialog.querySelector('[data-testid="config-policy-instance:feishu:default:feishu-main"]')).toBeNull()
  })

  it('PluginsPage strictly excludes system.*, eliminates dead noise, and offers direct UI entry for runtime UI plugins', async () => {
    const client = createDomainClient()
    const wrapper = mount(PluginsPage, {
      props: { client, mode: 'admin', writesEnabled: true, active: true }
    })
    mounted.push(wrapper)
    await flushPromises()
    const text = wrapper.text()
    expect(text).toContain('插件中心')
    expect(text).toContain('Token 配额')
    expect(text).toContain('v0.8.0')
    expect(text).not.toContain('system.model-services')
    expect(text).not.toContain('system.platform-qq-private')
    expect(text).not.toContain('没有可用动作')
    expect(text).not.toContain('3B25BF2C')
    expect(wrapper.find('[data-testid="plugin-open-ui-preset.feature.token-quota"]').exists()).toBe(true)
  })

  it('SystemConfigurationPage exposes 5 sections including read-only system packages table with domain jump links', async () => {
    const client = createDomainClient()
    const wrapper = mount(SystemConfigurationPage, {
      props: { client, mode: 'admin', writesEnabled: true, active: true }
    })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.find('[data-testid="sysconfig-tab-basic"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="sysconfig-tab-conversation"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="sysconfig-tab-agent"]').exists()).toBe(true)
    expect(wrapper.find('[data-testid="sysconfig-tab-reporting"]').exists()).toBe(true)
    await wrapper.get('[data-testid="sysconfig-tab-system-packages"]').trigger('click')
    await flushPromises()
    const panel = wrapper.get('[data-testid="system-packages-panel"]')
    expect(panel.text()).toContain('system.model-services')
    expect(panel.text()).toContain('system.platform-qq-private')
    await panel.get('[data-testid="syspkg-jump-system.model-services"]').trigger('click')
    expect(wrapper.emitted('open-domain')?.[0]).toEqual(['providers'])
    await panel.get('[data-testid="syspkg-jump-system.platform-qq-private"]').trigger('click')
    expect(wrapper.emitted('open-domain')?.[1]).toEqual(['platforms', 'system.platform-qq-private'])
  })
})

describe('Phase 4 polish regressions (TracesPage & RuntimeLogsPage)', () => {
  it('TracesPage opens node breakdown drawer and emits auth-expired on HTTP 401', async () => {
    const trace: ZbaTraceSummary = {
      runId: 'run-1',
      startedAt: '2026-11-20T12:00:00Z',
      totalMs: 420,
      outcome: 'replied',
      promptTokens: 1000,
      completionTokens: 200,
      cachedPromptTokens: 400,
      cacheWriteTokens: 0,
      cacheUsageUnknown: false,
      fallbackHops: 0,
      nodes: [
        { kind: 'Model', status: 'ok', durationMs: 380, toolId: null, reasonCode: null, count: null }
      ]
    }
    const client = new ZbaClient({ token: 'synthetic-trace-token' })
    const listTraces = vi.spyOn(client, 'listTraces').mockResolvedValueOnce({
      items: [trace],
      cache: {
        doneTurns: 1,
        activeTurns: 0,
        capacity: 50,
        cachedPromptTokens: 400,
        cacheWriteTokens: 0,
        cacheUnknownTurns: 0
      }
    })
    const wrapper = mount(TracesPage, { props: { client, active: true }, attachTo: document.body })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.get('[data-testid="trace-cache-hit"]').text()).toBe('400')
    expect(wrapper.text()).toContain('40%')
    await wrapper.get('[data-testid="trace-nodes-open"]').trigger('click')
    await flushPromises()
    expect(document.body.textContent).toContain('Model')
    expect(document.body.textContent).toContain('380 ms')

    // HTTP 401 emits auth-expired
    listTraces.mockRejectedValueOnce(new ZbaRequestError(401, 'unauthorized', 'expired'))
    await wrapper.get('[data-testid="traces-reload"]').trigger('click')
    await flushPromises()
    expect(wrapper.emitted('auth-expired')).toHaveLength(1)
  })

  it('RuntimeLogsPage renders working sync button, word-wrap toggle, and no tabindex trap on log messages', async () => {
    const client = new ZbaClient({ token: 'synthetic-logs-token' })
    vi.spyOn(client, 'listRuntimeLogFeed').mockResolvedValue({
      capacity: 600,
      cursor: 'cursor-1',
      hasMore: false,
      items: [
        {
          id: 'a'.repeat(32) + ':1',
          sequence: '1',
          time: 1793773800001,
          level: 'info',
          source: 'Agent',
          message: 'Safe synthetic line',
          redacted: true
        }
      ]
    })
    const wrapper = mount(RuntimeLogsPage, { props: { client, active: true } })
    mounted.push(wrapper)
    await flushPromises()
    expect(wrapper.find('[data-testid="logs-sync-now"]').exists()).toBe(true)
    const wrapBtn = wrapper.get('[data-testid="logs-wrap-toggle"]')
    expect(wrapBtn.attributes('aria-pressed')).toBe('false')
    await wrapBtn.trigger('click')
    expect(wrapBtn.attributes('aria-pressed')).toBe('true')
    expect(wrapper.get('.log-message').attributes('tabindex')).toBeUndefined()
  })
})
