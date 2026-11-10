import { expect, test } from '@playwright/test'
import type { PluginPreviewRequest } from '../../src/api/plugin-import'

const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'

async function connect(page: import('@playwright/test').Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()
}

test('插件市场：管理员能打开对话框并看到信任边界', async ({ page }) => {
  // This UI contract must not depend on the public index responding within eight seconds.
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({ json: {
    revision: 'synthetic-catalog-v1', source: 'https://github.com/example-owner/example-index',
    trustNotice: '插件不是沙箱：请仅安装你信任的代码。', categories: [], plugins: []
  } }))
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  await page.getByTestId('cap-marketplace').click()
  const dialog = page.getByTestId('marketplace')
  await expect(dialog).toBeVisible()
  // 只读横幅在管理员模式下不该出现；写入能力仍由开关决定。
  await expect(page.getByTestId('marketplace-readonly')).toHaveCount(0)
  await expect(page.getByTestId('marketplace-source-input')).toBeVisible()
  await expect(page.getByTestId('marketplace-source-resolve')).toBeEnabled()
  // 目录要么有条目，要么是显式空态——不能一片空白。
  const list = page.getByTestId('marketplace-list')
  const empty = page.getByTestId('marketplace-empty')
  await expect(list.or(empty)).toBeVisible()
  await page.getByTestId('marketplace-close').click()
  await expect(dialog).toHaveCount(0)
})

test('插件市场：目录失败时显示错误并结束加载，保留管理对话框', async ({ page }) => {
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({
    status: 503, json: { error: { code: 'service_unavailable', message: 'Synthetic catalogue unavailable' } }
  }))
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await page.getByTestId('cap-marketplace').click()
  const dialog = page.getByTestId('marketplace')
  await expect(dialog.getByRole('alert')).toBeVisible()
  await expect(dialog.getByText('正在读取市场目录…', { exact: true })).toHaveCount(0)
  await expect(dialog.getByTestId('marketplace-source-input')).toBeVisible()
  await expect(dialog.getByTestId('marketplace-close')).toBeEnabled()
})

test('插件市场：只读令牌打开时给出启用管理员的入口', async ({ page }) => {
  await connect(page, READ)
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
  await expect(page.getByTestId('marketplace-readonly')).toBeVisible()
  await page.getByTestId('marketplace-close').click()
})

test('插件市场：卡片上的「市场」按钮带着该插件打开', async ({ page }) => {
  await connect(page, ADMIN)
  const entry = page.locator('[data-testid^="cap-marketplace-"]').first()
  await expect(entry).toBeVisible()
  await entry.click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
  await page.getByTestId('marketplace-close').click()
  await expect(page.getByTestId('marketplace')).toHaveCount(0)
})

test('插件市场：指定数据源拒绝非 GitHub 地址且不发请求', async ({ page }) => {
  const calls: string[] = []
  page.on('request', (request) => {
    if (request.url().includes('/plugins/sources/resolve') || request.url().includes('/plugins/source/discover')) calls.push(request.url())
  })
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
  await page.getByTestId('marketplace-source-input').fill('https://example.com/example-plugin')
  await page.getByTestId('marketplace-source-resolve').click()
  await expect(page.getByTestId('marketplace-source-error')).toBeVisible()
  expect(calls).toHaveLength(0)
})

for (const outcome of ['install', 'recovery'] as const) {
  test('插件市场：' + outcome + ' 确认后目录刷新未完成也能确认放弃并关闭', async ({ page }) => {
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
      await page.getByTestId('cap-enable-admin').click()
      await page.getByTestId('cap-marketplace').click()
      await page.getByTestId('marketplace-source-input').fill('https://github.com/example-org/draft')
      await page.getByTestId('marketplace-install').click()
      await page.getByTestId('preview-acknowledge').check()
      await page.getByTestId('preview-confirm').click()
      if (outcome === 'recovery') {
        await expect(page.getByTestId('marketplace-recovery-check')).toBeEnabled()
        await page.getByTestId('marketplace-recovery-check').click()
        await expect(page.getByTestId('marketplace-recovery')).toHaveCount(0)
      }
      await expect.poll(() => catalogReads).toBe(2)
      // The read is still held: closing must not depend on a catalog response or a timeout.
      await page.getByTestId('marketplace-close').click()
      await expect(page.getByTestId('marketplace-discard-confirm')).toBeVisible()
      await page.getByTestId('marketplace-discard-confirm').click()
      await expect(page.getByTestId('marketplace')).toHaveCount(0)
      await expect(page.getByTestId('cap-marketplace')).toBeFocused()
      expect(installKeys).toHaveLength(1)
      expect(recoveryKeys).toEqual(outcome === 'recovery' ? installKeys : [])
    } finally {
      releaseRefresh()
      await page.unrouteAll({ behavior: 'wait' })
    }
  })
}
