import type { ZbaQuota } from '../../src/api/zba'
export const quota = (overrides: Partial<ZbaQuota> = {}): ZbaQuota => ({
  sourceKey: 'group:10001', displayName: '群聊 100***01', revision: '1', dailyTokenLimit: 100000,
  usedPromptTokens: 1200, usedCompletionTokens: 300, usedTokens: 1500, remainingTokens: 98500,
  resetDate: '2026-10-29', nextResetAt: '2026-10-30T00:00:00Z', energySaving: false,
  minimumDailyTokenLimit: 1, maximumDailyTokenLimit: 1000000000, ...overrides
})
export const quotaFeatures = ['quotas.list', 'quotas.save', 'quotas.find']
