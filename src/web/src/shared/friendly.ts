import {
  driverSpecByPlatformId,
  registeredPlatformIds,
  settingsFor
} from '../features/capabilities/plugin-settings'
import { platformQqPrivatePresentation } from '../../../../packages/platform-qq-private/frontend/presentation'
import { platformQqOfficialPresentation } from '../../../../packages/platform-qq-official/frontend/presentation'
import { platformFeishuPresentation } from '../../../../packages/platform-feishu/frontend/presentation'
import { platformLocalPresentation } from '../../../../packages/platform-local/frontend/presentation'
import { mcpToolsPresentation } from '../../../../packages/mcp-tools/frontend/presentation'
import type { AppIconName } from './ui/AppIcon.vue'

/**
 * 把后端的机器名翻译成人话。只影响显示：key、testid、请求体一律用原值。
 * 未收录的值原样返回，方便和服务端日志对照。
 */

const GENERIC_NAMES = new Set(['plugin', 'driver', 'provider', 'device', 'mcp', 'platform-driver', 'model-provider'])

const providerNames: Record<string, string> = {
  primary: '主模型',
  fast: '快速回复模型'
}

export interface SystemPackageDescriptorMeta {
  title: string
  summary: string
  domain: 'provider' | 'platform' | 'system'
  platformId?: string
  configScope?: string
  icon: AppIconName
}

function platformSystemPackageMeta(
  spec: {
    pluginKey: string
    title: string
    summary: string
    platformId: string
    icon: AppIconName
  }
): SystemPackageDescriptorMeta {
  return {
    title: spec.title,
    summary: spec.summary,
    domain: 'platform',
    platformId: spec.platformId,
    configScope: spec.pluginKey,
    icon: spec.icon
  }
}

export const SYSTEM_PACKAGE_META: Readonly<Record<string, SystemPackageDescriptorMeta>> = Object.freeze({
  'system.model-services': {
    title: '模型路由与服务引擎',
    summary: '模型供应商注册、主/快模型路由、连接测试与凭据治理',
    domain: 'provider',
    configScope: 'models',
    icon: 'provider'
  },
  'system.platform-qq-private': platformSystemPackageMeta(platformQqPrivatePresentation),
  'system.platform-qq-official': platformSystemPackageMeta(platformQqOfficialPresentation),
  'system.platform-feishu': platformSystemPackageMeta(platformFeishuPresentation),
  'system.platform-local': platformSystemPackageMeta(platformLocalPresentation),
  'system.conversation': {
    title: '对话编排与上下文引擎',
    summary: '多轮对话编排、上下文窗口预算与回复节奏调度',
    domain: 'system',
    configScope: 'conversation',
    icon: 'config'
  },
  'system.agent': {
    title: '内置 Agent 任务运行时',
    summary: '后台 Agent 执行、审批策略与附加提示词治理',
    domain: 'system',
    configScope: 'agent',
    icon: 'tasks'
  },
  'system.health-reporting': {
    title: '每日健康日报',
    summary: '运行指标汇总与定时健康日报推送',
    domain: 'system',
    configScope: 'reporting',
    icon: 'traces'
  },
  'system.mcp-tools': {
    title: mcpToolsPresentation.title,
    summary: mcpToolsPresentation.summary,
    domain: 'system',
    configScope: 'agent',
    icon: 'cpu'
  }
})

const FEATURE_PLUGIN_ICONS: Readonly<Record<string, AppIconName>> = Object.freeze({
  'preset.feature.music': 'music',
  'preset.feature.voice': 'voice',
  'preset.feature.stickers': 'sticker',
  'preset.feature.poke': 'poke',
  'preset.feature.profiles': 'profile',
  'preset.feature.vibes': 'vibe',
  'preset.feature.token-quota': 'quota',
  'preset.feature.websearch': 'globe'
})

const kindNames: Record<string, string> = {
  driver: '消息平台',
  plugin: '插件',
  provider: '模型供应商',
  device: '设备',
  mcp: 'MCP',
  model: '模型供应商',
  platform: '消息平台',
  backup: '备份',
  system: '系统核心'
}

