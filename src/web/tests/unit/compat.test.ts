import { describe, expect, it } from 'vitest'
import { evaluateCompatibility, EXPECTED_CONTRACT } from '../../src/api/compat'
import type { ZbaHealth, ZbaMeta } from '../../src/api/zba'

const health: ZbaHealth = {
  status: 'healthy',
  ready: true,
  configured: true,
  contractVersion: EXPECTED_CONTRACT,
  checkedAt: '2026-10-20T12:00:00+00:00'
}

const meta: ZbaMeta = {
  contractVersion: EXPECTED_CONTRACT,
  serverVersion: '0.0.0-synthetic',
  authentication: 'bearer',
  scopes: ['read', 'admin'],
  sse: false,
  mcpManagement: false,
  dockerManagement: false,
  maxBodyBytes: 1024,
  instanceId: 'zi-synthetic-0001',
  features: ['sessions.list', 'sessions.ensure', 'tasks.result', 'tasks.find', 'tasks.receipts']
}

describe('evaluateCompatibility', () => {
  it('zba-1.0 + 全部 feature + configured 时兼容且允许写', () => {
    const result = evaluateCompatibility(meta, health)
    expect(result.checked).toBe(true)
    expect(result.compatible).toBe(true)
    expect(result.writesEnabled).toBe(true)
    expect(result.reasons).toEqual([])
    expect(result.warnings).toEqual([])
  })

  it('契约版本不一致时禁止写，并给出原因', () => {
    const result = evaluateCompatibility({ ...meta, contractVersion: 'zba-9.9' }, health)
    expect(result.compatible).toBe(false)
    expect(result.writesEnabled).toBe(false)
    expect(result.reasons.join('')).toContain('zba-9.9')
  })

  it('非 Bearer 认证时不兼容', () => {
    const result = evaluateCompatibility({ ...meta, authentication: 'cookie' }, health)
    expect(result.compatible).toBe(false)
    expect(result.writesEnabled).toBe(false)
  })

  it('缺 feature 时保留只读但不允许写', () => {
    const result = evaluateCompatibility({ ...meta, features: ['sessions.list'] }, health)
    expect(result.compatible).toBe(true)
    expect(result.writesEnabled).toBe(false)
    expect(result.warnings.join('')).toContain('sessions.ensure')
    expect(result.warnings.join('')).toContain('tasks.result')
    expect(result.warnings.join('')).toContain('tasks.find')
  })

  it('后端未配置令牌时给出警告，但不伪造身份', () => {
    const result = evaluateCompatibility(meta, { ...health, configured: false })
    expect(result.compatible).toBe(true)
    expect(result.warnings.join('')).toContain('BOTAGENT_ZBA_TOKEN')
    expect(result).not.toHaveProperty('identity')
  })

  it('缺 feature 数组时按缺失处理，不抛异常', () => {
    const result = evaluateCompatibility({ ...meta, features: undefined as unknown as string[] }, health)
    expect(result.compatible).toBe(true)
    expect(result.writesEnabled).toBe(false)
    expect(result.warnings.join('')).toContain('sessions.list')
  })

  it('尚未读取 meta 时不声称兼容', () => {
    const result = evaluateCompatibility(null, health)
    expect(result.checked).toBe(false)
    expect(result.compatible).toBe(false)
    expect(result.writesEnabled).toBe(false)
    expect(result.reasons.length).toBe(1)
  })

  it('health 与 meta 契约版本不一致时给出警告', () => {
    const result = evaluateCompatibility(meta, { ...health, contractVersion: 'zba-1.1' })
    expect(result.compatible).toBe(true)
    expect(result.warnings.join('')).toContain('health')
  })
})
