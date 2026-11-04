import type { ZbaHealth, ZbaMeta } from './zba'

export const EXPECTED_CONTRACT = 'zba-1.0'

export const REQUIRED_FEATURES = [
  'sessions.list',
  'sessions.ensure',
  'tasks.result',
  'tasks.find'
] as const

export interface Compatibility {
  checked: boolean
  compatible: boolean
  writesEnabled: boolean
  reasons: string[]
  warnings: string[]
}

export function evaluateCompatibility(
  meta: ZbaMeta | null,
  health: ZbaHealth | null
): Compatibility {
  if (meta === null) {
    return {
      checked: false,
      compatible: false,
      writesEnabled: false,
      reasons: ['尚未读取 ZBA 契约信息。'],
      warnings: []
    }
  }

  const reasons: string[] = []
  const warnings: string[] = []

  if (meta.contractVersion !== EXPECTED_CONTRACT) {
    reasons.push(`契约版本 ${meta.contractVersion} 与前端期望的 ${EXPECTED_CONTRACT} 不一致。`)
  }
  if (meta.authentication !== 'bearer') {
    reasons.push(`认证方式 ${meta.authentication} 不受支持；本版只支持 Bearer。`)
  }
  if (health !== null && health.contractVersion !== meta.contractVersion) {
    warnings.push(`health 与 meta 的契约版本不一致（${health.contractVersion} / ${meta.contractVersion}）。`)
  }
  if (health !== null && !health.configured) {
    warnings.push('后端尚未配置 ZBA 访问令牌（BOTAGENT_ZBA_TOKEN）。')
  }

  const missing = REQUIRED_FEATURES.filter((feature) =>
    !(Array.isArray(meta.features) ? meta.features : []).includes(feature))
  if (missing.length > 0) {
    warnings.push(`后端缺少功能标记：${missing.join('、')}；写操作保持禁用。`)
  }

  return {
    checked: true,
    compatible: reasons.length === 0,
    writesEnabled: reasons.length === 0 && missing.length === 0,
    reasons,
    warnings
  }
}
