import { afterEach, describe, expect, it, vi } from 'vitest'
import { ZbaClient, ZbaNetworkError } from '../../src/api/zba'

const epoch = 'a'.repeat(32)
const entry = { id: epoch + ':1', sequence: '1', time: 1793773800000, level: 'warn', source: 'Agent', message: '安全摘要', redacted: true }
const feed = { capacity: 600, cursor: 'opaque-cursor', hasMore: false, items: [entry] }
const response = (body: unknown) => new Response(JSON.stringify(body), { headers: { 'Content-Type': 'application/json' } })
function setup(body: unknown) {
  const fetchImpl = vi.fn<typeof fetch>(async () => response(body))
  return { fetchImpl, client: new ZbaClient({ baseUrl: 'https://example.com/zba/v1', token: 'synthetic-token', fetchImpl }) }
}
afterEach(() => { vi.useRealTimers(); vi.restoreAllMocks() })
describe('运行日志游标 feed', () => {
  it('使用只读 JSON 路由和不透明游标，省略首次游标', async () => {
    const { client, fetchImpl } = setup(feed)
    expect(await client.listRuntimeLogFeed()).toEqual(feed)
    await client.listRuntimeLogFeed('a/b+c')
    expect(fetchImpl.mock.calls[0]![0]).toBe('https://example.com/zba/v1/logs/feed')
    expect(fetchImpl.mock.calls[1]![0]).toBe('https://example.com/zba/v1/logs/feed?cursor=a%2Fb%2Bc')
    expect(new Headers(fetchImpl.mock.calls[0]![1]?.headers).get('Authorization')).toBe('Bearer synthetic-token')
  })
  it.each([
    { ...feed, items: [{ ...entry, id: 'unreliable' }] },
    { ...feed, items: [{ ...entry, sequence: '01' }] },
    { ...feed, items: [entry, entry] },
    { ...feed, items: Array(201).fill(entry) },
    { ...feed, hasMore: 'true' }, { ...feed, cursor: '' }, { ...feed, cursor: 'x'.repeat(56) },
    { ...feed, hasMore: true, items: [] },
    { ...feed, items: [entry, { ...entry, sequence: '2', id: 'b'.repeat(32) + ':2' }] },
    { ...feed, resetReason: 'anything' }, { ...feed, omittedCount: -1 },
    { ...feed, items: [{ ...entry, message: 'x'.repeat(513) }] }
  ])('拒绝无效或无界 feed %#', async bad => {
    await expect(setup(bad).client.listRuntimeLogFeed()).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('丢弃非契约字段，拒绝不前进的立即续传', async () => {
    const result = await setup({ ...feed, raw: 'must-not-retain', items: [{ ...entry, raw: 'must-not-retain' }] }).client.listRuntimeLogFeed()
    expect(result).toEqual(feed)
    expect(JSON.stringify(result)).not.toContain('must-not-retain')
    await expect(setup({ ...feed, hasMore: true }).client.listRuntimeLogFeed(feed.cursor)).rejects.toMatchObject({ code: 'invalid_json' })
  })
  it('保留超过 JS 安全整数的序号', async () => {
    const sequence = '9007199254740993'
    const result = await setup({ ...feed, items: [{ ...entry, sequence, id: epoch + ':' + sequence }] }).client.listRuntimeLogFeed()
    expect(result.items[0]!.sequence).toBe(sequence)
  })
  it('调用者取消能结束忽略 abort 的请求，并释放监听', async () => {
    const caller = new AbortController()
    const remove = vi.spyOn(caller.signal, 'removeEventListener')
    let actualSignal: AbortSignal | null | undefined
    const fetchImpl = vi.fn<typeof fetch>((_, init) => { actualSignal = init?.signal; return new Promise(() => {}) })
    const client = new ZbaClient({ fetchImpl })
    const result = client.listRuntimeLogFeed(undefined, caller.signal)
    const rejected = expect(result).rejects.toBeInstanceOf(ZbaNetworkError)
    caller.abort()
    await rejected
    expect(actualSignal?.aborted).toBe(true)
    expect(remove).toHaveBeenCalledWith('abort', expect.any(Function))
  })
  it('超时覆盖已收到 headers 但挂起的 body', async () => {
    vi.useFakeTimers()
    const body = response(feed)
    vi.spyOn(body, 'text').mockImplementation(() => new Promise(() => {}))
    let signal: AbortSignal | null | undefined
    const client = new ZbaClient({ timeoutMs: 1000, fetchImpl: async (_, init) => { signal = init?.signal; return body } })
    const rejected = expect(client.listRuntimeLogFeed()).rejects.toBeInstanceOf(ZbaNetworkError)
    await vi.advanceTimersByTimeAsync(1000)
    await rejected
    expect(signal?.aborted).toBe(true)
    expect(vi.getTimerCount()).toBe(0)
  })
  it('已取消时不发请求；成功后也摘除调用者监听', async () => {
    const { client, fetchImpl } = setup(feed)
    const caller = new AbortController()
    const remove = vi.spyOn(caller.signal, 'removeEventListener')
    await client.listRuntimeLogFeed(undefined, caller.signal)
    expect(remove).toHaveBeenCalledWith('abort', expect.any(Function))
    caller.abort()
    await expect(client.listRuntimeLogFeed(undefined, caller.signal)).rejects.toBeInstanceOf(ZbaNetworkError)
    expect(fetchImpl).toHaveBeenCalledTimes(1)
  })
})
