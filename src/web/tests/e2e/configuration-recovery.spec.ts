import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { writeFile } from 'node:fs/promises'

const headers = { Authorization: 'Bearer synthetic-web-admin-token' }
async function enterConfiguration(page: Page) {
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
}
async function read(request: APIRequestContext): Promise<{ generation: string; version: string; view: { behavior: { privateCooldownSeconds: { value: number } } } }> {
  const response = await request.post('/zba/v1/configuration/read', { headers, data: { kind: 1 } })
  expect(response.ok()).toBeTruthy()
  return response.json()
}
function originalKey(generation: string): string {
  return 'cfg1:' + generation + ':' + Math.floor(Date.now() / 1000) + ':' + randomUUID().replace(/-/g, '')
}
async function restore(request: APIRequestContext, original: number) {
  const current = await read(request)
  if (current.view.behavior.privateCooldownSeconds.value === original) return
  const response = await request.post('/zba/v1/configuration/save', { headers, data: {
    contractRevision: 'cfg-0.3.3', expectedVersion: current.version, key: originalKey(current.generation),
    changes: { behavior: { privateCooldownSeconds: { kind: 1, value: original } } }, secrets: []
  } })
  expect(response.status()).toBe(200)
}

for (const responseStatus of [0, 408, 409, 429]) {
test('finds a saved configuration after refresh without replaying or clearing a new draft (HTTP ' + responseStatus + ')', async ({ page, request }, testInfo) => {
  const before = await read(request)
  const original = before.view.behavior.privateCooldownSeconds.value
  expect(Number.isFinite(original)).toBe(true)
  let savedKey = ''
  let committedVersion = ''
  let submissions = 0
  await page.route('**/zba/v1/configuration/save', async route => {
    submissions += 1
    const body: { key: string } = route.request().postDataJSON()
    savedKey = body.key
    const saved = await route.fetch()
    expect(saved.status()).toBe(200)
    const result = await saved.json()
    expect(result.receipt.commitState).toBe(1)
    committedVersion = result.receipt.committedVersion
    const evidencePath = testInfo.outputPath('upstream-commit.json')
    await writeFile(evidencePath, JSON.stringify({ upstreamStatus: saved.status(), commitState: result.receipt.commitState, committedVersion, downstreamStatus: responseStatus || 'connectionreset' }))
    await testInfo.attach('upstream-commit', { path: evidencePath, contentType: 'application/json' })
    if (responseStatus === 0) await route.abort('connectionreset')
    else await route.fulfill({ status: responseStatus, contentType: 'application/json', body: JSON.stringify({ error: { code: 'http_error', message: 'synthetic proxy response' } }) })
  })
  try {
    await page.goto('/')
    await enterConfiguration(page)
    await page.getByTestId('config-field-privateCooldownSeconds').locator('input').fill(String(original + 1))
    await page.getByTestId('config-save').click()
    await expect(page.getByTestId('config-unconfirmed')).toBeVisible()
    expect(savedKey).toMatch(/^cfg1:/)
    const visibleKey = page.getByTestId('configuration-operation-key')
    const retainedKey = await visibleKey.inputValue()
    expect(retainedKey).toBe(savedKey)
    await visibleKey.focus()
    expect(await visibleKey.evaluate((node: HTMLInputElement) => [node.selectionStart, node.selectionEnd])).toEqual([0, retainedKey.length])
    await page.setViewportSize({ width: 375, height: 900 })
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
    await page.getByTestId('configuration-operation-key-notice').screenshot({ path: testInfo.outputPath('pending-key.png') })
    await expect(page.getByTestId('config-recovery-key')).toBeDisabled()
    await page.reload()
    await enterConfiguration(page)
    await expect(page.getByTestId('config-unconfirmed')).toHaveCount(0)
    const input = page.getByTestId('config-field-privateCooldownSeconds').locator('input')
    await input.fill(String(original + 2))
    await page.getByTestId('config-recovery').locator('summary').click()
    await page.getByTestId('config-recovery-key').fill(retainedKey)
    await page.getByTestId('config-recovery-key').press('Enter')
    const receipt = page.getByTestId('config-recovery-receipt')
    await expect(receipt).toBeVisible()
    await expect(receipt).toContainText('该次持久化版本' + committedVersion)
    await expect(receipt).toContainText('历史回执不代表当前配置值和运行状态')
    await expect(input).toHaveValue(String(original + 2))
    await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')
    expect(submissions).toBe(1)
    await page.getByTestId('config-recovery').screenshot({ path: testInfo.outputPath('recovered.png') })
    await page.getByTestId('config-discard').click()
  } finally {
    await page.unroute('**/zba/v1/configuration/save')
    await restore(request, original)
  }
})
}

test('looks up an absent receipt with the keyboard on a narrow viewport without saving', async ({ page, request }, testInfo) => {
  await page.setViewportSize({ width: 375, height: 900 })
  const current = await read(request)
  let saves = 0
  page.on('request', req => { if (new URL(req.url()).pathname.endsWith('/configuration/save')) saves += 1 })
  await page.goto('/')
  await enterConfiguration(page)
  const summary = page.getByTestId('config-recovery').locator('summary')
  await summary.focus()
  await page.keyboard.press('Enter')
  await page.keyboard.press('Tab')
  await expect(page.getByTestId('config-recovery-key')).toBeFocused()
  await page.getByTestId('config-recovery-key').fill(originalKey(current.generation))
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('config-recovery-code')).toContainText('未找到')
  await expect(page.getByTestId('config-recovery-receipt')).toHaveCount(0)
  expect(saves).toBe(0)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await page.getByTestId('config-recovery').screenshot({ path: testInfo.outputPath('narrow.png') })
})
