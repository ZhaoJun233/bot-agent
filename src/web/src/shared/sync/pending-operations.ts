import { ref, toRaw } from 'vue'
import type { ZbaClient } from '../../api/zba'

export type PendingOperationKind = 'configuration' | 'quota' | 'plugin' | 'plugin-settings'
export interface PendingOperation {
  readonly key: string
  readonly kind: PendingOperationKind
  readonly target: string
  readonly meta: Readonly<Record<string, string | number | boolean>>
  readonly state: 'in-flight' | 'unknown' | 'settled'
  readonly updatedAt: number
}
interface ScopedOperation extends PendingOperation { readonly scope: string }
const records = ref<ScopedOperation[]>([])
const localIdentities = new WeakMap<object, number>()
let nextIdentity = 0
const SETTLED_TTL = 10 * 60 * 1000
const ACTIVE_LIMIT = 32

// No client, token, secret, request body or result is kept in this registry.
function scope(client: ZbaClient): string {
  client = toRaw(client)
  let identity = client.identityLabel
  if (typeof identity !== 'string' || !identity) {
    let local = localIdentities.get(client)
    if (local === undefined) { local = ++nextIdentity; localIdentities.set(client, local) }
    identity = 'local-' + local
  }
  return (globalThis.location?.origin ?? 'local') + '|' + (client.baseUrl ?? '/zba/v1') + '|' + identity
}
function prune(): void {
  const cutoff = Date.now() - SETTLED_TTL
  const next = records.value.filter(r => r.state !== 'settled' || r.updatedAt >= cutoff)
  if (next.length !== records.value.length) records.value = next
}
export function pendingOperations(client: ZbaClient, kind?: PendingOperationKind, target?: string): PendingOperation[] {
  prune()
  const owner = scope(client)
  return records.value.filter(r => r.scope === owner && (!kind || r.kind === kind) && (target === undefined || r.target === target))
}
export function getPendingOperation(client: ZbaClient, kind: PendingOperationKind, target: string): PendingOperation | null {
  return pendingOperations(client, kind, target).find(r => r.state !== 'settled') ?? null
}
export function beginOperation(client: ZbaClient, input: { key: string; kind: PendingOperationKind; target: string; meta?: Readonly<Record<string, string | number | boolean>> }): PendingOperation {
  const owner = scope(client)
  const active = pendingOperations(client).filter(r => r.state !== 'settled')
  if (active.some(r => r.key === input.key || (r.kind === input.kind && r.target === input.target))) throw new Error('pending_operation_exists')
  if (active.length >= ACTIVE_LIMIT) throw new Error('pending_operation_capacity')
  const record: ScopedOperation = { key: input.key, kind: input.kind, target: input.target, meta: Object.freeze({ ...input.meta }), scope: owner, state: 'in-flight', updatedAt: Date.now() }
  records.value = [...records.value, record]
  return record
}
export function markOperation(client: ZbaClient, key: string, state: 'unknown' | 'settled'): void {
  const owner = scope(client)
  records.value = records.value.map(r => r.scope === owner && r.key === key && r.state !== 'settled' ? { ...r, state, updatedAt: Date.now() } : r)
  prune()
}
/** Only for isolated synthetic tests, never called on lock or identity changes. */
export function clearPendingOperations(): void { records.value = [] }
