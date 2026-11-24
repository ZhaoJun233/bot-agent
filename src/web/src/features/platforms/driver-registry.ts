import type { ZbaDriverKey } from '../../api/zba'
import type { AppIconName } from '../../shared/ui/AppIcon.vue'
import { configurationFor, type CapabilityConfiguration } from '../capabilities/configuration-ownership'
import {
  driverSpecByPlatformId,
  pluginRegistryRevision,
  pluginSettings,
  settingsFor,
  type DriverCredentialMode,
  type PlatformPolicyCopy
} from '../capabilities/plugin-settings'

export interface ResolvedPlatformDriverMeta {
  platformId: string
  configScope: string
  title: string
  summary: string
  icon: AppIconName
  supportsMessagingInspection: boolean
  credentialMode: DriverCredentialMode
  credentialHint?: string
  policyCopy?: Readonly<PlatformPolicyCopy>
}

export interface PlatformSettingsTarget {
  capability: CapabilityConfiguration
  policyPlatformId?: string
  driverKey?: ZbaDriverKey
  dialogTitle?: string
}

const KNOWN_ICONS = new Set<AppIconName>([
  'platform', 'qq', 'feishu', 'terminal', 'globe', 'cpu', 'plugin', 'link', 'bolt', 'shield', 'sliders'
])

function resolveIcon(name?: string): AppIconName {
  if (name && KNOWN_ICONS.has(name as AppIconName)) return name as AppIconName
  return 'platform'
}

/** 按 platformId 从注册表动态解析驱动元数据（无宿主硬编码）。 */
export function platformDriverMeta(platformId: string): ResolvedPlatformDriverMeta {
  const spec = driverSpecByPlatformId(platformId)
  if (!spec) {
    return {
      platformId,
      configScope: 'platform',
      title: platformId,
      summary: '平台消息接入与运行时连接驱动',
      icon: 'platform',
      supportsMessagingInspection: false,
      credentialMode: 'required'
    }
  }
  return {
    platformId,
    configScope: spec.pluginKey,
    title: spec.title,
    summary: spec.summary,
    icon: resolveIcon(spec.icon),
    supportsMessagingInspection: Boolean(spec.supportsMessagingInspection),
    credentialMode: spec.credentialMode ?? 'required',
    credentialHint: spec.credentialHint,
    policyCopy: spec.policyCopy
  }
}

/** 仅用于显示层：把驱动凭据模式与底层布尔值格式化为清晰状态文案，不改写任何存储或请求键。 */
export function formatDriverCredentialStatus(
  mode: DriverCredentialMode,
  configured: boolean
): string {
  if (mode === 'none') return '无需凭据'
  if (mode === 'optional-env') return configured ? '已配置（环境变量）' : '可选（未设令牌）'
  if (mode === 'required-env') return configured ? '已配置（环境变量）' : '未配置（需环境变量）'
  return configured ? '已配置' : '未配置'
}

/** 仅用于显示层：把内置账号范围（legacy / default / 空）格式化为友好标签，不改写任何存储或请求键。 */
export function formatAccountScopeLabel(scope: string): string {
  const trimmed = scope.trim()
  if (!trimmed || trimmed === 'legacy' || trimmed === 'default') return '默认账号'
  return trimmed
}

/** 为指定平台驱动解析独立的配置弹窗目标；未登记的自定义驱动回退到 scoped platform 兜底作用域。 */
export function platformDriverConfigFor(
  platformId: string,
  displayTitle?: string,
  driverKey?: ZbaDriverKey
): PlatformSettingsTarget | null {
  const spec = driverSpecByPlatformId(platformId)
  if (spec) {
    const capability = configurationFor(spec.pluginKey)
    if (capability) {
      return {
        capability,
        policyPlatformId: capability.policyPlatformId ?? platformId,
        driverKey
      }
    }
  }
  const fallback = configurationFor('platform')
  if (!fallback) return null
  return {
    capability: fallback,
    policyPlatformId: platformId,
    driverKey,
    dialogTitle: displayTitle ? `${displayTitle} · 接入与策略` : undefined
  }
}

/** 按系统包 ID（如 `system.platform-feishu`）或兜底 `platform` 解析配置弹窗目标。 */
export function platformDriverConfigByScope(ownerId: string): PlatformSettingsTarget | null {
  const capability = configurationFor(ownerId)
  if (!capability) return null
  if (capability.policyPlatformId || ownerId === 'platform' || capability.platformId) {
    return {
      capability,
      policyPlatformId: capability.policyPlatformId
    }
  }
  return null
}

/** 判断一个未决配置操作 target 是否属于任一平台驱动或平台兜底作用域。 */
export function isPlatformConfigTarget(target: string): boolean {
  void pluginRegistryRevision.value
  if (target === 'platform' || target === 'config:platform' || target.startsWith('config:platform:')) return true
  if (!target.startsWith('config:')) return false
  const scope = target.slice('config:'.length)
  return pluginSettings.some(spec => spec.pluginKey === scope && Boolean(spec.platformId || spec.policyPlatformId))
}

/** 把未决恢复记录的 target 翻译成可读平台驱动名。 */
export function platformTargetLabel(target: string): string {
  if (target === 'platform' || target === 'config:platform') return '其它平台配置与策略'
  if (target.startsWith('config:platform:')) {
    const pid = target.slice('config:platform:'.length)
    return platformDriverMeta(pid).title
  }
  if (target.startsWith('config:')) {
    const scope = target.slice('config:'.length)
    return settingsFor(scope)?.title ?? scope
  }
  return target
}
