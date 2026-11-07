import { expect, test, type Page } from '@playwright/test'
import type { PluginPreviewRequest } from '../../src/api/plugin-import'
import type { ZbaPluginCatalog } from '../../src/api/zba'
import type { PluginPackageManifest } from '../../src/features/capabilities/plugin-manifest'

const HEADERS = { Authorization: 'Bearer synthetic-web-admin-token' }
const SOURCE = 'https://github.com/example-org/example-plugins'
const ALPHA = 'example.alpha'
function manifest(id = ALPHA, version = '1.0.0'): PluginPackageManifest {
  return { id, version, entryAssembly: 'Example.dll', entryType: 'Example.Plugin',
    sha256: (version === '2.0.0' ? 'b' : 'a').repeat(64),
    downloadUrl: SOURCE + '/releases/download/' + id + '-' + version + '/' + id + '-' + version + '.zip',
    apiVersion: 1, targetFramework: 'net8.0', permissions: [], dependencies: [], settings: [] }
}
async function openMarket(page: Page) {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId('cap-enable-admin').click()
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
}
async function fakeImportServer(page: Page, blocked = false) {
  const previews: PluginPreviewRequest[] = []
  const writes: { previewId: string; manifest: PluginPackageManifest; trustAcknowledged: boolean; expectedRevision: string }[] = []
  const catalog: ZbaPluginCatalog = {
    revision: 'rev-import', source: SOURCE, trustNotice: '未审核的进程内插件不是沙箱。',
    categories: [], builtinPlugins: [], plugins: [{ manifest: manifest(), category: '社区', installedVersion: null, enabled: false, status: '', restartRequired: false }]
  }
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({ json: catalog }))
  await page.route('**/zba/v1/plugins/installed', route => route.fulfill({ json: {
    revision: 'rev-import', plugins: [], builtinPlugins: []
  } }))
  await page.route('**/zba/v1/plugins/source/discover', route => route.fulfill({ json: {
    discoveryId: 'discovery-synthetic', sourceUrl: SOURCE, sourceRevision: 'c'.repeat(40),
    expiresAt: new Date(Date.now() + 600_000).toISOString(), candidates: [
      { pluginId: ALPHA, version: '1.0.0' }, { pluginId: ALPHA, version: '2.0.0' },
      { pluginId: 'example.beta', version: '1.0.0' }
    ]
  } }))
  await page.route('**/zba/v1/plugins/preview', route => {
    const body: PluginPreviewRequest = route.request().postDataJSON()
    previews.push(body)
    const selected = body.origin === 'repository' ? manifest(body.pluginId, body.version) : body.manifest
    return route.fulfill({ json: {
      previewId: 'preview-synthetic-' + previews.length, expiresAt: new Date(Date.now() + 600_000).toISOString(),
      revision: 'rev-import', manifest: selected, source: { kind: body.origin, url: SOURCE, revision: 'c'.repeat(40) },
      enabledAfterInstall: false, canInstall: !blocked, trustNotice: '默认禁用不代表零代码执行；仍须信任来源。',
      checks: [{ code: 'compatibility', status: blocked ? 'blocked' : 'passed', message: blocked ? '缺少依赖，拒绝安装' : '兼容性已检查' },
        { code: 'artifact', status: 'pending', message: 'ZIP 与 SHA-256 将在确认后下载验证' }]
    } })
  })
  // Never download or execute a package: rejected confirmations still prove the exact browser payload.
  await page.route('**/zba/v1/plugins/install', route => {
    writes.push(route.request().postDataJSON())
    return route.fulfill({ status: 409, json: { error: { code: 'plugin_preview_refresh_required', message: '来源已变化，请重新预览' } } })
  })
  return { previews, writes }
}
async function discover(page: Page) {
  await page.getByTestId('marketplace-source-input').fill(SOURCE)
  await page.getByTestId('marketplace-source-resolve').click()
  await expect(page.getByTestId('source-candidates')).toBeVisible()
}

test('安全导入：多插件多版本选择、显式确认与固定预览', async ({ page }, testInfo) => {
  const calls = await fakeImportServer(page)
  await openMarket(page)
  await discover(page)
  await expect(page.getByTestId('source-plugin').locator('option')).toHaveCount(2)
  await page.getByTestId('source-plugin').selectOption('example.beta')
  await expect(page.getByTestId('source-version').locator('option')).toHaveCount(1)
  await page.getByTestId('source-plugin').selectOption(ALPHA)
  await page.getByTestId('source-version').selectOption('2.0.0')
  await page.getByTestId('source-preview').click()
  await expect(page.getByTestId('preview-sha256')).toHaveText('b'.repeat(64))
  await expect(page.getByTestId('preview-confirm')).toBeDisabled()
  expect(calls.writes).toHaveLength(0)
  expect(calls.previews[0]).toEqual({ origin: 'repository', discoveryId: 'discovery-synthetic', pluginId: ALPHA, version: '2.0.0', expectedRevision: 'rev-import' })
  await page.getByTestId('preview-acknowledge').check()
  await expect(page.getByTestId('preview-confirm')).toBeEnabled()
  await page.screenshot({ path: testInfo.outputPath('import-preview-desktop.png'), fullPage: false })
  await page.getByTestId('preview-confirm').click()
  await expect.poll(() => calls.writes.length).toBe(1)
  expect(calls.writes[0]).toMatchObject({ previewId: 'preview-synthetic-1', manifest: manifest(ALPHA, '2.0.0'), trustAcknowledged: true, expectedRevision: 'rev-import' })
  await expect(page.getByTestId('plugin-install-preview')).toHaveCount(0)
  await page.getByTestId('source-preview').click()
  await expect(page.getByTestId('preview-acknowledge')).not.toBeChecked()
  await expect(page.getByTestId('preview-confirm')).toBeDisabled()
  expect(calls.previews).toHaveLength(2)
})

