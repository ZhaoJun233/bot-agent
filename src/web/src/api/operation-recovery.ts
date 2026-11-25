export interface OperationRecoveryReceipt {
  formatVersion: 1
  owner: string
  scopeDigest: string
  keyDigest: string
  requestDigest: string
  contract: string
  contractVersion: string
  generation: string | null
  state: 'committed' | 'rejected' | 'unknown'
  provenance: 'recorded' | 'legacy'
}
export type OperationFindResult =
  | { found: false; receipt: null; details: 'unavailable' }
  | { found: true; receipt: OperationRecoveryReceipt; details: 'unavailable' }

const digest = (value: unknown): value is string => typeof value === 'string' && /^[0-9a-f]{64}$/i.test(value)
const token = (value: unknown, max: number): value is string => typeof value === 'string'
  && value.length > 0 && value.length <= max && /^[A-Za-z0-9._:-]+$/.test(value)

export function isOperationFindResult(value: unknown): value is OperationFindResult {
  if (!value || typeof value !== 'object') return false
  const result = value as Record<string, unknown>
  if (result.details !== 'unavailable') return false
  if (result.found === false) return result.receipt === null
  if (result.found !== true || !result.receipt || typeof result.receipt !== 'object') return false
  const receipt = result.receipt as Record<string, unknown>
  return receipt.formatVersion === 1 && token(receipt.owner, 80) && digest(receipt.scopeDigest)
    && digest(receipt.keyDigest) && digest(receipt.requestDigest) && token(receipt.contract, 128)
    && token(receipt.contractVersion, 64) && ['committed', 'rejected', 'unknown'].includes(String(receipt.state))
    && (receipt.provenance === 'recorded' ? token(receipt.generation, 128)
      : receipt.provenance === 'legacy' && receipt.generation === null && receipt.state === 'committed')
}
