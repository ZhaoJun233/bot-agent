// @vitest-environment node
import { describe, expect, it } from 'vitest'
import http from 'node:http'
import type { AddressInfo } from 'node:net'
import { ZbaClient, ZbaNetworkError } from '../../src/api/zba'

type Outcome = 'resolved' | 'rejected' | 'wrong_error'

function outcome(promise: Promise<unknown>): Promise<Outcome> {
  return promise.then(
    () => 'resolved' as const,
    (error: unknown) => error instanceof ZbaNetworkError ? 'rejected' as const : 'wrong_error' as const
  )
}

describe('ZbaClient deadline covers the response body', () => {
  it('响应体一直不结束时，仍在 deadline 内拒绝', async () => {
    const stream = new ReadableStream({
      start() {
        // 故意不 enqueue、不 close：模拟 headers 已到但 body 永久挂起。
      }
    })
    const fetchImpl = (async () => new Response(stream, {
      status: 200,
      headers: { 'Content-Type': 'application/json' }
    })) as typeof fetch

    const result = await Promise.race([
      outcome(new ZbaClient({ fetchImpl, timeoutMs: 5 }).health()),
      new Promise<'still_pending'>((resolve) => setTimeout(() => resolve('still_pending'), 150))
    ])

    expect(result).toBe('rejected')
  })

  it('真实 HTTP headers 已到但 body 未完成时，遵守 deadline', async () => {
    let headersSent = false
    const server = http.createServer((_request, response) => {
      response.writeHead(200, { 'Content-Type': 'application/json' })
      response.write('{"status":')
      headersSent = true
    })
    await new Promise<void>((resolve) => server.listen(0, '127.0.0.1', resolve))
    const { port } = server.address() as AddressInfo

    try {
      const result = await Promise.race([
        outcome(new ZbaClient({
          baseUrl: `http://127.0.0.1:${port}/zba/v1`,
          timeoutMs: 100
        }).health()),
        new Promise<'still_pending'>((resolve) => setTimeout(() => resolve('still_pending'), 600))
      ])

      expect(headersSent).toBe(true)
      expect(result).toBe('rejected')
    } finally {
      server.closeAllConnections()
      await new Promise<void>((resolve) => server.close(() => resolve()))
    }
  })
})
