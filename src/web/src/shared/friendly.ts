import { settingsFor } from '../features/capabilities/plugin-settings'

/**
 * 把后端的机器名翻译成人话。只影响显示：key、testid、请求体一律用原值。
 * 未收录的值原样返回，方便和服务端日志对照。
 */

const GENERIC_NAMES = new Set(['plugin', 'driver', 'provider', 'device', 'mcp', 'platform-driver', 'model-provider'])

const platformNames: Record<string, string> = {
  'qq.private': 'QQ 私域通道',
  'qq.official': 'QQ 官方通道',
  feishu: '飞书',
  local: '本地调试'
}

const providerNames: Record<string, string> = {
  primary: '主模型',
  fast: '快速回复模型'
}

const kindNames: Record<string, string> = {
  driver: '驱动',
  plugin: '插件',
  provider: '模型',
  device: '设备',
  mcp: 'MCP',
  model: '模型',
  platform: '平台'
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
  disconnected: '已断开',
  unknown: '未知'
}

const errorNames: Record<string, string> = {
  model_timeout: '模型响应超时',
  model_unavailable: '模型暂时不可用',
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
  return platformNames[platformId] ?? platformId
}

export function pluginName(key: string): string {
  const spec = settingsFor(key)
  if (spec) return spec.title
  const tail = key.split('.').pop() ?? key
  return tail || key
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
    const platform = [...Object.keys(platformNames)].find((p) => rest === p || rest.startsWith(p + ':') || rest.startsWith(p + '/'))
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
