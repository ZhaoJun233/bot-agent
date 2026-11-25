import { expect, test } from '@playwright/test'
import type { PluginPreviewRequest } from '../../src/api/plugin-import'

const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'

async function connect(page: import('@playwright/test').Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-plugins').click()
  await expect(page.getByTestId('plugins-page')).toBeVisible()
}

test('插件市场：管理员能切到市场与导入工作区并看到信任边界', async ({ page }) => {
  // This UI contract must not depend on the public index responding within eight seconds.
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({ json: {
    revision: 'synthetic-catalog-v1', source: 'https://github.com/example-owner/example-index',
    trustNotice: '插件不是沙箱：请仅安装你信任的代码。', categories: [], plugins: []
  } }))
  await connect(page, ADMIN)
  await page.getByTestId('plugins-enable-admin').click()
  await expect(page.getByTestId('global-admin-toggle')).toHaveAttribute('data-mode', 'admin')

  await page.getByTestId('plugins-tab-catalog').click()
  const workspace = page.getByTestId('marketplace-workspace')
  await expect(workspace).toBeVisible()
  // 只读横幅在管理员模式下不该出现
  await expect(page.getByTestId('plugins-readonly')).toHaveCount(0)
  // 目录要么有条目，要么是显式空态——不能一片空白。
  const list = page.getByTestId('marketplace-list')
  const empty = page.getByTestId('marketplace-empty')
  await expect(list.or(empty)).toBeVisible()

  await page.getByTestId('plugins-tab-import').click()
  await expect(page.getByTestId('marketplace-source-input')).toBeVisible()
  await expect(page.getByTestId('marketplace-source-resolve')).toBeEnabled()
})

test('插件市场：目录失败时显示错误并结束加载，导入工作区仍可用', async ({ page }) => {
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({
    status: 503, json: { error: { code: 'service_unavailable', message: 'Synthetic catalogue unavailable' } }
  }))
  await connect(page, ADMIN)
  await page.getByTestId('plugins-enable-admin').click()
  await page.getByTestId('plugins-tab-catalog').click()
  const workspace = page.getByTestId('marketplace-workspace')
  await expect(workspace.getByRole('alert')).toBeVisible()
  await expect(workspace.getByText('正在读取市场目录…', { exact: true })).toHaveCount(0)
  await page.getByTestId('plugins-tab-import').click()
  await expect(workspace.getByTestId('marketplace-source-input')).toBeVisible()
})

test('插件市场：只读令牌打开时给出启用管理员的入口', async ({ page }) => {
  await connect(page, READ)
  await page.getByTestId('plugins-tab-catalog').click()
  await expect(page.getByTestId('marketplace-workspace')).toBeVisible()
  await expect(page.getByTestId('plugins-readonly')).toBeVisible()
})

test('插件市场：详情抽屉的「在插件市场中查看」带着该插件切到市场', async ({ page }) => {
  await connect(page, ADMIN)
  const inspectBtn = page.locator('[data-testid^="cap-inspect-plugin:preset.feature."]').first()
  await expect(inspectBtn).toBeVisible()
  await inspectBtn.click()
  await expect(page.getByTestId('plugin-detail-drawer')).toBeVisible()
  await page.getByTestId('plugin-detail-open-market').click()
  await expect(page.getByTestId('marketplace-workspace')).toBeVisible()
  await expect(page.getByTestId('plugin-detail-drawer')).toHaveCount(0)
})

test('插件市场：指定数据源拒绝非 GitHub 地址且不发请求', async ({ page }) => {
  const calls: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('/plugins/sources/resolve') || request.url().includes('/plugins/source/discover')) calls.push(request.url())
  })
  await connect(page, ADMIN)
  await page.getByTestId('plugins-enable-admin').click()
  await page.getByTestId('plugins-tab-import').click()
  await expect(page.getByTestId('marketplace-workspace')).toBeVisible()
  await page.getByTestId('marketplace-source-input').fill('https://example.com/example-plugin')
  await page.getByTestId('marketplace-source-resolve').click()
  await expect(page.getByTestId('marketplace-source-error')).toBeVisible()
  expect(calls).toHaveLength(0)
})

for (const outcome of ['install', 'recovery'] as const) {
  test('插件市场：' + outcome + ' 确认后目录刷新未完成也能切回已安装列表，不重复提交', async ({ page }) => {
    let releaseRefresh!: () => void
    const heldRefresh = new Promise<void>(resolve => { releaseRefresh = resolve })
    let catalogReads = 0
    const installKeys: string[] = []
    const recoveryKeys: string[] = []
    const syntheticManifest = { id: 'example.plugin', version: '1.0.0', entryAssembly: 'Example.Plugin.dll', entryType: 'Example.Plugin', sha256: 'a'.repeat(64), downloadUrl: 'https://example.com/fixture.zip', apiVersion: 1, targetFramework: 'net10.0', permissions: [], settings: [] }
    const receipt = { revision: 'synthetic-p2', operation: { id: 'synthetic-operation', pluginId: syntheticManifest.id, kind: 'install', status: 'restart_required', restartRequired: true } }
    await page.route('**/zba/v1/plugins/catalog', async route => {
      if (++catalogReads > 1) await heldRefresh
      await route.fulfill({ json: { revision: 'synthetic-p1', source: 'synthetic', trustNotice: 'Synthetic UI fixture only', categories: [], plugins: [{ manifest: syntheticManifest, enabled: false, status: 'available', restartRequired: false }] } })
    })
    await page.route('**/zba/v1/plugins/installed', route => route.fulfill({ json: { revision: 'synthetic-p1', plugins: [] } }))
    await page.route('**/zba/v1/plugins/preview', route => {
      const request: PluginPreviewRequest = route.request().postDataJSON()
      return route.fulfill({ json: { previewId: 'synthetic-preview', expiresAt: new Date(Date.now() + 60_000).toISOString(), revision: request.expectedRevision, manifest: syntheticManifest, source: { kind: request.origin }, enabledAfterInstall: false, canInstall: true, checks: [], trustNotice: 'Synthetic UI fixture only' } })
    })
    await page.route('**/zba/v1/plugins/install', async route => {
      installKeys.push(route.request().postDataJSON().idempotencyKey)
      if (outcome === 'recovery') await route.abort('failed')
      else await route.fulfill({ json: receipt })
    })
    await page.route('**/zba/v1/plugins/operations/*', route => {
      recoveryKeys.push(decodeURIComponent(new URL(route.request().url()).pathname.split('/').pop()!))
      return route.fulfill({ json: receipt })
    })
    try {
      await connect(page, ADMIN)
      await page.getByTestId('plugins-enable-admin').click()
      await page.getByTestId('plugins-tab-catalog').click()
      await page.getByTestId('marketplace-install').click()
      await page.getByTestId('preview-acknowledge').check()
      await page.getByTestId('preview-confirm').click()
      if (outcome === 'recovery') {
        await expect(page.getByTestId('marketplace-recovery-check')).toBeEnabled()
        await page.getByTestId('marketplace-recovery-check').click()
        await expect(page.getByTestId('marketplace-recovery')).toHaveCount(0)
      }
      await expect.poll(() => catalogReads).toBe(2)
      // The read is still held: returning to installed tab must not depend on a catalog response.
      await page.getByTestId('plugins-tab-installed').click()
      await expect(page.getByTestId('marketplace-workspace')).toHaveCount(0)
      expect(installKeys).toHaveLength(1)
      expect(recoveryKeys).toEqual(outcome === 'recovery' ? installKeys : [])
    } finally {
      releaseRefresh()
      await page.unrouteAll({ behavior: 'wait' })
    }
  })
}
