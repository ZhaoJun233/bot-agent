export type MessagingConditionState = 0 | 1 | 2 | 3
export interface MessagingCondition { state: MessagingConditionState; reasonCode: string }
export interface MessagingCapability {
  id: string
  implementation: 0 | 1
  policy: MessagingCondition
  applicationGrant: MessagingCondition
  conversation: MessagingCondition
  actor: MessagingCondition
}
export interface MessagingCapabilitySnapshot { observedAt: string; items: MessagingCapability[] }

function record(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value)
}
function condition(value: unknown): value is MessagingCondition {
  return record(value) && typeof value.state === 'number' && [0, 1, 2, 3].includes(value.state)
    && typeof value.reasonCode === 'string' && /^[a-z][a-z0-9_]{0,79}$/.test(value.reasonCode)
}
export function isMessagingSnapshot(value: unknown): value is MessagingCapabilitySnapshot {
  return record(value) && typeof value.observedAt === 'string' && Number.isFinite(Date.parse(value.observedAt))
    && Array.isArray(value.items) && value.items.length <= 64
    && value.items.every(item => record(item) && typeof item.id === 'string' && /^[a-z][a-z0-9_]{0,39}$/.test(item.id)
      && (item.implementation === 0 || item.implementation === 1) && condition(item.policy)
      && condition(item.applicationGrant) && condition(item.conversation) && condition(item.actor))
    && new Set(value.items.map(item => item.id)).size === value.items.length
}
