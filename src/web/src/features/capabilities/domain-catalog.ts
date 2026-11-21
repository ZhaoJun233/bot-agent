import type {
  ZbaConfigurationReadResult,
  ZbaInstalledPlugins,
  ZbaManagedDescriptor,
  ZbaPlatformPolicyEntryView,
  ZbaPluginCatalogItem,
  ZbaProviderConfigurationView
} from '../../api/zba'
import {
  capabilityTitle,
  isGenericName,
  platformIcon,
  platformName,
  pluginIcon,
  pluginName,
  pluginSummary,
  SYSTEM_PACKAGE_META
} from '../../shared/friendly'
import type { AppIconName } from '../../shared/ui/AppIcon.vue'
import { settingsFor } from './plugin-settings'

export interface SystemPackageModel {
  id: string
  title: string
  summary: string
  domain: 'provider' | 'platform' | 'system'
  platformId?: string
  configScope?: string
  icon: AppIconName
  descriptor: ZbaManagedDescriptor | null
  installed: ZbaPluginCatalogItem | null
  version: string | null
  adoption: 'cold' | 'online'
  runtimeState: number
  desiredEnabled: boolean | null
  restartRequired: boolean
  errorCode?: string | null
}

export interface ProviderCardModel {
  key: string
  title: string
  descriptor: ZbaManagedDescriptor
  configView: ZbaProviderConfigurationView | undefined
  icon: AppIconName
}

export interface PlatformChannelModel {
  platformId: string
  accountScope: string
  instanceId: string
  identity: string
  title: string
  summary: string
  icon: AppIconName
  descriptor: ZbaManagedDescriptor
  driverPackage: SystemPackageModel | null
  policy: ZbaPlatformPolicyEntryView | undefined
}

export type PluginOriginKind = 'builtin-feature' | 'extension'

export interface PluginCardModel {
  id: string
  title: string
  summary: string
  origin: PluginOriginKind
  runtime: 'dotnet' | 'python'
  icon: AppIconName
  descriptor: ZbaManagedDescriptor
  installed: ZbaPluginCatalogItem | null
  version: string | null
  adoption: 'cold' | 'online'
  hasRuntimeUi: boolean
  restartRequired: boolean
  category: string
}

export interface DomainCatalogView {
  providers: ProviderCardModel[]
  modelServicesPackage: SystemPackageModel | null
  platforms: PlatformChannelModel[]
  plugins: PluginCardModel[]
  systemPackages: SystemPackageModel[]
  otherCapabilities: ZbaManagedDescriptor[]
}

export function isSystemPackageId(id: string): boolean {
  return id.startsWith('system.')
}

export function isBuiltinFeaturePluginId(id: string): boolean {
  return id.startsWith('preset.feature.')
}

export function keyIdentity(key: ZbaManagedDescriptor['key']): string {
  switch (key.kind) {
    case 'driver':
      return `driver:${key.key.platformId}:${key.key.accountScope}:${key.key.instanceId}`
    case 'device':
      return `device:${key.key.deviceId}`
    case 'plugin':
      return `plugin:${key.key}`
    case 'provider':
      return `provider:${key.key}`
    case 'mcp':
      return `mcp:${key.key}`
  }
}

export function keySlug(key: ZbaManagedDescriptor['key']): string {
  switch (key.kind) {
    case 'driver':
      return `driver:${key.key.platformId}:${key.key.accountScope}:${key.key.instanceId}`
    case 'device':
      return `device:${key.key.deviceId}`
    case 'plugin':
      return `plugin:${key.key}`
    case 'provider':
      return `provider:${key.key}`
    case 'mcp':
      return `mcp:${key.key}`
  }
}

export function runtimeLabel(state: number): string {
  const labels: Record<number, string> = {
    0: '未知', 1: '未加载', 2: '已停用', 3: '启动中',
    4: '运行中', 5: '停止中', 6: '已断开', 7: '故障'
  }
  return labels[state] ?? '未知'
}

export function runtimeTone(state: number): 'ok' | 'warn' | 'bad' | 'neutral' {
  switch (state) {
    case 4: return 'ok'
    case 6:
    case 7: return 'bad'
    case 0:
    case 3:
    case 5: return 'warn'
    default: return 'neutral'
  }
}

