import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import type { ZbaPluginSettings } from '../../src/api/zba'

const ID = 'preset.feature.websearch'
const PATH = '/zba/v1/plugins/' + ID + '/settings'
const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'
const HEADERS = { Authorization: 'Bearer ' + ADMIN }
const triggerId = 'cap-settings-plugin:' + ID

async function readSettings(request: APIRequestContext): Promise<ZbaPluginSettings> {
  const response = await request.get(PATH, { headers: HEADERS })
  expect(response.status()).toBe(200)
  return response.json()
}
async function restore(request: APIRequestContext, value: string): Promise<void> {
  const current = await readSettings(request)
  if (current.values.linkPreviewMax === value) return
  const response = await request.put(PATH, { headers: HEADERS, data: {
    expectedRevision: current.revision, idempotencyKey: 'synthetic-restore-' + randomUUID(),
    edits: [{ key: 'linkPreviewMax', kind: 1, value }]
  } })
  expect(response.status()).toBe(200)
}
async function readConfiguration(request: APIRequestContext): Promise<{
  generation: string; version: string; view: { behavior: { linkPreviewMax: { value: number } } }
}> {
  const response = await request.post('/zba/v1/configuration/read', { headers: HEADERS, data: { kind: 1 } })
  expect(response.status()).toBe(200)
  return response.json()
}
async function openSettings(page: Page, token = ADMIN, admin = true): Promise<void> {
  await page.goto('/web/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-plugins').click()
  if (admin) await page.getByTestId('plugins-enable-admin').click()
  await page.getByTestId(triggerId).click()
  await expect(page.getByTestId('declared-plugin-settings-dialog')).toBeVisible()
  await expect(page.getByTestId('plugin-setting-linkPreviewMax')).toBeVisible()
}

for (const width of [1280, 390]) {
  test('actual research owner configuration shares legacy values and fits at ' + width, async ({ page }, testInfo) => {
    const original = await readSettings(page.request)
    const savedValue = original.values.linkPreviewMax === '2' ? '4' : '2'
    const legacyValue = savedValue === '2' ? 4 : 2
    try {
      await page.setViewportSize({ width, height: 950 })
      await openSettings(page)
      const dialog = page.getByTestId('declared-plugin-settings-dialog')
      await expect(dialog.locator('.declared-field')).toHaveCount(9)
      await expect(dialog.getByTestId('plugin-setting-webSearchReadMaxChars')).toHaveCount(0)
      await expect(dialog.getByTestId('config-field-linkPreviewMax')).toHaveCount(0)
      const count = dialog.getByTestId('plugin-setting-linkPreviewMax')
      await expect(count).toHaveAttribute('min', '0')
      await expect(count).toHaveAttribute('max', '5')
      await expect(count).toHaveAttribute('step', '1')
      await expect(count).toHaveValue(original.values.linkPreviewMax!)
      expect(await dialog.evaluate(node => node.scrollWidth <= node.clientWidth + 1)).toBe(true)
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1)
      const close = dialog.locator('.icon-close')
      const box = await close.boundingBox()
      expect(box).not.toBeNull()
      expect(Math.abs(box!.width - box!.height)).toBeLessThanOrEqual(1)
      expect(box!.width).toBeGreaterThanOrEqual(width < 600 ? 44 : 36)
      await page.screenshot({ path: testInfo.outputPath('research-settings-' + width + '.png'), fullPage: true })
      await count.fill(savedValue)
      const response = page.waitForResponse(result => new URL(result.url()).pathname === PATH && result.request().method() === 'PUT')
      await page.getByTestId('declared-plugin-settings-save').click()
      const saved = await response
      expect(saved.status()).toBe(200)
      expect(saved.request().postDataJSON()).toMatchObject({ expectedRevision: original.revision,
        edits: [{ key: 'linkPreviewMax', kind: 1, value: savedValue }] })
      expect(saved.request().postDataJSON().idempotencyKey).toBeTruthy()
      await expect(page.getByTestId('declared-plugin-settings-notice')).toContainText('已保存并热应用')
      expect((await readSettings(page.request)).values.linkPreviewMax).toBe(savedValue)
      const legacy = await readConfiguration(page.request)
      expect(legacy.view.behavior.linkPreviewMax.value).toBe(Number(savedValue))
      const external = await page.request.post('/zba/v1/configuration/save', { headers: HEADERS, data: {
        contractRevision: 'cfg-0.3.3', expectedVersion: legacy.version,
        key: `cfg1:${legacy.generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`,
        changes: { behavior: { linkPreviewMax: { kind: 1, value: legacyValue } } }, secrets: []
      } })
      expect(external.status()).toBe(200)
      expect((await external.json()).code).toBe(0)
      await page.getByTestId('declared-plugin-settings-reload').click()
      await expect(count).toHaveValue(String(legacyValue))
      await page.getByTestId('declared-plugin-settings-dialog-close').click()
      await expect(dialog).toHaveCount(0)
      await expect(page.getByTestId(triggerId)).toBeFocused()
    } finally { await restore(page.request, original.values.linkPreviewMax!) }
  })
}

