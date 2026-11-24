import type { ZbaFeatureOverride } from './zba'

export interface ZbaDriverKey {
  platformId: string
  accountScope: string
  instanceId: string
}

export type ZbaConversationToolTier = 0 | 1 | 2 | 3

export interface ZbaConversationProfileKey {
  driver: ZbaDriverKey
  conversationKey: string
}

export interface ZbaConversationStyleOverride {
  personaOverride?: string | null
  personaAppend?: string | null
  filterActionNarration?: boolean | null
  voiceMaxChars?: number | null
  voiceEagerness?: number | null
}

export interface ZbaConversationPacingOverride {
  groupInterjectionProbability?: number | null
  groupInterjectionCooldownSeconds?: number | null
  groupContinuationWaitSeconds?: number | null
  privateContinuationWaitSeconds?: number | null
  suitabilityThreshold?: number | null
  maxContextMessages?: number | null
  proactiveEnabled: number
  proactiveQuietSeconds?: number | null
}

export interface ZbaConversationCapabilityOverride {
  features: ZbaFeatureOverride[]
  toolTier: ZbaConversationToolTier
}

export interface ZbaConversationProfile {
  key: ZbaConversationProfileKey
  displayLabel: string
  style?: ZbaConversationStyleOverride | null
  pacing?: ZbaConversationPacingOverride | null
  capabilities?: ZbaConversationCapabilityOverride | null
}

/** 0 = Upsert, 1 = Delete（按主键逐行操作，不覆盖其它会话）。 */
export interface ZbaConversationProfileEdit {
  key: ZbaConversationProfileKey
  operation: 0 | 1
  profile?: ZbaConversationProfile | null
}

export interface ZbaConversationProfileView {
  key: ZbaConversationProfileKey
  displayLabel: string
  conversationKeyDisplay: string
  visibility: 0 | 1 | 2
  displayLabelRaw?: string | null
  style?: ZbaConversationStyleOverride | null
  pacing?: ZbaConversationPacingOverride | null
  capabilities?: ZbaConversationCapabilityOverride | null
}

function isObject(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}

function isInt(value: unknown): value is number {
  return typeof value === 'number' && Number.isInteger(value)
}

function isOptString(value: unknown): boolean {
  return value === undefined || value === null || typeof value === 'string'
}

function isOptBool(value: unknown): boolean {
  return value === undefined || value === null || typeof value === 'boolean'
}

function isOptInt(value: unknown): boolean {
  return value === undefined || value === null || isInt(value)
}

function isOptFinite(value: unknown): boolean {
  return value === undefined || value === null || (typeof value === 'number' && Number.isFinite(value))
}

export function isConversationProfileKey(value: unknown): value is ZbaConversationProfileKey {
  return isObject(value)
    && isObject(value.driver)
    && typeof value.driver.platformId === 'string'
    && typeof value.driver.accountScope === 'string'
    && typeof value.driver.instanceId === 'string'
    && typeof value.conversationKey === 'string'
}

function isStyleOverride(value: unknown): boolean {
  return isObject(value)
    && isOptString(value.personaOverride)
    && isOptString(value.personaAppend)
    && isOptBool(value.filterActionNarration)
    && isOptInt(value.voiceMaxChars)
    && isOptInt(value.voiceEagerness)
}

function isPacingOverride(value: unknown): boolean {
  return isObject(value)
    && isOptInt(value.groupInterjectionProbability)
    && isOptInt(value.groupInterjectionCooldownSeconds)
    && isOptInt(value.groupContinuationWaitSeconds)
    && isOptFinite(value.privateContinuationWaitSeconds)
    && isOptInt(value.suitabilityThreshold)
    && isOptInt(value.maxContextMessages)
    && (value.proactiveEnabled === undefined || (isInt(value.proactiveEnabled) && value.proactiveEnabled >= 0 && value.proactiveEnabled <= 2))
    && isOptInt(value.proactiveQuietSeconds)
}

function isCapabilityOverride(value: unknown): boolean {
  return isObject(value)
    && (value.features === undefined || (Array.isArray(value.features)
      && value.features.every((item) => isObject(item) && isInt(item.feature) && typeof item.enabled === 'boolean')))
    && (value.toolTier === undefined || (isInt(value.toolTier) && value.toolTier >= 0 && value.toolTier <= 3))
}

export function isConversationProfileView(value: unknown): value is ZbaConversationProfileView {
  return isObject(value)
    && isConversationProfileKey(value.key)
    && typeof value.displayLabel === 'string'
    && typeof value.conversationKeyDisplay === 'string'
    && isInt(value.visibility) && value.visibility >= 0 && value.visibility <= 2
    && isOptString(value.displayLabelRaw)
    && (value.style === undefined || value.style === null || isStyleOverride(value.style))
    && (value.pacing === undefined || value.pacing === null || isPacingOverride(value.pacing))
    && (value.capabilities === undefined || value.capabilities === null || isCapabilityOverride(value.capabilities))
}

export function parseConversationProfileAddress(
  address: Record<string, unknown>
): { kind: 'conversationProfile'; key: ZbaConversationProfileKey } | null {
  if (address.kind !== 'conversationProfile' || !isConversationProfileKey(address.key)) return null
  return {
    kind: 'conversationProfile',
    key: {
      driver: {
        platformId: address.key.driver.platformId,
        accountScope: address.key.driver.accountScope,
        instanceId: address.key.driver.instanceId
      },
      conversationKey: address.key.conversationKey
    }
  }
}
