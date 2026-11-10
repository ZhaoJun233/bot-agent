import { reactive, shallowRef } from 'vue'
import type { ZbaClient } from '../../api/zba'
import { serviceLabel } from './pending-intent'

/** Same-document receipt guard. No client, token, summary or decision body is retained. */
export interface PendingDecision {
  readonly id: string
  readonly service: string
  readonly identity: string
  readonly expiresAt: number
  busy: boolean
  stale: boolean
  unknown: boolean
  retryable: boolean
}
const entries = shallowRef<PendingDecision[]>([])
const LIMIT = 32

export function pendingDecisions(client: ZbaClient): PendingDecision[] {
  return entries.value.filter(item => item.service === serviceLabel(client) && item.identity === client.identityLabel)
}

export function beginDecision(client: ZbaClient, id: string, expiresAt: string): PendingDecision {
  // In-flight associations are never evicted, even after their approval expires.
  entries.value = entries.value.filter(item => item.busy || !(item.expiresAt <= Date.now()))
  const current = pendingDecisions(client)
  if (current.some(item => item.id === id)) throw new Error('pending_decision_exists')
  if (entries.value.length >= LIMIT) throw new Error('pending_decision_capacity')
  const record = reactive<PendingDecision>({
    id, service: serviceLabel(client), identity: client.identityLabel, expiresAt: Date.parse(expiresAt),
    busy: true, stale: false, unknown: false, retryable: false
  })
  entries.value = [...entries.value, record]
  return record
}

export function markDecision(record: PendingDecision, unknown: boolean, retryable: boolean): void {
  record.stale = true
  record.unknown = unknown
  record.retryable = retryable
}

export function forgetDecision(record: PendingDecision): void {
  entries.value = entries.value.filter(item => item !== record)
}

/** Synthetic tests only; session locking must preserve the guard. */
export function clearPendingDecisions(): void { entries.value = [] }
