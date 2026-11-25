import type { ZbaPatchSection, ZbaSecretSlot } from '../../api/zba'
import { retiredReplyFields } from '../configuration/reply-pacing'
import {
  fallbackOwnerOf, ownerOf, pluginRegistryRevision, pluginSettings, settingsFor,
  type RegisteredPluginSettingsSpec
} from './plugin-settings'

/** Storage partitions are wire contracts; metadata and editors belong to the registry. */
export interface CapabilityConfiguration extends Omit<
  RegisteredPluginSettingsSpec,
  'pluginKey' | 'sections' | 'fields' | 'fallbackSections' | 'secrets' | 'fieldLabels' | 'fieldHints' | 'secretLabels' | 'policyCopy'
> {
  id: string
  sections: ZbaPatchSection[]
  fields?: Array<{ section: ZbaPatchSection; key: string }>
  fallbackSections?: ZbaPatchSection[]
  secrets: ZbaSecretSlot[]
  fieldLabels?: Record<string, string>
  fieldHints?: Record<string, string>
  secretLabels?: Record<number, string>
  policyCopy?: {
    groupListLabel?: string
    privateListLabel?: string
    groupPlaceholder?: string
    privatePlaceholder?: string
  }
  /** System registration IDs are not backend plugin gates. */
  pluginKey?: string
}

export const basicFields = new Set([
  'aiModeEnabled', 'botPersona', 'groupInterjectionProbability', 'groupInterjectionCooldownSeconds',
  'groupContinuationWaitSeconds', 'privateContinuationWaitSeconds',
  'maxConcurrentReplies', 'maxContextMessages', 'maxMessagesPerConversation'
])

function capabilityFrom(spec: RegisteredPluginSettingsSpec): CapabilityConfiguration {
  return {
    ...spec, id: spec.pluginKey, pluginKey: spec.kind === 'plugin' ? spec.pluginKey : undefined,
    sections: [...new Set([
      ...(spec.sections ?? []), ...(spec.fields ?? []).map(field => field.section),
      ...(spec.enableField ? [spec.enableField.section] : []), ...(spec.fallbackSections ?? []),
      ...(spec.policyPlatformId ? ['platform' as const] : [])
    ])],
    fields: spec.fields?.map(field => ({ ...field })),
    enableField: spec.enableField ? { ...spec.enableField } : undefined,
    fallbackSections: spec.fallbackSections ? [...spec.fallbackSections] : undefined,
    secrets: [...spec.secrets],
    fieldLabels: spec.fieldLabels ? { ...spec.fieldLabels } : undefined,
    fieldHints: spec.fieldHints ? { ...spec.fieldHints } : undefined,
    secretLabels: spec.secretLabels ? { ...spec.secretLabels } : undefined,
    policyCopy: spec.policyCopy ? { ...spec.policyCopy } : undefined,
    editor: { ...spec.editor }
  }
}

/** Reading the revision also tracks registrations added after an outer computed was created. */
export function configurationCapabilities(): CapabilityConfiguration[] {
  void pluginRegistryRevision.value
  return pluginSettings.map(capabilityFrom)
}

export function configurationFor(id: string): CapabilityConfiguration | undefined {
  const spec = settingsFor(id)
  return spec ? capabilityFrom(spec) : undefined
}

/** Explicit ownership > basic behavior fields > a system section fallback. */
export function fieldOwner(section: string, key: string): string {
  const explicit = ownerOf(section, key)
  if (explicit) return explicit.pluginKey
  if (section === 'behavior' && basicFields.has(key)) return 'basic'
  return fallbackOwnerOf(section as ZbaPatchSection)?.pluginKey ?? section
}

export function secretOwner(slot: ZbaSecretSlot): string | undefined {
  void pluginRegistryRevision.value
  return pluginSettings.find(spec => spec.secrets.includes(slot))?.pluginKey
}

export function ownsConfiguration(scope: string, section: string, key: string): boolean {
  if (section === 'behavior' && retiredReplyFields.has(key)) return false
  return fieldOwner(section, key) === scope
}
