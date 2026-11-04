import { expect, test, type Page } from '@playwright/test'
const READ = 'synthetic-web-read-token'
async function connect(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(READ)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-logs').click()
  await expect(page.getByTestId('runtime-logs-page')).toBeVisible()
}

test('运行日志：真实宿主只读鉴权、安全摘要与参数边界', async ({ page }) => {
  const responsePromise = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/logs')
  await connect(page)
  const response = await responsePromise
  expect(response.status()).toBe(200)
  expect(response.headers()['cache-control']).toContain('no-store')
  const result = await response.json()
  expect(result.capacity).toBe(600)
  expect(Array.isArray(result.items)).toBe(true)
  expect(JSON.stringify(result)).not.toContain(READ)
  await expect(page.getByTestId('runtime-logs-page')).toContainText('聊天正文、成员信息和凭据不会返回浏览器')
  const invalid = await page.request.get('/zba/v1/logs?limit=601', { headers: { Authorization: 'Bearer ' + READ } })
  expect(invalid.status()).toBe(400)
  const denied = await page.request.get('/zba/v1/logs')
  expect(denied.status()).toBe(401)
})

test('运行日志：合成摘要可筛选，移动端不溢出，锁定清除视图', async ({ page }, testInfo) => {
  await page.route('**/zba/v1/logs?*', route => route.fulfill({ json: { capacity: 600, items: [
    { time: 1793773800000, level: 'warn', source: 'Agent', message: '模型输出为空（empty_reply）', redacted: true },
    { time: 1793773790000, level: 'info', source: 'Web', message: '服务已启动', redacted: true },
    { time: 1793773780000, level: 'error', source: 'Other', message: '内容已隐藏（长度 88）', redacted: true }
  ] } }))
  await connect(page)
  const panel = page.getByTestId('runtime-logs-page')
  await expect(panel.locator('.log-row')).toHaveCount(3)
  await page.screenshot({ path: testInfo.outputPath('runtime-logs-desktop.png'), fullPage: true })
  await panel.getByRole('combobox', { name: '级别', exact: true }).selectOption('warn')
  await expect(panel.locator('.log-row')).toHaveCount(1)
  await panel.getByLabel('关键词', { exact: true }).fill('missing')
  await expect(panel).toContainText('没有匹配的日志')
  await panel.getByRole('button', { name: '清除筛选' }).click()
  await page.setViewportSize({ width: 390, height: 844 })
  await expect(panel.locator('.log-row')).toHaveCount(3)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('runtime-logs-mobile.png'), fullPage: true })
  await page.getByTestId('lock').click()
  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(panel).toHaveCount(0)
})
