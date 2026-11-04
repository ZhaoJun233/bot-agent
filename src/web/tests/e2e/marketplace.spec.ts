import { expect, test } from '@playwright/test'

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
    if (request.url().includes('/plugins/sources/resolve')) calls.push(request.url())
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
