import { describe, expect, it, vi } from 'vitest'
import { ZbaClient } from '../../src/api/zba'

const entry = { time: 1793773800000, level: 'warn', source: 'Agent', message: '模型输出为空（empty_reply）', redacted: true }
function setup(body: unknown, status = 200) {
  const fetchImpl = vi.fn<typeof fetch>(async () => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }))
  return { fetchImpl, client: new ZbaClient({ baseUrl: 'https://example.com/zba/v1', token: 'synthetic-read-token', fetchImpl }) }
}
describe('运行日志只读客户端', () => {
  it('访问 ZBA 而不是原始日志端点，携带只读鉴权', async () => {
    const { client, fetchImpl } = setup({ capacity: 600, items: [entry] })
    expect(await client.listRuntimeLogs(100)).toEqual({ capacity: 600, items: [entry] })
    const [url, init] = fetchImpl.mock.calls[0]!
    expect(String(url)).toBe('https://example.com/zba/v1/logs?limit=100')
    expect(init?.method).toBe('GET')
    expect(new Headers(init?.headers).get('Authorization')).toBe('Bearer synthetic-read-token')
  })
  it.each([
    { ...entry, time: 'today' }, { ...entry, time: -1 }, { ...entry, time: 1e17 },
    { ...entry, level: 'debug' }, { ...entry, redacted: undefined },
    { ...entry, message: 'x'.repeat(513) }, { ...entry, source: 'x'.repeat(65) }
  ])('拒绝无效日志行 %#', async bad => {
    const { client } = setup({ capacity: 600, items: [bad] })
    await expect(client.listRuntimeLogs()).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('拒绝超出容量的响应', async () => {
    const { client } = setup({ capacity: 600, items: Array.from({ length: 601 }, () => entry) })
    await expect(client.listRuntimeLogs()).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it.each([401, 403, 503])('HTTP %i 不能伪装成空日志', async status => {
    const { client } = setup({ code: 'unavailable', message: 'hidden' }, status)
    await expect(client.listRuntimeLogs()).rejects.toMatchObject({ status })
  })
})
