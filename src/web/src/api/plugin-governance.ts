import { isReviewTimestamp } from './plugin-presentation'

/** Lifecycle findings are separate from review badges and source freshness. */
export interface PluginGovernance {
  status: 'active' | 'deprecated' | 'withdrawn' | 'revoked' | 'unknown'
  reason: string | null
  updatedAt: string | null
  blocked: boolean
  match: 'identity' | 'sha256' | 'none'
}
export interface PluginGovernanceSource {
  status: 'fresh' | 'cached' | 'unavailable'
  source: string
  generatedAt: string | null
  checkedAt: string | null
  errorCode: string | null
}
function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
const nullableText = (value: unknown) => value === null || typeof value === 'string'
const nullableTime = (value: unknown) => value === null || typeof value === 'string' && isReviewTimestamp(value)
export function isPluginGovernance(value: unknown): value is PluginGovernance {
  return record(value) && typeof value.status === 'string' && ['active', 'deprecated', 'withdrawn', 'revoked', 'unknown'].includes(value.status)
    && nullableText(value.reason) && nullableTime(value.updatedAt) && typeof value.blocked === 'boolean'
    && typeof value.match === 'string' && ['identity', 'sha256', 'none'].includes(value.match)
    && value.blocked === (value.status === 'revoked')
}
export function isPluginGovernanceSource(value: unknown): value is PluginGovernanceSource {
  return record(value) && typeof value.status === 'string' && ['fresh', 'cached', 'unavailable'].includes(value.status) && typeof value.source === 'string'
    && nullableTime(value.generatedAt) && nullableTime(value.checkedAt) && nullableText(value.errorCode)
    && (value.status !== 'fresh' || value.generatedAt !== null && value.checkedAt !== null && value.errorCode === null)
}
export function governanceLabel(value?: PluginGovernance): string {
  switch (value?.status) {
    case 'active': return '治理记录未标记此包（不等于安全审核）'
    case 'deprecated': return '已弃用 · 建议迁移'
    case 'withdrawn': return '已撤下 · 不等于安全撤销'
    case 'revoked': return '已明确撤销 · 禁止获取此包'
    default: return '治理状态未知'
  }
}
export function governanceSourceLabel(source?: PluginGovernanceSource): string {
  switch (source?.status) {
    case 'fresh': return '治理源已检查；结论仅适用于当前记录'
    case 'cached': return '治理源未获最新数据，正在使用缓存；不能确认未出现新撤销'
    case 'unavailable': return '治理源不可用；不能确认未出现新撤销'
    default: return '宿主未提供治理源检查结果；不能视为已通过检查'
  }
}
