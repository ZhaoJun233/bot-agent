import { expect, test, type Page, type Response } from '@playwright/test'
import { quota } from '../fixtures/quotas'
const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'

async function expectHotApplied(page: Page, response: Response, enabled: boolean): Promise<void> {
  expect(response.status()).toBe(200)
  const saved = await response.json()
  expect(saved.code).toBe(0)
  expect(saved.errors).toEqual([])
  expect(saved.receipt).toMatchObject({
    commitState: 1, applyState: 1,
    steps: expect.arrayContaining([{ step: 7, state: 1 }])
  })
  expect(saved.receipt.key).toBe(response.request().postDataJSON().key)
  expect(saved.receipt.committedVersion).toBeTruthy()

  // Query the durable receipt independently of the save response.
  const headers = { Authorization: 'Bearer synthetic-web-admin-token' }
  const found = await page.request.post('/zba/v1/configuration/find', {
    headers, data: { key: saved.receipt.key }
  })
  expect(found.status()).toBe(200)
  expect(await found.json()).toMatchObject({ code: 0, receipt: saved.receipt })

  // Inspect the registry-backed runtime, not just the persisted switch value.
  await expect.poll(async () => {
    const runtime = await page.request.post('/zba/v1/capabilities/inspect', {
      headers, data: { key: { kind: 'plugin', key: 'preset.feature.token-quota' } }
    })
    expect(runtime.status()).toBe(200)
    const result = await runtime.json()
    expect(result.code).toBe(0)
    return result.descriptor?.runtimeState
  }).toBe(enabled ? 4 : 2) // Running / Disabled; no host restart.
}
async function open(page: import('@playwright/test').Page, token: string) {
  await page.goto('/web/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('cap-item-plugin:preset.feature.token-quota')).toContainText('Token 配额')
}
test('quota plugin: real host card, read-only list and keyboard focus under /web', async ({ page }) => {
  await open(page, READ)
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/quotas')
  const trigger = page.getByTestId('cap-settings-plugin:preset.feature.token-quota')
  await trigger.click()
  const list = await response
  expect(list.status()).toBe(200)
  const body = await list.json() as { items: unknown[] }
  expect(Array.isArray(body.items)).toBe(true)
  await expect(page.getByTestId('quota-readonly')).toBeVisible()
  if (!body.items.length) await expect(page.getByTestId('quota-empty')).toBeVisible()
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('token-quota-dialog')).toHaveCount(0)
  await expect(trigger).toBeFocused()
})
test('quota plugin: persistent enablement and restore hot-apply with durable receipts', async ({ page }) => {
  await open(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')
  await page.getByTestId('cap-settings-plugin:preset.feature.token-quota').click()
  const toggle = page.getByTestId('quota-plugin-toggle')
  await expect(toggle).toBeEnabled()
  const original = await toggle.innerText()
  const originallyEnabled = original.includes('停用')
  try {
    const save = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/configuration/save')
    await toggle.click()
    await expectHotApplied(page, await save, !originallyEnabled)
    await expect(page.getByTestId('quota-plugin-desired')).toContainText(originallyEnabled ? '已保存设置：停用' : '已保存设置：启用')
    await expect(page.getByTestId('quota-plugin-switch')).toContainText(originallyEnabled ? '当前运行状态：已停用' : '当前运行状态：运行中')
    await expect(page.getByTestId('quota-plugin-switch')).not.toContainText('需重启')
    await expect(toggle).toBeEnabled()
    await expect(toggle).not.toHaveText(original)
  } finally {
    // Restore this isolated host and verify both the durable receipt and runtime again.
    if (await toggle.innerText() !== original) {
      const restore = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/configuration/save')
      await toggle.click()
      await expectHotApplied(page, await restore, originallyEnabled)
      await expect(page.getByTestId('quota-plugin-desired')).toContainText(originallyEnabled ? '已保存设置：启用' : '已保存设置：停用')
      await expect(page.getByTestId('quota-plugin-switch')).toContainText(originallyEnabled ? '当前运行状态：运行中' : '当前运行状态：已停用')
      await expect(page.getByTestId('quota-plugin-switch')).not.toContainText('需重启')
      await expect(toggle).toHaveText(original)
      await expect(toggle).toBeEnabled()
    }
  }
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('token-quota-dialog')).toHaveCount(0)
})
test('quota plugin: real host rejects synthetic unknown source without retry', async ({ page, request }) => {
  await open(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')
  // Only the visible source list is synthetic. The mutation and rejection come from the actual host.
  await page.route('**/zba/v1/quotas', route => route.fulfill({ json: { items: [quota({ sourceKey: 'panel:synthetic-quota-unknown' })] } }))
  await page.getByTestId('cap-settings-plugin:preset.feature.token-quota').click()
  await page.getByTestId('quota-edit-0').click()
  await page.getByTestId('quota-limit').fill('120000')
  const writes: string[] = []
  page.on('request', r => { if (new URL(r.url()).pathname === '/zba/v1/quotas/save') writes.push(r.postDataJSON().idempotencyKey as string) })
  const response = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/quotas/save')
  await page.getByTestId('quota-save').click()
  const rejected = await response
  expect(rejected.status()).toBe(404)
  expect(await rejected.json()).toMatchObject({ error: { code: 'quota_source_not_found' } })
  await expect(page.getByTestId('quota-unknown')).toHaveCount(0)
  await expect(page.getByTestId('quota-limit')).toHaveValue('120000')
  expect(writes).toHaveLength(1)
  const receipt = await request.post('/zba/v1/quotas/find', { headers: { Authorization: 'Bearer ' + ADMIN }, data: { idempotencyKey: writes[0] } })
  expect(receipt.status()).toBe(200); expect(await receipt.json()).toEqual({ found: false })
  await page.keyboard.press('Escape'); await expect(page.getByTestId('quota-discard')).toBeVisible()
  await page.getByTestId('quota-discard-confirm').click()
  await expect(page.getByTestId('token-quota-dialog')).toHaveCount(0)
})
