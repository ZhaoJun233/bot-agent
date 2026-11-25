/** Reply pacing is the only scheduling policy exposed by basic configuration. */
export const retiredReplyFields = new Set([
  'aiDesire', 'replyPacingEnabled', 'enableAtmosphereDamping', 'enableParticipationGating',
  'groupCooldownSeconds', 'privateCooldownSeconds', 'idleFallbackSeconds', 'proactiveCooldownSeconds',
  'participationCooldownSeconds', 'participationMaxActiveLifetimeSeconds', 'participationMaxConsecutiveReplies',
  'participationMaxExitingLifetimeSeconds', 'participationProbingMaxReplies'
])

export const replyPacingNumericLimits: Record<string, { min: number; max: number; step?: number | 'any' }> = {
  groupInterjectionProbability: { min: 0, max: 100 },
  groupInterjectionCooldownSeconds: { min: 0, max: 3600 },
  groupContinuationWaitSeconds: { min: 0, max: 30 },
  privateContinuationWaitSeconds: { min: 0, max: 30, step: 'any' }
}

export function replyPacingNumberError(key: string, value: number): string | undefined {
  const range = replyPacingNumericLimits[key]
  if (!range) return undefined
  if (!Number.isFinite(value)) return '请输入有限数字'
  if (key !== 'privateContinuationWaitSeconds' && !Number.isInteger(value)) return '请输入整数'
  return value >= range.min && value <= range.max
    ? undefined : `需要 ${range.min}–${range.max} 之间的数字`
}
