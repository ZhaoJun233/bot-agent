import { describe, expect, it } from 'vitest'
import { ZbaClient, ZbaNetworkError, ZbaRequestError, type ZbaPluginCatalogItem } from '../../src/api/zba'
import { syntheticManifest } from '../fixtures/provider-marketplace'

function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json', ...headers }
  })
}

type FetchCall = { url: string; init: RequestInit }
function fakeFetch(response: Response | (() => Response)): { fetchImpl: typeof fetch; calls: FetchCall[] } {
  const calls: FetchCall[] = []
  const create = typeof response === 'function' ? response : () => response.clone()
  const fetchImpl = (async (input: RequestInfo | URL, init?: RequestInit) => {
    calls.push({ url: String(input), init: init ?? {} })
    return create()
  }) as typeof fetch
  return { fetchImpl, calls }
}

const entry: ZbaPluginCatalogItem = {
  manifest: syntheticManifest,
  installedVersion: null,
  enabled: false,
  status: 'available',
  restartRequired: false,
  name: '示例插件',
  summary: '示例功能',
  category: '工具',
  repository: 'https://github.com/example-owner/example-plugin'
}

const catalog = {
  revision: 'rev-1',
  source: 'https://github.com/example-owner/example-index',
  trustNotice: '插件不是沙箱：请仅安装你信任的代码。',
  categories: ['工具', '消息'],
  plugins: [entry]
}

const operation = {
  operation: {
    id: 'op-1',
    pluginId: syntheticManifest.id,
    kind: 'install' as const,
    status: 'restart_required' as const,
    restartRequired: true
  },
  revision: 'rev-2'
}

const settings = {
  revision: 'rev-3',
  schema: syntheticManifest.settings,
  values: { caption: '示例' },
  configuredSecrets: { secret: true },
  restartRequired: false
}

