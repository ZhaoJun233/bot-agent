import { expect, test, type Page } from '@playwright/test'

async function connect(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()
  await page.getByTestId('task-enable-admin').click()
  await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')
}

test('accepted response loss and full refresh recover by original key without another submission', async ({ page }, testInfo) => {
  let submissions = 0
  let acceptedId = ''
  let originalKey = ''
  await page.route('**/zba/v1/tasks', async route => {
    if (route.request().method() !== 'POST') return route.continue()
    submissions += 1
    originalKey = route.request().headers()['idempotency-key'] ?? ''
    const accepted = await route.fetch()
    expect(accepted.ok()).toBe(true)
    acceptedId = (await accepted.json()).id
    await route.abort('connectionreset')
  })
  await connect(page)
  await page.getByTestId('task-source-key').fill('panel:synthetic-recovery')
  await page.getByTestId('task-session-id').fill('synthetic-recovery-session')
  await page.getByTestId('task-prompt').fill('synthetic recovery check')
  await page.getByTestId('task-submit').click()
  await expect(page.getByTestId('task-submission-state')).toContainText('结果未知')
  expect(originalKey).toMatch(/^task1-/)
  await expect(page.getByTestId('task-idempotency-key')).toHaveText(originalKey)

  await page.reload()
  await connect(page)
  await expect(page.getByTestId('task-submission')).toHaveCount(0)
  await page.getByTestId('task-recovery').locator('summary').click()
  await page.getByTestId('task-recovery-key').fill(originalKey)
  const request = page.waitForRequest(r => r.url().endsWith('/zba/v1/tasks/find'))
  await page.getByTestId('task-recovery-key').press('Enter')
  expect((await request).postDataJSON()).toEqual({ key: originalKey })
  await expect(page.getByTestId('task-recovery-code')).toHaveText('已找到受理记录')
  await page.getByTestId('task-recovery-open').click()
  await expect(page.locator('.task-result-id')).toContainText(acceptedId)
  expect(submissions).toBe(1)
  await page.getByTestId('task-recovery').screenshot({ path: testInfo.outputPath('recovered.png') })
})

test('narrow keyboard lookup keeps an unknown result separate from replay', async ({ page }, testInfo) => {
  let submissions = 0
  page.on('request', r => { if (r.method() === 'POST' && r.url().endsWith('/zba/v1/tasks')) submissions += 1 })
  await page.setViewportSize({ width: 375, height: 812 })
  await connect(page)
  const disclosure = page.getByTestId('task-recovery').locator('summary')
  await disclosure.focus()
  await page.keyboard.press('Enter')
  await page.keyboard.press('Tab')
  await expect(page.getByTestId('task-recovery-key')).toBeFocused()
  await page.getByTestId('task-recovery-key').fill('synthetic-key-with-no-receipt')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('task-recovery-code')).toContainText('不等于任务没有执行')
  await expect(page.getByTestId('task-recovery-open')).toHaveCount(0)
  expect(submissions).toBe(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true)
  await page.getByTestId('task-recovery').screenshot({ path: testInfo.outputPath('narrow.png') })
})
