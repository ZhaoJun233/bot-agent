import { expect, test } from '@playwright/test'
import { quota } from '../fixtures/quotas'

const ADMIN = 'synthetic-web-admin-token'

async function openMarketplace(page: import('@playwright/test').Page): Promise<void> {
  // Keep catalog deterministic & offline; /zba/v1/plugins/installed comes from the REAL Headless host.
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({
    json: {
      revision: 'synthetic-catalog-ui-v1',
      source: 'https://github.com/example-owner/example-index',
      trustNotice: '插件不是沙箱：请仅安装你信任的代码。',
      categories: [],
      plugins: [],
    },
  }))
  await page.goto('/web/')
  await page.getByTestId('token-input').fill(ADMIN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
}

test('runtime plugin UI: real token-quota iframe loads classic script, enforces CSP, renders wire fields, saves with string revision, and closes via bridge', async ({ page }) => {
  const consoleErrors: string[] = []
  page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()) })
  page.on('pageerror', err => consoleErrors.push(err.message))

  const savedBodies: Array<Record<string, unknown>> = []
  let currentLimit = 50000
  let currentRevision = '3'
  await page.route('**/zba/v1/quotas', route => route.fulfill({
    json: {
      items: [quota({
        sourceKey: 'group:10001',
        displayName: '测试群A',
        dailyTokenLimit: currentLimit,
        usedTokens: 1234,
        revision: currentRevision,
      })],
    },
  }))
  await page.route('**/zba/v1/quotas/save', async route => {
    const body = route.request().postDataJSON() as Record<string, unknown>
    savedBodies.push(body)
    currentLimit = Number(body.dailyTokenLimit)
    currentRevision = '4'
    await route.fulfill({
      json: {
        quota: quota({
          sourceKey: 'group:10001',
          displayName: '测试群A',
          dailyTokenLimit: currentLimit,
          usedTokens: 1234,
          revision: currentRevision,
        }),
      },
    })
  })

  await openMarketplace(page)
  const item = page.getByTestId('marketplace-item-preset.feature.token-quota')
  await expect(item).toBeVisible()
  await item.click()

  const htmlResponsePromise = page.waitForResponse(r =>
    r.url().includes('/zba/v1/plugins/preset.feature.token-quota/ui/') && r.url().endsWith('/web/index.html'))
  const jsResponsePromise = page.waitForResponse(r =>
    r.url().includes('/zba/v1/plugins/preset.feature.token-quota/ui/') && r.url().endsWith('/web/token-quota-runtime.js'))

  await page.getByTestId('marketplace-open-ui').click()
  await expect(page.getByTestId('plugin-ui-dialog')).toBeVisible()

  const htmlRes = await htmlResponsePromise
  const jsRes = await jsResponsePromise
  expect(htmlRes.status()).toBe(200)
  expect(jsRes.status()).toBe(200)
  expect(htmlRes.headers()['content-security-policy']).toContain("connect-src 'none'")
  expect(htmlRes.headers()['content-security-policy']).not.toContain('unsafe-inline')
  expect(htmlRes.headers()['cache-control']).toContain('private')
  expect(htmlRes.headers()['cache-control']).toContain('immutable')

  const frame = page.frameLocator('[data-testid="plugin-ui-dialog"] iframe.plugin-ui-iframe')
  await expect(frame.locator('#quota-meta')).toContainText('v1.0.0')
  await expect(frame.locator('#quota-list')).toContainText('测试群A')
  await expect(frame.locator('#quota-list')).toContainText('已用 1234 / 限额 50000 · 修订 3')

  const input = frame.locator('#quota-list input[type="number"]')
  await input.fill('60000')
  await frame.locator('#quota-list button.primary').click()

  await expect(frame.locator('#quota-list')).toContainText('已用 1234 / 限额 60000 · 修订 4')
  expect(savedBodies).toHaveLength(1)
  expect(savedBodies[0]).toMatchObject({
    sourceKey: 'group:10001',
    dailyTokenLimit: 60000,
    expectedRevision: '3',
  })

  // Closing via the iframe's own close button verifies zba.ui.request -> dialog.close
  await frame.locator('#close-btn').click()
  await expect(page.getByTestId('plugin-ui-dialog')).toHaveCount(0)
  expect(consoleErrors).toEqual([])
})

test('runtime plugin UI: real stickers iframe completes zba.ui.init handshake and closes via bridge', async ({ page }) => {
  const consoleErrors: string[] = []
  page.on('console', msg => { if (msg.type() === 'error') consoleErrors.push(msg.text()) })
  page.on('pageerror', err => consoleErrors.push(err.message))

  await openMarketplace(page)
  const item = page.getByTestId('marketplace-item-preset.feature.stickers')
  await expect(item).toBeVisible()
  await item.click()
  await page.getByTestId('marketplace-open-ui').click()

  await expect(page.getByTestId('plugin-ui-dialog')).toBeVisible()
  const frame = page.frameLocator('[data-testid="plugin-ui-dialog"] iframe.plugin-ui-iframe')
  await expect(frame.locator('#generation-badge')).toContainText('v1.0.0 · 代次')
  await expect(frame.locator('#status-line')).toContainText('活跃代际会话已建立')

  await frame.locator('#close-btn').click()
  await expect(page.getByTestId('plugin-ui-dialog')).toHaveCount(0)
  expect(consoleErrors).toEqual([])
})