test('安全导入：更换版本或来源立即作废已确认预览', async ({ page }) => {
  const calls = await fakeImportServer(page)
  await openMarket(page)
  await discover(page)
  await page.getByTestId('source-preview').click()
  await page.getByTestId('preview-acknowledge').check()
  await page.getByTestId('source-version').selectOption('2.0.0')
  await expect(page.getByTestId('plugin-install-preview')).toHaveCount(0)
  await page.getByTestId('source-preview').click()
  await expect(page.getByTestId('preview-acknowledge')).not.toBeChecked()
  await page.getByTestId('preview-acknowledge').check()
  await page.getByTestId('marketplace-source-input').fill(SOURCE + '-other')
  await expect(page.getByTestId('plugin-install-preview')).toHaveCount(0)
  await expect(page.getByTestId('source-candidates')).toHaveCount(0)
  expect(calls.writes).toHaveLength(0)
})

test('安全导入：高级 JSON 遵守同一阻断规则且移动端不溢出', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const calls = await fakeImportServer(page, true)
  await openMarket(page)
  await page.getByText('高级：粘贴技术清单 JSON', { exact: true }).click()
  await page.getByTestId('marketplace-json-input').fill(JSON.stringify(manifest()))
  await page.getByTestId('json-preview').click()
  await expect(page.getByTestId('plugin-install-preview')).toContainText('缺少依赖')
  await expect(page.getByTestId('preview-acknowledge')).toBeDisabled()
  await expect(page.getByTestId('preview-confirm')).toBeDisabled()
  expect(calls.previews[0]?.origin).toBe('json')
  expect(calls.writes).toHaveLength(0)
  await page.getByTestId('plugin-install-preview').scrollIntoViewIfNeeded()
  expect(await page.getByTestId('marketplace').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('import-preview-mobile.png'), fullPage: false })
  await page.getByTestId('marketplace-json-input').fill('{}')
  await expect(page.getByTestId('plugin-install-preview')).toHaveCount(0)
  await page.getByTestId('json-preview').click()
  await expect(page.locator('details.json-import').getByRole('alert')).toBeVisible()
  expect(calls.previews).toHaveLength(1)
})

test('安全导入：市场条目只预览，未确认不发安装请求', async ({ page }) => {
  const calls = await fakeImportServer(page)
  await openMarket(page)
  await page.getByTestId('marketplace-install').click()
  await expect(page.getByTestId('plugin-install-preview')).toBeVisible()
  expect(calls.previews[0]).toMatchObject({ origin: 'catalog', manifest: manifest() })
  await expect(page.getByTestId('preview-confirm')).toBeDisabled()
  expect(calls.writes).toHaveLength(0)
})

test('安全导入：真实合成宿主预览不安装，拒绝缺失或被篡改的确认', async ({ request }) => {
  const before = await request.get('/zba/v1/plugins/installed', { headers: HEADERS })
  expect(before.status()).toBe(200)
  const installed = await before.json()
  const input = manifest('example.wire')
  const previewResponse = await request.post('/zba/v1/plugins/preview', { headers: HEADERS,
    data: { origin: 'json', expectedRevision: installed.revision, manifest: input } })
  expect(previewResponse.status()).toBe(200)
  const preview = await previewResponse.json()
  expect(preview).toMatchObject({ revision: installed.revision, manifest: input, enabledAfterInstall: false, canInstall: true })
  expect(preview.checks.some((check: { status: string }) => check.status === 'pending')).toBe(true)
  const base = { manifest: input, expectedRevision: installed.revision, trustAcknowledged: true }
  const missing = await request.post('/zba/v1/plugins/install', { headers: HEADERS, data: { ...base, idempotencyKey: 'synthetic-import-missing-preview' } })
  expect(missing.status()).toBe(409)
  expect((await missing.json()).error.code).toBe('plugin_preview_required')
  const tampered = await request.post('/zba/v1/plugins/install', { headers: HEADERS, data: { ...base,
    previewId: preview.previewId, idempotencyKey: 'synthetic-import-tampered-preview', manifest: { ...input, sha256: 'c'.repeat(64) } } })
  expect(tampered.status()).toBe(409)
  const after = await request.get('/zba/v1/plugins/installed', { headers: HEADERS })
  const afterInstalled = await after.json()
  // A read/check may advance advisory freshness; it must not alter installed state.
  const { checkedAt: beforeCheckedAt, ...beforeSource } = installed.governanceSource
  const { checkedAt: afterCheckedAt, ...afterSource } = afterInstalled.governanceSource
  expect({ ...afterInstalled, governanceSource: afterSource })
    .toEqual({ ...installed, governanceSource: beforeSource })
  for (const checkedAt of [beforeCheckedAt, afterCheckedAt]) {
    expect(checkedAt === null || (typeof checkedAt === 'string' && Number.isFinite(Date.parse(checkedAt)))).toBe(true)
  }
  expect(afterCheckedAt).not.toBeNull()
  if (beforeCheckedAt !== null) {
    expect(Date.parse(afterCheckedAt)).toBeGreaterThanOrEqual(Date.parse(beforeCheckedAt))
  }
})