describe('ZbaClient 插件市场', () => {
  it('读取目录并按需拼接 q/category，不拉取清单里的地址', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(catalog))
    const client = new ZbaClient({ baseUrl: 'http://z.example/zba/v1', token: 'read-token', fetchImpl })

    const result = await client.pluginCatalog({ q: ' 示例 ', category: '工具' })

    expect(result.revision).toBe('rev-1')
    expect(result.plugins[0]?.manifest.id).toBe('example.plugin')
    expect(calls).toHaveLength(1)
    expect(calls[0]?.url).toBe('http://z.example/zba/v1/plugins/catalog?q=%E7%A4%BA%E4%BE%8B&category=%E5%B7%A5%E5%85%B7')
    expect(calls[0]?.init.method).toBe('GET')
  })

  it('目录查询为空时不带查询参数', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(catalog))
    const client = new ZbaClient({ baseUrl: '/zba/v1', fetchImpl })

    await client.pluginCatalog()
    await client.pluginCatalog({ q: '   ', category: '' })

    expect(calls.map((call) => call.url)).toEqual(['/zba/v1/plugins/catalog', '/zba/v1/plugins/catalog'])
  })

  it('目录缺信任提示或清单 sha256 漂移时判为非法响应', async () => {
    const missingNotice = fakeFetch(jsonResponse({ ...catalog, trustNotice: undefined }))
    await expect(new ZbaClient({ fetchImpl: missingNotice.fetchImpl }).pluginCatalog())
      .rejects.toMatchObject({ name: 'ZbaRequestError', code: 'invalid_json', status: 200 })

    const badSha = fakeFetch(jsonResponse({ ...catalog, plugins: [{ ...entry, manifest: { ...syntheticManifest, sha256: 'invalid' } }] }))
    await expect(new ZbaClient({ fetchImpl: badSha.fetchImpl }).pluginCatalog())
      .rejects.toMatchObject({ code: 'invalid_json' })
  })

  it('解析数据源只把地址交给主机，不在浏览器里请求它', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse({ manifest: syntheticManifest, trustNotice: '仅安装你信任的代码。' }))
    const client = new ZbaClient({ baseUrl: '/zba/v1', token: 't', fetchImpl })

    const result = await client.resolvePluginSource('https://github.com/example-owner/example-plugin')

    expect(result.manifest.id).toBe('example.plugin')
    expect(calls[0]?.url).toBe('/zba/v1/plugins/sources/resolve')
    expect(calls[0]?.init.method).toBe('POST')
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({ sourceUrl: 'https://github.com/example-owner/example-plugin' })
  })

  it('已安装列表与目录共用条目校验', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse({ revision: 'rev-9', plugins: [{ ...entry, installedVersion: '1.0.0', enabled: true, status: 'installed' }] }))
    const result = await new ZbaClient({ baseUrl: '/zba/v1', fetchImpl }).installedPlugins()

    expect(result.plugins[0]?.installedVersion).toBe('1.0.0')
    expect(calls[0]?.url).toBe('/zba/v1/plugins/installed')
  })

  it('安装必须带上字面量信任确认与幂等键', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(operation))
    const client = new ZbaClient({ baseUrl: '/zba/v1', fetchImpl })

    const result = await client.installPlugin({
      manifest: syntheticManifest,
      expectedRevision: 'rev-1',
      idempotencyKey: 'plugin-op-example',
      trustAcknowledged: true
    })

    expect(result.operation.status).toBe('restart_required')
    expect(result.revision).toBe('rev-2')
    expect(calls[0]?.url).toBe('/zba/v1/plugins/install')
    expect(JSON.parse(String(calls[0]?.init.body))).toMatchObject({
      expectedRevision: 'rev-1',
      idempotencyKey: 'plugin-op-example',
      trustAcknowledged: true
    })
  })

  it('更新与卸载走各自的路径', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(operation))
    const client = new ZbaClient({ baseUrl: '/zba/v1', fetchImpl })

    await client.updatePlugin({ manifest: syntheticManifest, expectedRevision: 'rev-2', idempotencyKey: 'k2', trustAcknowledged: true })
    await client.uninstallPlugin('example.plugin', { expectedRevision: 'rev-3', idempotencyKey: 'k3' })

    expect(calls.map((call) => call.url)).toEqual(['/zba/v1/plugins/update', '/zba/v1/plugins/example.plugin/uninstall'])
    expect(JSON.parse(String(calls[1]?.init.body))).toEqual({ expectedRevision: 'rev-3', idempotencyKey: 'k3' })
  })

  it('按幂等键核对结果，id 需要转义', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(operation))
    const result = await new ZbaClient({ baseUrl: '/zba/v1', fetchImpl }).pluginOperation('plugin op/1')

    expect(result.operation.id).toBe('op-1')
    expect(calls[0]?.url).toBe('/zba/v1/plugins/operations/plugin%20op%2F1')
  })

  it('把 404 plugin_operation_not_found 与 409 idempotency_conflict 留给调用方识别', async () => {
    const notFound = fakeFetch(jsonResponse({ error: { code: 'plugin_operation_not_found', message: '没有该幂等键的记录。' } }, 404))
    await expect(new ZbaClient({ fetchImpl: notFound.fetchImpl }).pluginOperation('k1'))
      .rejects.toMatchObject({ status: 404, code: 'plugin_operation_not_found' })

    const conflict = fakeFetch(jsonResponse({ error: { code: 'idempotency_conflict', message: '同一个幂等键不能用于不同请求体。' } }, 409))
    await expect(new ZbaClient({ fetchImpl: conflict.fetchImpl }).installPlugin({
      manifest: syntheticManifest, expectedRevision: 'rev-1', idempotencyKey: 'k1', trustAcknowledged: true
    })).rejects.toMatchObject({ status: 409, code: 'idempotency_conflict' })
  })

  it('非法操作状态或空 operation.id 判为非法响应', async () => {
    const badStatus = fakeFetch(jsonResponse({ operation: { ...operation.operation, status: 'queued' }, revision: 'rev-2' }))
    await expect(new ZbaClient({ fetchImpl: badStatus.fetchImpl }).pluginOperation('k1'))
      .rejects.toMatchObject({ code: 'invalid_json' })

    const emptyId = fakeFetch(jsonResponse({ operation: { ...operation.operation, id: '' }, revision: 'rev-2' }))
    await expect(new ZbaClient({ fetchImpl: emptyId.fetchImpl }).pluginOperation('k1'))
      .rejects.toMatchObject({ code: 'invalid_json' })
  })

  it('读取声明式设置：schema 与 configuredSecrets 必须齐全', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse(settings))
    const result = await new ZbaClient({ baseUrl: '/zba/v1', fetchImpl }).pluginSettings('example.plugin')

    expect(result.configuredSecrets.secret).toBe(true)
    expect(result.values.caption).toBe('示例')
    expect(calls[0]?.url).toBe('/zba/v1/plugins/example.plugin/settings')
  })

  it('保存设置：Keep 不带 value，Set/Delete 带 value，且不回显旧密钥', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse({ ...settings, revision: 'rev-4' }))
    const result = await new ZbaClient({ baseUrl: '/zba/v1', fetchImpl }).savePluginSettings('example.plugin', {
      expectedRevision: 'rev-3',
      idempotencyKey: 'plugin-settings-example',
      edits: [
        { key: 'caption', kind: 0 },
        { key: 'limit', kind: 1, value: '9' },
        { key: 'secret', kind: 2 }
      ]
    })

    expect(result.revision).toBe('rev-4')
    expect(calls[0]?.init.method).toBe('PUT')
    expect(JSON.parse(String(calls[0]?.init.body))).toEqual({
      expectedRevision: 'rev-3',
      idempotencyKey: 'plugin-settings-example',
      edits: [
        { key: 'caption', kind: 0 },
        { key: 'limit', kind: 1, value: '9' },
        { key: 'secret', kind: 2, value: '' }
      ]
    })
  })

  it('设置响应里的 schema 漂移或 secrets 非布尔值判为非法响应', async () => {
    const badSchema = fakeFetch(jsonResponse({ ...settings, schema: [{ key: 'x', label: 'X', type: 'script' }] }))
    await expect(new ZbaClient({ fetchImpl: badSchema.fetchImpl }).pluginSettings('example.plugin'))
      .rejects.toMatchObject({ code: 'invalid_json' })

    const badSecrets = fakeFetch(jsonResponse({ ...settings, configuredSecrets: { secret: 'yes' } }))
    await expect(new ZbaClient({ fetchImpl: badSecrets.fetchImpl }).pluginSettings('example.plugin'))
      .rejects.toMatchObject({ code: 'invalid_json' })
  })

  it('网络失败归一化为 ZbaNetworkError 而不是插件结果未知', async () => {
    const failing = (async () => { throw new Error('socket closed') }) as unknown as typeof fetch
    const client = new ZbaClient({ baseUrl: '/zba/v1', fetchImpl: failing })

    await expect(client.pluginOperation('k1')).rejects.toBeInstanceOf(ZbaNetworkError)
    await expect(client.pluginOperation('k1')).rejects.toMatchObject({ name: 'ZbaNetworkError' })
  })

  it('401 保留为可解释错误，不泄露令牌', async () => {
    const { fetchImpl, calls } = fakeFetch(jsonResponse({ error: { code: 'unauthorized', message: '缺少 Bearer 凭据。' } }, 401))
    const client = new ZbaClient({ baseUrl: '/zba/v1', token: 'secret-token', fetchImpl })

    await expect(client.installedPlugins()).rejects.toMatchObject({ name: 'ZbaRequestError', status: 401, code: 'unauthorized' })
    expect(calls[0]?.url).not.toContain('secret-token')
  })

  it('ZbaRequestError 实例可被调用方按 name 判定', async () => {
    const { fetchImpl } = fakeFetch(jsonResponse({ error: { code: 'plugin_source_invalid', message: '地址不是固定版本。' } }, 422))
    const error = await new ZbaClient({ fetchImpl }).resolvePluginSource('https://example.com/x').catch((cause: unknown) => cause)
    expect(error).toBeInstanceOf(ZbaRequestError)
    expect((error as ZbaRequestError).code).toBe('plugin_source_invalid')
  })
})