test('research readonly owner fields refuse edits through the real server', async ({ page }, testInfo) => {
  const original = await readSettings(page.request)
  const writes: string[] = []
  page.on('request', request => { if (request.method() === 'PUT' && new URL(request.url()).pathname === PATH) writes.push(PATH) })
  await page.setViewportSize({ width: 390, height: 844 })
  await openSettings(page, READ, false)
  await expect(page.getByTestId('declared-plugin-settings-readonly')).toBeVisible()
  await expect(page.getByTestId('plugin-setting-linkPreviewMax')).toBeDisabled()
  await expect(page.getByTestId('declared-plugin-settings-save')).toBeDisabled()
  await page.screenshot({ path: testInfo.outputPath('research-readonly.png'), fullPage: true })
  expect(writes).toEqual([])
  const refused = await page.request.put(PATH, { headers: { Authorization: 'Bearer ' + READ }, data: {
    expectedRevision: original.revision, idempotencyKey: 'synthetic-readonly-' + randomUUID(),
    edits: [{ key: 'linkPreviewMax', kind: 1, value: '1' }]
  } })
  expect(refused.status()).toBe(403)
  expect(await readSettings(page.request)).toEqual(original)
})

test('research integer validation and keyboard draft close never send an invalid value', async ({ page }) => {
  const writes: string[] = []
  page.on('request', request => { if (request.method() === 'PUT' && new URL(request.url()).pathname === PATH) writes.push(PATH) })
  await openSettings(page)
  await page.getByTestId('plugin-setting-linkPreviewMax').fill('1.5')
  await page.getByTestId('declared-plugin-settings-save').click()
  await expect(page.getByTestId('declared-plugin-settings-notice')).toContainText('整数')
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('declared-plugin-settings-discard-confirmation')).toBeVisible()
  await page.getByTestId('declared-plugin-settings-discard-cancel').click()
  await expect(page.getByTestId('plugin-setting-linkPreviewMax')).toHaveValue('1.5')
  await page.keyboard.press('Escape')
  await page.getByTestId('declared-plugin-settings-discard-confirm').click()
  await expect(page.getByTestId('declared-plugin-settings-dialog')).toHaveCount(0)
  await expect(page.getByTestId(triggerId)).toBeFocused()
  expect(writes).toEqual([])
})

