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
  'preset.feature.websearch': 'globe',
  'preset.feature.documents': 'upload',
  'preset.feature.digest': 'spark',
  'preset.feature.knowledge': 'history',
  'preset.feature.scheduler': 'bolt',
  'preset.feature.personal-todo': 'check-circle'
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
  system: '系统核心',
  governance: '隐私治理',
  task: '后台任务',
  subsystem: '核心子系统'
}

const stateNames: Record<string, string> = {
  healthy: '运行良好',
  ready: '就绪',
  running: '运行中',
  active: '运行中',
  enabled: '已启用',
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
  unauthorized: '令牌无效',
  mcp_management_unavailable: '当前宿主未启用 MCP 管理运行时',
  env_server_locked: '该 MCP 服务端由环境变量锁定为只读，无法在控制台覆盖或删除',
  ssrf_endpoint_blocked: 'MCP 地址未通过安全校验（默认拒绝私网、回环及非 HTTP/HTTPS 地址）',
  invalid_server_id: 'MCP 服务端标识格式无效（仅支持字母、数字、连字符与下划线，最长 32 位）',
  allowed_tools_required: '至少需要显式勾选或填写一个允许调用的工具名（allowedTools 不能为空）',
  invalid_tool_name: '工具名称包含控制字符或超过 64 字符限制',
  too_many_tools: '单个 MCP 服务端声明的工具数量超过上限（最多 64 个）',
  invalid_auth_token: 'Bearer Token 格式无效（仅允许可见 ASCII 字符，最长 4096 位；且不能同时勾选清除凭据）',
  mcp_server_limit_exceeded: '控制台托管的 MCP 服务端数量已达上限',
  mcp_servers_payload_too_large: 'MCP 服务端配置总大小超过 64 KB 上限',
  mcp_timeout: '连接或探测 MCP 服务端超时',
  mcp_unreachable: '无法连接到指定的 MCP 服务端地址',
  mcp_probe_failed: 'MCP 服务端探测失败，请检查地址、协议或凭据',
  probe_canceled: 'MCP 服务端探测已取消',
  mcp_reload_failed: 'MCP 运行态热重载失败，配置已落盘但目录未刷新',
  server_not_found: '指定的 MCP 服务端不存在或已被删除',
  revision_conflict: '配置版本已变更，请刷新最新快照后重试',
  idempotency_conflict: '幂等键与历史操作载荷冲突，请重新提交',
  access_roles_unavailable: '当前宿主未启用跨平台角色权限存储',
  invalid_access_role: '角色绑定记录格式无效或包含不支持的角色值',
  invalid_access_role_platform: '不支持的平台标识（仅支持 qq.private / qq.official / feishu / local）',
  invalid_access_role_instance: 'InstanceId 格式无效或超过长度限制',
  invalid_access_role_token: '标识字段（用户 ID / AccountScope / 会话 Key）不能为空、空白或包含控制字符',
  invalid_access_role_note: '备注包含控制字符或超过 80 字符上限',
  duplicate_access_role: '存在重复的角色绑定键（同一平台、账号、实例、会话范围与用户只能绑定一条）',
  group_manager_requires_group_scope: '群组管理员（GroupManager）必须绑定具体的群聊会话 Key（如 group:10001 或 feishu:group:oc_xxx）',
  access_role_scope_platform_mismatch: '会话范围前缀与所选平台不一致',
  access_roles_payload_too_large: '角色绑定配置总大小超过 64 KB 上限',
  access_role_limit_exceeded: '角色绑定条目数量超过上限（最多 128 条）',
  passive_trigger_required: 'QQ 官方通道受限于被动回复窗口，暂不支持无触发定时主动推送；建议使用飞书、QQ 私域或 Web 工作台',
  private_conversation_only: '个人待办仅限本人工作台与可靠私聊访问（群聊严禁读取或操作）',
  identity_linking_disabled: '跨平台本人身份关联当前默认关闭，请先开启该功能后再绑定',
  challenge_invalid_or_expired: '挑战码无效、已过期（120 秒）或已被使用',
  same_endpoint_forbidden: '不能在同一平台端点自我确认，请在另一端的可靠私聊中确认',
  cron_interval_too_short: '周期任务最小间隔不能低于 5 分钟',
  scheduler_scope_quota_exceeded: '当前会话活跃定时任务已达上限（32 条）',
  scheduler_global_quota_exceeded: '全局活跃定时任务已达上限（256 条）',
  attachment_too_large: '附件体积超过 10 MiB 上限或内容为空',
  attachment_invalid_base64: '附件内容不是有效的 Base64 编码',
  executable_rejected: '安全策略已拦截可执行文件或危险二进制附件',
  zip_bomb_rejected: '压缩包解压体积或嵌套层级异常，已安全拦截',
  file_too_large: '提取的文档内容体积超出上限',
  scheduler_unavailable: '当前宿主未启用定时与提醒引擎',
  scheduler_revision_conflict: '定时任务版本已变化，请刷新后再操作',
  job_not_found: '指定的定时任务不存在或已被删除',
  job_id_invalid: '定时任务编号格式无效',
  invalid_timezone: '时区名称无效（请使用标准 IANA 时区，例如 Asia/Shanghai）',
  scheduler_fire_time_in_past: '触发时间必须晚于当前时间',
  cron_expr_invalid: 'Cron 表达式格式无效（需为合法的 5 段表达式）',
  personal_todo_unavailable: '当前宿主未启用个人待办引擎',
  todo_revision_conflict: '个人待办版本已变化，请刷新后再操作',
  todo_not_found: '指定的个人待办不存在或已被删除',
  todo_title_invalid: '待办标题不能为空、包含控制字符或超过长度限制',
  todo_status_invalid: '待办状态值无效（仅支持 open / done / archived）',
  todo_id_invalid: '个人待办编号格式无效',
  idempotency_key_conflict: '幂等键与历史请求参数冲突，请重新提交',
  invalid_idempotency_key: '幂等键格式无效',
  workspace_unavailable: '当前宿主未启用个人工作台服务',
  workspace_text_required: '请输入工作台消息内容或选择有效附件',
  workspace_text_too_long: '工作台消息正文超过最大长度限制',
  scope_payload_too_large: '当前会话知识库总大小已超出上限，请清理部分旧文档后再保存'
}

export function workspaceRoleLabel(role: number): string {
  switch (role) {
    case 0: return '访客（Guest · 仅聊天）'
    case 1: return '普通成员（Member）'
    case 2: return '受信任成员（TrustedMember）'
    case 3: return '本群管理员（GroupManager）'
    case 4: return '号主（BotOwner）'
    default: return `未知角色 (${role})`
  }
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
