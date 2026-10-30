import { describe, expect, it, vi } from 'vitest'
import { ZbaClient, ZbaRequestError } from '../../src/api/zba'
import { quota } from '../fixtures/quotas'
const intent = { sourceKey: 'group:10001', expectedRevision: '1', idempotencyKey: 'synthetic-quota-key', dailyTokenLimit: 100000 }
function setup(body: unknown, status = 200) {
  const fetchImpl = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  return { client: new ZbaClient({ token: 'synthetic-admin-token', fetchImpl }), fetchImpl }
}
describe('quota API memory-auth contract', () => {
  it('uses absolute API roots from /web and sends exact CAS/receipt bodies', async () => {
    const { client, fetchImpl } = setup({ items: [quota()] })
    expect(await client.listQuotas()).toEqual({ items: [quota()] })
    expect(fetchImpl.mock.calls[0]?.[0]).toBe('/zba/v1/quotas')
    const save = setup({ quota: quota() }); await save.client.saveQuota(intent)
    const call = save.fetchImpl.mock.calls[0] as unknown as [string, RequestInit]
    expect(call[0]).toBe('/zba/v1/quotas/save'); expect(call[1].method).toBe('POST')
    expect(JSON.parse(call[1].body as string)).toEqual(intent)
    expect(new Headers(call[1].headers).get('Authorization')).toBe('Bearer synthetic-admin-token')
    const find = setup({ found: true, quota: quota() }); await find.client.findQuota(intent.idempotencyKey)
    expect(JSON.parse((find.fetchImpl.mock.calls[0] as unknown as [string, RequestInit])[1].body as string)).toEqual({ idempotencyKey: intent.idempotencyKey })
    expect(await setup({ found: false }).client.findQuota(intent.idempotencyKey)).toEqual({ found: false })
  })
  it.each([{}, { items: [{}] }, { items: [quota({ revision: '' })] }, { items: [quota({ revision: 2 as unknown as string })] },
    { items: [quota({ usedTokens: -1 })] }, { items: [quota({ usedCompletionTokens: 1.5 })] }, { items: [quota({ remainingTokens: Number.MAX_SAFE_INTEGER + 1 })] },
    { items: [quota({ dailyTokenLimit: 0 })] }, { items: [quota({ maximumDailyTokenLimit: 2147483648 })] },
    { items: [quota({ nextResetAt: 'tomorrow' })] }, { items: [quota({ nextResetAt: '2026-10-30T00:00:00' })] },
    { items: [quota({ resetDate: '2026-02-30' })] }, { items: [quota({ energySaving: 'false' as unknown as boolean })] },
    { items: [quota(), quota()] }])('rejects malformed list %#', async (body) => {
    await expect(setup(body).client.listQuotas()).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it.each([{ found: true }, { found: false, quota: quota() }, { found: 'true', quota: quota() }, { found: true, quota: {} }])('rejects contradictory receipt %#', async body => {
    await expect(setup(body).client.findQuota(intent.idempotencyKey)).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('does not accept error-status success bodies or retry a mutation', async () => {
    for (const status of [401, 403, 409, 500, 503]) {
      const { client, fetchImpl } = setup({ quota: quota() }, status)
      await expect(client.saveQuota(intent)).rejects.toBeInstanceOf(ZbaRequestError)
      expect(fetchImpl).toHaveBeenCalledTimes(1)
    }
    const mismatch = setup({ quota: quota({ sourceKey: 'group:20002' }) })
    await expect(mismatch.client.saveQuota(intent)).rejects.toMatchObject({ code: 'invalid_json' })
    const changed = setup({ quota: quota({ dailyTokenLimit: 90000 }) })
    await expect(changed.client.saveQuota(intent)).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it.each(['quota_revision_conflict', 'idempotency_conflict'])('preserves exact 409 error code %s', async code => {
    await expect(setup({ error: { code, message: 'synthetic error' } }, 409).client.saveQuota(intent)).rejects.toMatchObject({ status: 409, code })
  })
})