test('a lost research save reply preserves the original key and does not resend after reopen', async ({ page }, testInfo) => {
  const original = await readSettings(page.request)
  const value = original.values.linkPreviewMax === '2' ? '4' : '2'
  let writes = 0
  let key = ''
  await page.route('**' + PATH, async route => {
    if (route.request().method() !== 'PUT') return route.fallback()
    writes++
    key = route.request().postDataJSON().idempotencyKey
    const committed = await route.fetch()
    expect(committed.status()).toBe(200)
    await route.abort('failed')
  })
  try {
    await openSettings(page)
    await page.getByTestId('plugin-setting-linkPreviewMax').fill(value)
    await page.getByTestId('declared-plugin-settings-save').click()
    await expect(page.getByTestId('declared-plugin-settings-recovery')).toContainText('保存结果未知')
    await expect(page.getByTestId('declared-plugin-settings-recovery')).toContainText(key)
    expect((await readSettings(page.request)).values.linkPreviewMax).toBe(value)
    await page.getByTestId('declared-plugin-settings-reload').click()
    await expect(page.getByTestId('plugin-setting-linkPreviewMax')).toHaveValue(value)
    await page.getByTestId('declared-plugin-settings-dialog-close').click()
    await page.getByTestId(triggerId).click()
    await expect(page.getByTestId('declared-plugin-settings-recovery')).toContainText(key)
    await expect(page.getByTestId('declared-plugin-settings-save')).toBeDisabled()
    await page.screenshot({ path: testInfo.outputPath('research-original-key.png'), fullPage: true })
    await page.getByTestId('declared-plugin-settings-reconcile-ack').check()
    await page.getByTestId('declared-plugin-settings-reconcile').click()
    await expect(page.getByTestId('declared-plugin-settings-recovery')).toHaveCount(0)
    expect(writes).toBe(1)
  } finally { await page.unroute('**' + PATH); await restore(page.request, original.values.linkPreviewMax!) }
})

test('a committed application-failure envelope shows saved values and permits reread without resend', async ({ page }, testInfo) => {
  // The real server commits synthetic settings; the failure envelope is injected here.
  // A genuine runtime-publication fault is covered by the independent native probe.
  const original = await readSettings(page.request)
  const value = original.values.linkPreviewMax === '2' ? '4' : '2'
  let writes = 0
  await page.route('**' + PATH, async route => {
    if (route.request().method() !== 'PUT') {
      const response = await route.fetch()
      return route.fulfill({ response, json: { ...await response.json(), restartRequired: writes > 0 } })
    }
    writes++
    const response = await route.fetch()
    expect(response.status()).toBe(200)
    return route.fulfill({ status: 503, json: { error: { code: 'plugin_settings_application_failed', message: 'Synthetic publication fault' } } })
  })
  try {
    await openSettings(page)
    await page.getByTestId('plugin-setting-linkPreviewMax').fill(value)
    await page.getByTestId('declared-plugin-settings-save').click()
    await expect(page.getByTestId('declared-plugin-settings-notice')).toContainText('设置已保存')
    await expect(page.getByTestId('error-notice')).toContainText('应用未完成')
    await expect(page.getByTestId('declared-plugin-settings-recovery')).toHaveCount(0)
    await page.getByTestId('declared-plugin-settings-reload').click()
    await expect(page.getByTestId('plugin-setting-linkPreviewMax')).toHaveValue(value)
    await expect(page.getByTestId('declared-plugin-settings-restart')).toContainText('尚未应用')
    await page.screenshot({ path: testInfo.outputPath('research-application-failed.png'), fullPage: true })
    expect(writes).toBe(1)
  } finally { await page.unroute('**' + PATH); await restore(page.request, original.values.linkPreviewMax!) }
})

test('research settings authentication expiry closes the session before further edits', async ({ page }) => {
  let reads = 0
  let writes = 0
  await page.route('**' + PATH, async route => {
    if (route.request().method() === 'PUT') { writes++; return route.abort('failed') }
    if (++reads === 1) return route.fallback()
    return route.fulfill({ status: 401, json: { error: { code: 'unauthorized', message: 'Synthetic expired session' } } })
  })
  await openSettings(page)
  await page.getByTestId('declared-plugin-settings-reload').click()
  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('declared-plugin-settings-dialog')).toHaveCount(0)
  expect(writes).toBe(0)
})
