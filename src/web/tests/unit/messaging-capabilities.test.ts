import { describe, expect, it } from 'vitest'
import { ZbaClient } from '../../src/api/zba'
import { messagingResult, officialDescriptor } from '../fixtures/messaging'

function api(body: unknown): ZbaClient {
  return new ZbaClient({ baseUrl: '/zba/v1', token: 'synthetic', fetchImpl: async () => new Response(JSON.stringify(body), { status: 200 }) })
}
describe('message capability wire validation', () => {
  it('accepts current snapshots and absent fields from an older host', async () => {
    await expect(api(messagingResult()).inspectCapability(officialDescriptor.key)).resolves.toHaveProperty('messaging')
    await expect(api({ code: 0, descriptor: officialDescriptor }).inspectCapability(officialDescriptor.key)).resolves.toHaveProperty('code', 0)
  })
  it.each(['state', 'timestamp', 'duplicate', 'reason'])('rejects malformed %s observations', async kind => {
    const result = messagingResult()
    if (kind === 'state') Object.assign(result.messaging!.items[0]!.policy, { state: 8 })
    if (kind === 'timestamp') result.messaging!.observedAt = 'not-a-time'
    if (kind === 'duplicate') result.messaging!.items.push(result.messaging!.items[0]!)
    if (kind === 'reason') result.messaging!.items[0]!.policy.reasonCode = 'unexpected raw detail'
    await expect(api(result).inspectCapability(officialDescriptor.key)).rejects.toMatchObject({ code: 'invalid_json', status: 200 })
  })
})
