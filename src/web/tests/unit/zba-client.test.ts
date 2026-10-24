import { describe, expect, it, vi } from 'vitest'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaConfigurationSaveRequest, type ZbaMeta, type ZbaStatus } from '../../src/api/zba'
import metaFixture from '../fixtures/meta.json'
import statusFixture from '../fixtures/status.json'

const META = metaFixture as ZbaMeta
const STATUS = statusFixture as ZbaStatus

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers }
  })
}

function fakeFetch(response: Response | (() => Response)): { fetchImpl: typeof fetch; calls: Array<{ url: string; init: RequestInit }> } {
  const calls: Array<{ url: string; init: RequestInit }> = []
  const create = typeof response === 'function' ? response : () => response.clone()
  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), init: init ?? {} })
    return create()
  }) as typeof fetch
  return { fetchImpl, calls }
}

describe('ZbaClient', () => {
  it('health 不发送 Authorization，并归一化 baseUrl', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse({
      status: 'healthy',
      ready: true,
      configured: true,
      contractVersion: 'zba-1.0',
      checkedAt: '2026-10-20T12:00:00+00:00'
    }))
    const client = new ZbaClient({ baseUrl: 'http://z.example/zba/v1/', token: 'secret-token', fetchImpl })

    const health = await client.health()

    expect(health.contractVersion).toBe('zba-1.0')
    expect(calls).toHaveLength(1)
    expect(calls[0]?.url).toBe('http://z.example/zba/v1/health')
    expect(new Headers(calls[0]?.init.headers).get('Authorization')).toBeNull()
  })

  it('meta/status 发送 Bearer 但不把令牌放进 URL', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(META))
    const client = new ZbaClient({ baseUrl: '/zba/v1', token: ' read-token ', fetchImpl })

    await client.meta()

    expect(calls[0]?.url).toBe('/zba/v1/meta')
    expect(calls[0]?.url).not.toContain('read-token')
    expect(new Headers(calls[0]?.init.headers).get('Authorization')).toBe('Bearer read-token')
  })

  it('把 401 错误体归一化为安全错误码与 request id', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      error: { code: 'unauthorized', message: '缺少 Bearer 凭据。', reason: null, backendCode: null }
    }, 401, { 'X-Request-Id': 'zr-synthetic-0001' }))
    const client = new ZbaClient({ token: 'bad', fetchImpl })

    await expect(client.status()).rejects.toMatchObject({
      name: 'ZbaRequestError',
      status: 401,
      code: 'unauthorized',
      requestId: 'zr-synthetic-0001'
    })
  })

  it('区分 503 zba_not_configured 与其它 503', async () => {
    const notConfigured = fakeFetch(jsonResponse({
      error: { code: 'zba_not_configured', message: 'ZBA 未配置访问凭据。' }
    }, 503))
    await expect(new ZbaClient({ fetchImpl: notConfigured.fetchImpl }).status())
      .rejects.toMatchObject({ status: 503, code: 'zba_not_configured' })

    const unavailable = fakeFetch(jsonResponse({ error: { code: 'unavailable', message: '暂不可用。' } }, 503))
    await expect(new ZbaClient({ fetchImpl: unavailable.fetchImpl }).status())
      .rejects.toMatchObject({ status: 503, code: 'unavailable' })
  })

  it('把 429 保留为可解释的限流错误', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      error: { code: 'too_many_authentication_failures', message: '认证失败次数过多，请稍后重试。' }
    }, 429))
    await expect(new ZbaClient({ fetchImpl }).status())
      .rejects.toMatchObject({ status: 429, code: 'too_many_authentication_failures' })
  })

  it('网络失败归一化为 ZbaNetworkError，不泄露底层异常文本', async () => {
    const fetchImpl = (async () => { throw new Error('synthetic socket failure') }) as typeof fetch
    const client = new ZbaClient({ fetchImpl })
    const error = await client.health().catch((cause: unknown) => cause)

    expect(error).toBeInstanceOf(ZbaNetworkError)
    expect((error as Error).message).toBe('network_unavailable')
  })

  it('checkAdmin 只把 404 task_not_found 视为 admin，403 视为 read', async () => {
    const adminFetch = fakeFetch(jsonResponse({ error: { code: 'task_not_found', message: '任务不存在。' } }, 404))
    await expect(new ZbaClient({ token: 'admin', fetchImpl: adminFetch.fetchImpl }).checkAdmin())
      .resolves.toBe(true)

    const readFetch = fakeFetch(jsonResponse({ error: { code: 'forbidden', message: '需要管理员凭据。' } }, 403))
    await expect(new ZbaClient({ token: 'read', fetchImpl: readFetch.fetchImpl }).checkAdmin())
      .resolves.toBe(false)
  })

  it('checkAdmin 不把代理/旧版本的其它 404 误判为 admin', async () => {
    const proxyMissing = fakeFetch(jsonResponse({
      error: { code: 'route_not_found', message: '路由不存在。' }
    }, 404))
    await expect(new ZbaClient({ token: 'read', fetchImpl: proxyMissing.fetchImpl }).checkAdmin())
      .rejects.toMatchObject({ status: 404, code: 'route_not_found' })

    const nonJson = fakeFetch(new Response('<html>not found</html>', { status: 404 }))
    await expect(new ZbaClient({ token: 'read', fetchImpl: nonJson.fetchImpl }).checkAdmin())
      .rejects.toMatchObject({ status: 404, code: 'http_error' })
  })

  it('checkAdmin 只接受 200 的有效任务结果，不接受 200 HTML', async () => {
    const valid = fakeFetch(jsonResponse({ id: 'task-synthetic', state: 3, updatedAt: '2026-10-20T12:00:00+00:00' }))
    await expect(new ZbaClient({ token: 'admin', fetchImpl: valid.fetchImpl }).checkAdmin())
      .resolves.toBe(true)

    const html = fakeFetch(new Response('<html>proxy ok</html>', { status: 200 }))
    await expect(new ZbaClient({ token: 'admin', fetchImpl: html.fetchImpl }).checkAdmin())
      .rejects.toMatchObject({ status: 200, code: 'invalid_json' })
  })

  it('非 JSON 的成功响应不当作成功数据', async () => {
    const fetchImpl = (async () => new Response('<html>not json</html>', { status: 200 })) as typeof fetch
    await expect(new ZbaClient({ fetchImpl }).health())
      .rejects.toBeInstanceOf(ZbaRequestError)
  })

  it('不把错误体里的未知字段带入 UI 错误', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      error: { code: 'bad_request', message: '请求无效。', detail: 'synthetic private payload' }
    }, 400))
    const error = await new ZbaClient({ token: 'x', fetchImpl }).status().catch((cause: unknown) => cause)
    expect(error).toBeInstanceOf(ZbaRequestError)
    expect((error as ZbaRequestError).message).not.toContain('synthetic private payload')
    expect((error as unknown as { detail?: string }).detail).toBeUndefined()
  })

  it('health 的 503 若带 ZbaHealth 本体则作为 liveness 结果，不当作传输失败', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      status: 'unhealthy',
      ready: false,
      configured: true,
      contractVersion: 'zba-1.0',
      checkedAt: '2026-10-20T12:00:00+00:00'
    }, 503))

    await expect(new ZbaClient({ fetchImpl }).health()).resolves.toMatchObject({
      status: 'unhealthy',
      ready: false,
      configured: true
    })
  })

  it('health 的 503 若是 error 体则抛出可解释错误', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      error: { code: 'zba_not_configured', message: 'ZBA 未配置访问凭据。' }
    }, 503))

    await expect(new ZbaClient({ fetchImpl }).health())
      .rejects.toMatchObject({ status: 503, code: 'zba_not_configured' })
  })

  it('请求超时归一化为 ZbaNetworkError，不无限 loading', async () => {
    const fetchImpl = ((_: RequestInfo | URL, init?: RequestInit) => new Promise((_, reject) => {
      init?.signal?.addEventListener('abort', () => reject(new Error('synthetic abort')))
    })) as typeof fetch

    await expect(new ZbaClient({ fetchImpl, timeoutMs: 5 }).health())
      .rejects.toBeInstanceOf(ZbaNetworkError)
  })

  it('没有令牌时不伪造 Authorization', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(META))
    await new ZbaClient({ fetchImpl }).meta()
    expect(new Headers(calls[0]?.init.headers).get('Authorization')).toBeNull()
  })

  it('不会在令牌变化时复用旧 client', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(META))
    const read = new ZbaClient({ token: 'read-token', fetchImpl })
    const admin = new ZbaClient({ token: 'admin-token', fetchImpl })

    await read.meta()
    await admin.meta()

    expect(new Headers(calls[0]?.init.headers).get('Authorization')).toBe('Bearer read-token')
    expect(new Headers(calls[1]?.init.headers).get('Authorization')).toBe('Bearer admin-token')
    expect(vi.isMockFunction(fetchImpl)).toBe(false)
  })

  it('status 拒绝合法 JSON 的空对象、缺字段与错误嵌套，不冒充空态', async () => {
    const empty = fakeFetch(jsonResponse({}))
    await expect(new ZbaClient({ token: 'x', fetchImpl: empty.fetchImpl }).status())
      .rejects.toMatchObject({ name: 'ZbaRequestError', status: 200, code: 'invalid_json' })

    const missingComponents = fakeFetch(jsonResponse({ ...STATUS, components: undefined }))
    await expect(new ZbaClient({ token: 'x', fetchImpl: missingComponents.fetchImpl }).status())
      .rejects.toMatchObject({ status: 200, code: 'invalid_json' })

    const badComponent = fakeFetch(jsonResponse({
      ...STATUS,
      components: [{ ...STATUS.components[0], available: 'yes' }]
    }))
    await expect(new ZbaClient({ token: 'x', fetchImpl: badComponent.fetchImpl }).status())
      .rejects.toMatchObject({ status: 200, code: 'invalid_json' })
  })

  it('配置保存不会把 HTTP 503 上的成功码业务体当成成功', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      code: 0,
      replayed: false,
      currentVersion: '2',
      errors: []
    }, 503))
    const client = new ZbaClient({ token: 'x', fetchImpl })
    const request: ZbaConfigurationSaveRequest = {
      contractRevision: 'cfg-0.3.3',
      expectedVersion: '1',
      key: 'cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210',
      changes: {},
      secrets: []
    }

    await expect(client.saveConfiguration(request)).rejects.toMatchObject({
      name: 'ZbaRequestError',
      status: 503,
      code: 'http_error'
    })
  })

  it('配置保存接受 HTTP 503 上合法的 UnknownCommit 结果', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({
      code: 12,
      replayed: false,
      currentVersion: '2',
      errors: []
    }, 503))
    const client = new ZbaClient({ token: 'x', fetchImpl })
    const request: ZbaConfigurationSaveRequest = {
      contractRevision: 'cfg-0.3.3',
      expectedVersion: '1',
      key: 'cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210',
      changes: {},
      secrets: []
    }

    await expect(client.saveConfiguration(request)).resolves.toMatchObject({ code: 12 })
  })

  it('配置核对不会把 HTTP 503 上的 NotFound 业务体当成未落盘', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({ code: 14 }, 503))
    const client = new ZbaClient({ token: 'x', fetchImpl })

    await expect(client.findConfiguration('cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210'))
      .rejects.toMatchObject({ name: 'ZbaRequestError', status: 503, code: 'http_error' })
  })

  it('配置核对在 HTTP 404 上接受 NotFound 结果，允许换新键重存', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({ code: 14 }, 404))
    const client = new ZbaClient({ token: 'x', fetchImpl })

    await expect(client.findConfiguration('cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210'))
      .resolves.toMatchObject({ code: 14 })
  })

  it('拒绝可选字段类型错误的任务结果回包', async () => {
    const invalidBodies = [
      { id: 'task-synthetic', state: 3, updatedAt: '2026-10-20T12:00:00Z', available: true, summaryTruncated: false, summaryChars: 0, summary: {} },
      { id: 'task-synthetic', state: 3, updatedAt: '2026-10-20T12:00:00Z', available: true, summaryTruncated: false, summaryChars: 0, reasonCode: 42 },
      { id: 'task-synthetic', state: 3, updatedAt: '2026-10-20T12:00:00Z', available: true, summaryTruncated: false, summaryChars: 0, errorCode: [] },
      { id: 'task-synthetic', state: 3, updatedAt: '2026-10-20T12:00:00Z', available: true, summaryTruncated: false, summaryChars: 0, summary: null }
    ]
    for (const body of invalidBodies) {
      const { fetchImpl } = fakeFetch(jsonResponse(body))
      await expect(new ZbaClient({ fetchImpl }).getTaskResult('task-synthetic'))
        .rejects.toMatchObject({ status: 200, code: 'invalid_json' })
    }
  })

  it('配置核对在 HTTP 409 上接受代际变化结果', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({ code: 6 }, 409))
    const client = new ZbaClient({ token: 'x', fetchImpl })

    await expect(client.findConfiguration('cfg1:0123456789abcdef0123456789abcdef:1760000000:fedcba9876543210fedcba9876543210'))
      .resolves.toMatchObject({ code: 6 })
  })

})
