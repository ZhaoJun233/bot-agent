import type { ZbaManagedDescriptor, ZbaManagedInspectResult } from '../../src/api/zba'
import type { MessagingCapabilitySnapshot } from '../../src/api/messaging-capabilities'
export const officialDescriptor: ZbaManagedDescriptor = {
  key: { kind: 'driver', key: { platformId: 'qq.official', accountScope: 'synthetic-app', instanceId: '' } },
  displayName: 'QQ 官方', runtimeState: 4, actions: [], credentialConfigured: true, descriptorRevision: 'synthetic-revision'
}
export function messagingSnapshot(): MessagingCapabilitySnapshot {
  return { observedAt: '2026-01-02T03:04:05Z', items: [{
    id: 'send', implementation: 0, policy: { state: 0, reasonCode: 'platform_policy_satisfied' },
    applicationGrant: { state: 2, reasonCode: 'application_grant_unverified' },
    conversation: { state: 2, reasonCode: 'conversation_not_selected' }, actor: { state: 2, reasonCode: 'actor_not_selected' }
  }] }
}
export const messagingResult = (): ZbaManagedInspectResult => ({ code: 0, descriptor: officialDescriptor, messaging: messagingSnapshot() })
