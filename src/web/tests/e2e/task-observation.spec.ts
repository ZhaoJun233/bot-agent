import { expect, test } from '@playwright/test'
import fs from 'node:fs/promises'

const token = 'synthetic-web-admin-token'

async function openTasks(page: import('@playwright/test').Page) {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()
  await expect(page.getByTestId('tasks')).toBeVisible()
}

test('exports a private client cohort from a real isolated host', async ({ page }, info) => {
  await openTasks(page)
  await page.getByTestId('task-enable-admin').click()
  await page.getByTestId('task-source-key').fill('panel:synthetic-observation')
  await page.getByTestId('task-session-id').fill('synthetic-observation-session')
  await page.getByTestId('task-prompt').fill('synthetic observation body')
  const submitted = page.waitForResponse(response => response.request().method() === 'POST'
    && new URL(response.url()).pathname === '/zba/v1/tasks')
  await page.getByTestId('task-submit').click()
  const acceptedTask = await (await submitted).json() as { id: string }
  await expect(page.getByTestId('task-submission-state')).toContainText('已受理')
  // The isolated host's model endpoint is deliberately unreachable; observe its real terminal failure.
  await expect(page.getByTestId('task-item-' + acceptedTask.id).getByTestId('task-state')).toHaveText('失败', { timeout: 20_000 })
  const key = (await page.getByTestId('task-idempotency-key').textContent())?.trim()
  await page.getByTestId('task-observation').locator('summary').click()
  const download = page.waitForEvent('download')
  await page.getByTestId('task-observation-export').click()
  const file = info.outputPath('observation.json')
  await (await download).saveAs(file)
  const raw = await fs.readFile(file, 'utf8')
  const summary = JSON.parse(raw)
  expect(summary).toMatchObject({ attempts: 1, accepted: 1, rejected: 0, acceptedReplays: 0,
    acceptanceUnknown: 0, completeAttempts: true, taskUsage: null, attributedCost: null, actualUsefulness: null,
    states: { failed: 1, inFlight: 0 }, latency: { knownCount: 1, unknownCount: 0 } })
  expect(Number.isFinite(summary.latency.p95Ms)).toBe(true)
  expect(summary.latency.p95Ms).toBeGreaterThan(0)
  for (const secret of [token, acceptedTask.id, 'panel:synthetic-observation', 'synthetic-observation-session', 'synthetic observation body', key]) {
    if (secret) expect(raw).not.toContain(secret)
  }
  expect(Object.values<number>(summary.states).reduce((sum, count) => sum + count, 0)).toBe(1)
  await page.screenshot({ path: info.outputPath('task-observation-desktop.png'), fullPage: true })
  await openTasks(page)
  await page.getByTestId('task-observation').locator('summary').click()
  const refreshedDownload = page.waitForEvent('download')
  await page.getByTestId('task-observation-export').click()
  const refreshedFile = info.outputPath('observation-after-refresh.json')
  await (await refreshedDownload).saveAs(refreshedFile)
  const refreshed = JSON.parse(await fs.readFile(refreshedFile, 'utf8'))
  expect(refreshed.scopeId).not.toBe(summary.scopeId)
  expect(refreshed).toMatchObject({ attempts: 0, accepted: 0, samples: [] })
})

test('narrow layout supports keyboard export and a new client starts an independent cohort', async ({ page }, info) => {
  await page.setViewportSize({ width: 375, height: 812 })
  await openTasks(page)
  const section = page.getByTestId('task-observation')
  await section.locator('summary').focus()
  await section.locator('summary').press('Enter')
  await expect(page.getByTestId('task-observation-export')).toBeVisible()
  await section.locator('summary').press('Tab')
  await expect(page.getByTestId('task-observation-export')).toBeFocused()
  const download = page.waitForEvent('download')
  await page.getByTestId('task-observation-export').press('Enter')
  const file = info.outputPath('observation-empty.json')
  await (await download).saveAs(file)
  expect(JSON.parse(await fs.readFile(file, 'utf8'))).toMatchObject({ attempts: 0, accepted: 0,
    survivesRefresh: false, latency: { p50Ms: null, p95Ms: null } })
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.screenshot({ path: info.outputPath('task-observation-narrow.png'), fullPage: true })
})