function resolvePackageTitle(id: string, descriptor: ZbaManagedDescriptor | null, installed: ZbaPluginCatalogItem | null): string {
  const sys = SYSTEM_PACKAGE_META[id]
  if (sys) return sys.title
  const spec = settingsFor(id)
  if (spec) return spec.title
  const installedName = installed?.metadata?.name || installed?.name
  if (installedName && !isGenericName(installedName)) return installedName
  if (descriptor && !isGenericName(descriptor.displayName)) return descriptor.displayName
  return pluginName(id)
}

function resolvePackageSummary(id: string, installed: ZbaPluginCatalogItem | null): string {
  const installedSummary = installed?.metadata?.summary || installed?.summary
  if (installedSummary && installedSummary.trim().length > 0) return installedSummary.trim()
  const known = pluginSummary(id)
  if (known) return known
  return '扩展插件能力包'
}

function resolveRuntime(installed: ZbaPluginCatalogItem | null): 'dotnet' | 'python' {
  const manifest = installed?.manifest
  if (manifest?.apiVersion === 2 || Boolean(manifest?.runtime) || installed?.metadata?.runtime === 'python') {
    return 'python'
  }
  return 'dotnet'
}

function buildSystemPackageModel(
  id: string,
  descriptor: ZbaManagedDescriptor | null,
  installed: ZbaPluginCatalogItem | null
): SystemPackageModel {
  const meta = SYSTEM_PACKAGE_META[id]
  return {
    id,
    title: resolvePackageTitle(id, descriptor, installed),
    summary: resolvePackageSummary(id, installed),
    domain: meta?.domain ?? 'system',
    platformId: meta?.platformId,
    configScope: meta?.configScope,
    icon: meta?.icon ?? 'cpu',
    descriptor,
    installed,
    version: installed?.installedVersion || installed?.manifest.version || null,
    adoption: installed?.manifest.adoption === 'online' ? 'online' : 'cold',
    runtimeState: descriptor?.runtimeState ?? (installed?.status === 'running' ? 4 : 0),
    desiredEnabled: descriptor?.desiredEnabled ?? installed?.enabled ?? null,
    restartRequired: Boolean(installed?.restartRequired || descriptor?.errorCode === 'plugin_restart_required'),
    errorCode: descriptor?.errorCode ?? null
  }
}

/**
 * 将能力目录、配置视图与已安装包清单聚合为清晰的四大领域模型：
 * 1) 模型供应商 (providers + system.model-services)
 * 2) 消息平台 (platforms = driver + 对应 system.platform-* + platformPolicies)
 * 3) 插件中心 (plugins = preset.feature.* + 外部扩展，严格排除 system.*)
 * 4) 系统核心包 (systemPackages = 9 个 system.* 包元信息)
 */
