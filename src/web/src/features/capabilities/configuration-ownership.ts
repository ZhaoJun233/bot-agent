import type { ZbaPatchSection, ZbaSecretSlot } from '../../api/zba'
import {
  fallbackOwnerOf, ownerOf, pluginRegistryRevision, pluginSettings, settingsFor,
  type RegisteredPluginSettingsSpec
} from './plugin-settings'

/** Storage partitions are wire contracts; metadata and editors belong to the registry. */
export interface CapabilityConfiguration extends Omit<RegisteredPluginSettingsSpec, 'pluginKey' | 'sections'> {
  id: string
  sections: ZbaPatchSection[]
  /** System registration IDs are not backend plugin gates. */
  pluginKey?: string
}

export const basicFields = new Set([
  'aiModeEnabled', 'botPersona', 'groupCooldownSeconds', 'privateCooldownSeconds',
  'maxConcurrentReplies', 'maxContextMessages', 'maxMessagesPerConversation'
])

function capabilityFrom(spec: RegisteredPluginSettingsSpec): CapabilityConfiguration {
  return {
    ...spec, id: spec.pluginKey, pluginKey: spec.kind === 'plugin' ? spec.pluginKey : undefined,
    sections: [...new Set([
      ...(spec.sections ?? []), ...(spec.fields ?? []).map(field => field.section),
      ...(spec.enableField ? [spec.enableField.section] : []), ...(spec.fallbackSections ?? [])
    ])],
    fields: spec.fields?.map(field => ({ ...field })),
    enableField: spec.enableField ? { ...spec.enableField } : undefined,
    fallbackSections: spec.fallbackSections ? [...spec.fallbackSections] : undefined,
    secrets: [...spec.secrets], editor: { ...spec.editor }
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

/** Explicit ownership > the seven basic behavior fields > a system section fallback. */
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
  return fieldOwner(section, key) === scope
}