const stateNames: Record<string, string> = {
  healthy: '运行良好',
  ready: '就绪',
  running: '运行中',
  active: '运行中',
  applied: '已生效',
  degraded: '部分降级',
  unhealthy: '出现故障',
  failed: '失败',
  error: '出错',
  starting: '启动中',
  pending: '等待中',
  restartrequired: '需要重启',
  notstarted: '尚未生效',
  notloaded: '未加载',
  disabled: '已停用',
  stopped: '已停止',
  paused: '已暂停',
  inactive: '未启用',
  stale: '已过期',
  faulted: '出现故障',
  disconnected: '已断开',
  unknown: '未知'
}

const errorNames: Record<string, string> = {
  model_timeout: '模型响应超时',
  model_unavailable: '模型暂时不可用',
  model_auth_failed: '模型拒绝了这个密钥（401）',
  model_http_error: '模型接口返回错误',
  model_malformed_response: '模型返回的内容无法解析',
  upstream_empty: '模型没有给出回复内容',
  provider_secret_unavailable: '模型凭据不可用，本轮没有发起调用',
  provider_route_unavailable: '模型路由不可用，本轮没有发起调用',
  provider_restart_required: '模型供应商配置需要重启后生效',
  plugin_reload_failed: '插件热重载失败，旧运行实例保持不变，请修正后重试',
  plugin_reload_pending: '插件变更等待热应用',
  endpoint_authorization_required: '修改已有供应商的 API 地址仅允许复用已登记端点；如需接入全新地址，请先新增模型供应商',
  credential_required: '请为该供应商配置有效的 API Key（不能填写 URL）',
  credential_facts_unknown: '无法确认该供应商关联的凭据状态，请重新配置或替换 API Key',
  provider_endpoint_invalid: 'API 根地址格式无效，仅支持不含账号凭据的 http/https 地址',
  provider_metadata_required: '请完整填写供应商名称、API 根地址与默认模型名称',
  provider_create_must_be_disabled: '新增供应商需先保存为停用状态，核对并测试连接后再启用',
  provider_already_registered: '该供应商标识已存在，请刷新后重试',
  field_read_only: '该字段为只读（主供应商的优先级、启用状态与删除操作不可修改）',
  disabled_by_operator: '已被手动停用',
  rate_limited: '请求太频繁',
  unauthorized: '令牌无效'
}

/** 名字是否只是一个类型占位词（后端没给真名）。 */
export function isGenericName(name: string | undefined | null): boolean {
  return !name || GENERIC_NAMES.has(name.trim().toLowerCase())
}

export function kindName(kind: string): string {
  return kindNames[kind] ?? kind
}

export function platformName(platformId: string): string {
  return driverSpecByPlatformId(platformId)?.title ?? platformId
}

export function platformIcon(platformId: string): AppIconName {
  const icon = driverSpecByPlatformId(platformId)?.icon
  return (icon as AppIconName | undefined) ?? 'platform'
}

export function pluginName(key: string): string {
  const sys = SYSTEM_PACKAGE_META[key]
  if (sys) return sys.title
  const spec = settingsFor(key)
  if (spec) return spec.title
  const tail = key.split('.').pop() ?? key
  return tail || key
}

export function pluginSummary(key: string): string {
  const sys = SYSTEM_PACKAGE_META[key]
  if (sys) return sys.summary
  const spec = settingsFor(key)
  if (spec) return spec.summary
  return ''
}

export function pluginIcon(key: string, runtime?: string): AppIconName {
  const sys = SYSTEM_PACKAGE_META[key]
  if (sys) return sys.icon
  const feature = FEATURE_PLUGIN_ICONS[key]
  if (feature) return feature
  if (runtime === 'python') return 'python'
  return 'plugin'
}

export function providerName(key: string): string {
  return providerNames[key] ?? key
}

