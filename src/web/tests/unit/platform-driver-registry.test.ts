import { describe, expect, it } from 'vitest'
import {
  formatAccountScopeLabel,
  formatDriverCredentialStatus,
  platformDriverMeta
} from '../../src/features/platforms/driver-registry'

describe('platform driver registry presentation helpers', () => {
  it('resolves credentialMode and credentialHint for all four builtin drivers and defaults unknown drivers to required', () => {
    expect(platformDriverMeta('local')).toMatchObject({
      credentialMode: 'none',
      credentialHint: expect.stringContaining('无需外部接入凭据')
    })
    expect(platformDriverMeta('qq.private')).toMatchObject({
      credentialMode: 'optional-env',
      credentialHint: expect.stringContaining('环境变量')
    })
    expect(platformDriverMeta('qq.official')).toMatchObject({
      credentialMode: 'required-env',
      credentialHint: expect.stringContaining('BOTAGENT_OFFICIAL_APP_SECRET')
    })
    expect(platformDriverMeta('feishu')).toMatchObject({
      credentialMode: 'required',
      credentialHint: expect.stringContaining('App Secret')
    })
    const unknown = platformDriverMeta('custom.unknown')
    expect(unknown.credentialMode).toBe('required')
    expect(unknown.credentialHint).toBeUndefined()
  })

  it('formats driver credential status across all four modes without altering boolean wire semantics', () => {
    expect(formatDriverCredentialStatus('none', false)).toBe('无需凭据')
    expect(formatDriverCredentialStatus('none', true)).toBe('无需凭据')

    expect(formatDriverCredentialStatus('optional-env', false)).toBe('可选（未设令牌）')
    expect(formatDriverCredentialStatus('optional-env', true)).toBe('已配置（环境变量）')

    expect(formatDriverCredentialStatus('required-env', false)).toBe('未配置（需环境变量）')
    expect(formatDriverCredentialStatus('required-env', true)).toBe('已配置（环境变量）')

    expect(formatDriverCredentialStatus('required', false)).toBe('未配置')
    expect(formatDriverCredentialStatus('required', true)).toBe('已配置')
  })

  it('maps legacy/default/blank account scopes to friendly label while preserving explicit custom scopes', () => {
    expect(formatAccountScopeLabel('legacy')).toBe('默认账号')
    expect(formatAccountScopeLabel('default')).toBe('默认账号')
    expect(formatAccountScopeLabel('  ')).toBe('默认账号')
    expect(formatAccountScopeLabel('')).toBe('默认账号')
    expect(formatAccountScopeLabel('corp-main')).toBe('corp-main')
    expect(formatAccountScopeLabel('  team-a  ')).toBe('team-a')
  })
})