export function buildDomainCatalog(
  descriptors: readonly ZbaManagedDescriptor[],
  configuration: ZbaConfigurationReadResult | null,
  installedPlugins: ZbaInstalledPlugins | null
): DomainCatalogView {
  const installedById = new Map<string, ZbaPluginCatalogItem>()
  for (const item of installedPlugins?.plugins ?? []) {
    installedById.set(item.manifest.id, item)
  }

  const providerViews = configuration?.view?.registered?.providers ?? []
  const policyViews: ZbaPlatformPolicyEntryView[] = configuration?.view?.platform?.platformPolicies?.value ?? []

  const systemPackageIds = new Set<string>()
  const pluginDescriptorById = new Map<string, ZbaManagedDescriptor>()
  const providers: ProviderCardModel[] = []
  const driverDescriptors: ZbaManagedDescriptor[] = []
  const plugins: PluginCardModel[] = []
  const otherCapabilities: ZbaManagedDescriptor[] = []

  for (const descriptor of descriptors) {
    if (descriptor.key.kind === 'provider') {
      const configView = providerViews.find(item => item.key === descriptor.key.key)
      const displayName = configView?.name.display || capabilityTitle(descriptor.displayName, descriptor.key)
      providers.push({
        key: descriptor.key.key,
        title: displayName,
        descriptor,
        configView,
        icon: 'provider'
      })
    } else if (descriptor.key.kind === 'driver') {
      driverDescriptors.push(descriptor)
    } else if (descriptor.key.kind === 'plugin') {
      const id = descriptor.key.key
      pluginDescriptorById.set(id, descriptor)
      if (isSystemPackageId(id)) {
        systemPackageIds.add(id)
      } else {
        const installed = installedById.get(id) ?? null
        const runtime = resolveRuntime(installed)
        const origin: PluginOriginKind = isBuiltinFeaturePluginId(id) ? 'builtin-feature' : 'extension'
        const version = installed?.installedVersion || installed?.manifest.version || null
        const adoption = installed?.manifest.adoption === 'cold' ? 'cold' : 'online'
        plugins.push({
          id,
          title: resolvePackageTitle(id, descriptor, installed),
          summary: resolvePackageSummary(id, installed),
          origin,
          runtime,
          icon: pluginIcon(id, runtime),
          descriptor,
          installed,
          version,
          adoption,
          hasRuntimeUi: Boolean(installed?.manifest.ui),
          restartRequired: Boolean(installed?.restartRequired || descriptor.errorCode === 'plugin_restart_required'),
          category: installed?.category || (origin === 'builtin-feature' ? '内置功能' : '扩展插件')
        })
      }
    } else {
      otherCapabilities.push(descriptor)
    }
  }

  // Also include system packages reported in installedPlugins even if absent from capabilities page.
  for (const id of installedById.keys()) {
    if (isSystemPackageId(id)) systemPackageIds.add(id)
  }

  const systemPackages = [...systemPackageIds]
    .sort((a, b) => a.localeCompare(b))
    .map(id => buildSystemPackageModel(id, pluginDescriptorById.get(id) ?? null, installedById.get(id) ?? null))

  const systemByPlatform = new Map<string, SystemPackageModel>()
  let modelServicesPackage: SystemPackageModel | null = null
  for (const pkg of systemPackages) {
    if (pkg.id === 'system.model-services') modelServicesPackage = pkg
    if (pkg.platformId) systemByPlatform.set(pkg.platformId, pkg)
  }

  function policyPlatformId(row: ZbaPlatformPolicyEntryView): string {
    return row.target.kind === 'instance' ? row.target.key.platformId : row.target.platformId
  }
  function policyAccountScope(row: ZbaPlatformPolicyEntryView): string {
    return row.target.kind === 'instance' ? row.target.key.accountScope : row.target.accountScope
  }

  const platforms: PlatformChannelModel[] = driverDescriptors.map(descriptor => {
    const key = descriptor.key as Extract<ZbaManagedDescriptor['key'], { kind: 'driver' }>
    const { platformId, accountScope, instanceId } = key.key
    const driverPackage = systemByPlatform.get(platformId) ?? null
    const policy = policyViews.find(
      row => row.target.kind === 'instance'
        && row.target.key.platformId === platformId
        && row.target.key.accountScope === accountScope
        && row.target.key.instanceId === instanceId
    ) ?? policyViews.find(
      row => policyPlatformId(row) === platformId
        && (policyAccountScope(row) === accountScope || !policyAccountScope(row))
    ) ?? policyViews.find(row => policyPlatformId(row) === platformId)

    return {
      platformId,
      accountScope,
      instanceId,
      identity: keyIdentity(descriptor.key),
      title: platformName(platformId),
      summary: driverPackage?.summary || '平台消息接入与运行时连接驱动',
      icon: platformIcon(platformId),
      descriptor,
      driverPackage,
      policy
    }
  })

  // Sort providers by priority ascending (if available), then key.
  providers.sort((a, b) => {
    const pa = a.configView?.priority ?? Number.MAX_SAFE_INTEGER
    const pb = b.configView?.priority ?? Number.MAX_SAFE_INTEGER
    if (pa !== pb) return pa - pb
    return a.key.localeCompare(b.key)
  })

  // Sort plugins: builtin-feature first, then extensions, by title.
  plugins.sort((a, b) => {
    if (a.origin !== b.origin) return a.origin === 'builtin-feature' ? -1 : 1
    return a.title.localeCompare(b.title, 'zh-CN')
  })

  return {
    providers,
    modelServicesPackage,
    platforms,
    plugins,
    systemPackages,
    otherCapabilities
  }
}