/** 能力目录卡片标题：有真名用真名，只有占位词时按 key 推出人话。 */
export function capabilityTitle(
  displayName: string,
  key: { kind: string; key: unknown }
): string {
  if (!isGenericName(displayName)) return displayName
  const raw = key.key
  if (key.kind === 'driver' && raw && typeof raw === 'object') {
    const platformId = String((raw as { platformId?: unknown }).platformId ?? '')
    return platformName(platformId)
  }
  if (typeof raw !== 'string') return displayName || kindName(key.kind)
  if (key.kind === 'plugin') return pluginName(raw)
  if (key.kind === 'provider') return providerName(raw)
  return raw
}

/** 概览组件表：id 形如 "plugin:preset.feature.music" / "driver:qq.private:legacy"。 */
export function componentTitle(displayName: string, kind: string, id: string): string {
  if (!isGenericName(displayName)) return displayName
  const rest = id.startsWith(kind + ':') ? id.slice(kind.length + 1) : id
  if (kind === 'plugin') return pluginName(rest)
  if (kind === 'provider') return providerName(rest)
  if (kind === 'driver') {
    const platform = registeredPlatformIds().find((p) => rest === p || rest.startsWith(p + ':') || rest.startsWith(p + '/'))
    if (platform) return platformName(platform)
  }
  return displayName || rest
}

function normalize(state: string): string {
  return state.toLowerCase().replace(/[\s_-]/g, '')
}

export function stateName(state: string | undefined | null): string {
  if (!state) return '未知'
  return stateNames[normalize(state)] ?? state
}

export function errorName(code: string | undefined | null): string | null {
  if (!code) return null
  return errorNames[code] ?? null
}

/** "3 分钟前" 这类相对时间；无效值返回 "—"。 */
export function relativeTime(value: string | undefined | null, now: number = Date.now()): string {
  if (!value) return '—'
  const time = new Date(value).getTime()
  if (Number.isNaN(time)) return value
  const diff = Math.round((now - time) / 1000)
  if (diff < 0) return '刚刚'
  if (diff < 45) return '刚刚'
  if (diff < 3600) return Math.max(1, Math.round(diff / 60)) + ' 分钟前'
  if (diff < 86400) return Math.round(diff / 3600) + ' 小时前'
  if (diff < 86400 * 30) return Math.round(diff / 86400) + ' 天前'
  return new Date(value).toLocaleDateString('zh-CN')
}

/** 剩余倒计时（用于待审批项）：例如 "剩余 4 分钟" / "已过期"。 */
export function remainingTime(value: string | undefined | null, now: number = Date.now()): string {
  if (!value) return '—'
  const target = new Date(value).getTime()
  if (Number.isNaN(target)) return value
  const diff = Math.round((target - now) / 1000)
  if (diff <= 0) return '已过期'
  if (diff < 60) return `剩余 ${diff} 秒`
  if (diff < 3600) return `剩余 ${Math.ceil(diff / 60)} 分钟`
  return `剩余 ${Math.floor(diff / 3600)} 小时 ${Math.ceil((diff % 3600) / 60)} 分钟`
}

/** 运行时长：2 天 3 小时 / 4 小时 12 分 / 8 分钟。 */
export function uptime(startedAt: string | undefined | null, now: number = Date.now()): string | null {
  if (!startedAt) return null
  const start = new Date(startedAt).getTime()
  if (Number.isNaN(start) || start > now) return null
  const minutes = Math.floor((now - start) / 60000)
  const days = Math.floor(minutes / 1440)
  const hours = Math.floor((minutes % 1440) / 60)
  const mins = minutes % 60
  if (days > 0) return days + ' 天 ' + hours + ' 小时'
  if (hours > 0) return hours + ' 小时 ' + mins + ' 分'
  return Math.max(1, mins) + ' 分钟'
}

export function greeting(hour: number = new Date().getHours()): string {
  if (hour < 5) return '夜深了'
  if (hour < 9) return '早上好'
  if (hour < 12) return '上午好'
  if (hour < 14) return '中午好'
  if (hour < 18) return '下午好'
  return '晚上好'
}
