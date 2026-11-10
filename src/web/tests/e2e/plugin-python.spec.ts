import { expect, test, type Page } from '@playwright/test'
import { pythonManifest } from '../fixtures/python-plugin'
async function setup(page: Page, ready: boolean) {
  const writes: unknown[] = []
  const item = { manifest: pythonManifest, enabled: false, status: 'available', restartRequired: false, isCompatible: ready, compatibilityMessage: ready ? null : 'Python 解释器未配置；环境尚未准备完成' }
  await page.route('**/zba/v1/plugins/catalog*', route => route.fulfill({ json: { revision: 'python-r1', source: 'bundled', trustNotice: '进程不是沙箱', categories: [], plugins: [item] } }))
  await page.route('**/zba/v1/plugins/installed', route => route.fulfill({ json: { revision: 'python-r1', plugins: [] } }))
  await page.route('**/zba/v1/plugins/preview', async route => {
    const request = route.request().postDataJSON()
    expect(request.manifest).toEqual(pythonManifest)
    expect(request.expectedRevision).toBe('python-r1')
    await route.fulfill({ json: { previewId: 'python-preview', expiresAt: new Date(Date.now() + 60_000).toISOString(), revision: 'python-r1', manifest: pythonManifest, source: { kind: 'catalog' }, enabledAfterInstall: false, canInstall: ready, checks: [{ code: 'python-runtime', status: ready ? 'passed' : 'blocked', message: ready ? 'Python 环境已准备' : '解释器不可用' }], trustNotice: 'Python 子进程不是沙箱；安装后默认禁用。' } })
  })
  await page.route('**/zba/v1/plugins/install', async route => {
    writes.push(route.request().postDataJSON())
    await route.fulfill({ json: { revision: 'python-r2', operation: { id: 'python-install', pluginId: pythonManifest.id, kind: 'install', status: 'installed', restartRequired: false } } })
  })
  await page.goto('/web/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId('cap-enable-admin').click()
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
  await page.getByTestId('catalog-runtime').selectOption('python')
  await page.locator('.catalog-list .catalog-entry').click()
  return writes
}
for (const width of [1040, 390]) {
  for (const ready of [true, false]) {
    test('Python readiness '+ready+' and explicit confirmation at '+width+'px', async ({ page }) => {
      await page.setViewportSize({ width, height: 860 })
      const writes = await setup(page, ready)
      await expect(page.getByTestId('marketplace-trust-warning')).toContainText('子进程不是安全沙箱')
      await expect(page.getByTestId('marketplace-install')).toBeEnabled({ enabled: ready })
      if (!ready) {
        await expect(page.getByTestId('marketplace')).toContainText('Python 解释器未配置；环境尚未准备完成')
      } else {
        await page.getByTestId('marketplace-install').click()
        await expect(page.getByTestId('plugin-install-preview')).toContainText('Python >=3.11、<4')
        await expect(page.getByTestId('preview-confirm')).toBeDisabled()
        expect(writes).toHaveLength(0)
        await page.getByTestId('preview-acknowledge').check()
        await page.getByTestId('preview-confirm').click()
        await expect.poll(() => writes.length).toBe(1)
        expect(writes[0]).toEqual(expect.objectContaining({ manifest: pythonManifest, previewId: 'python-preview', trustAcknowledged: true }))
      }
      expect(writes).toHaveLength(ready ? 1 : 0)
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth)).toBe(true)
    })
  }
}
