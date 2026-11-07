import { expect, test, type Page } from '@playwright/test'
import type { PluginGovernance, PluginGovernanceSource } from '../../src/api/plugin-governance'
const syntheticManifest = { id: 'example.plugin', version: '1.0.0', entryAssembly: 'Example.Plugin.dll', entryType: 'Example.Plugin', sha256: 'a'.repeat(64), downloadUrl: 'https://example.com/fixture.zip', apiVersion: 1, targetFramework: 'net10.0', permissions: ['network'], settings: [], dependencies: [] }

const revoked: PluginGovernance = { status: 'revoked', reason: '合成撤销公告：此固定制品不再允许获取。', updatedAt: '2026-11-06T00:00:00Z', blocked: true, match: 'sha256' }
const active: PluginGovernance = { status: 'active', reason: null, updatedAt: null, blocked: false, match: 'none' }
const source: PluginGovernanceSource = { status: 'cached', source: 'https://example.com/governance.json', generatedAt: '2026-11-01T00:00:00Z', checkedAt: '2026-11-06T00:00:00Z', errorCode: 'governance_unavailable' }
async function mockGovernance(page: Page, offline = false): Promise<string[]> {
  const writes: string[] = []
  const item = { manifest: { ...syntheticManifest, version: '2.0.0' }, name: '合成治理插件', summary: '仅用于合成治理验收', category: '工具', installedVersion: '1.0.0', enabled: true, status: 'installed', restartRequired: false, governance: revoked, installedGovernance: revoked }
  await page.route('**/zba/v1/plugins/**', async route => {
    const request = route.request()
    const pathname = new URL(request.url()).pathname
    if (request.method() !== 'GET') { writes.push(pathname); await route.fulfill({ status: 409, json: { error: 'synthetic_write_not_allowed' } }); return }
    if (pathname.endsWith('/catalog')) { await route.fulfill(offline ? { status: 503, json: { error: 'synthetic_offline' } } : { json: { revision: 'g1', source: 'remote', trustNotice: '治理状态不等于安全审计。', categories: ['工具'], plugins: [item], governanceSource: source } }); return }
    if (pathname.endsWith('/installed')) { await route.fulfill({ json: { revision: 'g1', plugins: [{ ...item, manifest: syntheticManifest, governance: active }], governanceSource: source } }); return }
    await route.fallback()
  })
  return writes
}
async function openMarket(page: Page): Promise<void> {
  await page.goto('/web/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId('cap-enable-admin').click()
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
  await page.getByTestId('marketplace-search').fill('合成治理插件')
  await expect(page.locator('.catalog-list .catalog-entry')).toHaveCount(1)
  await page.locator('.catalog-list .catalog-entry').click()
}
for (const width of [1040, 390]) {
  test('cached revocation stays explicit and blocks acquisition at ' + width, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 860 })
    const writes = await mockGovernance(page)
    await openMarket(page)
    const dialog = page.getByTestId('marketplace')
    await expect(dialog).toContainText('不能确认未出现新撤销')
    await expect(dialog).toContainText('2026-11-01T00:00:00Z')
    await expect(dialog).toContainText('不会静默停用或删除')
    await expect(page.getByTestId('marketplace-update')).toBeDisabled()
    await expect(page.getByTestId('marketplace-uninstall')).toBeEnabled()
    expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true)
    expect(writes).toEqual([])
    await page.screenshot({ path: testInfo.outputPath('governance-' + width + '.png'), fullPage: true })
  })
}
test('offline catalog keeps installed revocation and source age visible', async ({ page }) => {
  const writes = await mockGovernance(page, true)
  await openMarket(page)
  const dialog = page.getByTestId('marketplace')
  await expect(dialog).toContainText('当前已安装包')
  await expect(dialog).toContainText('合成撤销公告')
  await expect(dialog).toContainText('不能确认未出现新撤销')
  await expect(page.getByTestId('marketplace-uninstall')).toBeEnabled()
  expect(writes).toEqual([])
})
