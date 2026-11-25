import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'
import { retiredReplyFields } from '../../src/features/configuration/reply-pacing'

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const headers = { Authorization: 'Bearer ' + ADMIN_TOKEN }
const keys = ['groupInterjectionProbability', 'groupInterjectionCooldownSeconds',
  'groupContinuationWaitSeconds', 'privateContinuationWaitSeconds'] as const
const defaultPacingValues = { groupInterjectionProbability: 20, groupInterjectionCooldownSeconds: 30,
  groupContinuationWaitSeconds: 3, privateContinuationWaitSeconds: 1.5 }

type Configuration = {
  version: string
  generation: string
  contractRevision: string
  view: { behavior: Record<string, { value?: unknown }> }
}

async function readConfiguration(request: APIRequestContext): Promise<Configuration> {
  const response = await request.post('/zba/v1/configuration/read', { headers, data: { kind: 1 } })
  expect(response.ok()).toBeTruthy()
  return response.json()
}

async function writePacingValues(request: APIRequestContext, values: Record<string, unknown>): Promise<void> {
  const latest = await readConfiguration(request)
  const behavior = Object.fromEntries(keys.map(key => [key, { kind: 1, value: values[key] }]))
  const response = await request.post('/zba/v1/configuration/save', { headers, data: {
    contractRevision: latest.contractRevision, expectedVersion: latest.version,
    key: `cfg1:${latest.generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`,
    changes: { behavior }, secrets: []
  } })
  expect(response.ok(), `pacing save status ${response.status()}`).toBeTruthy()
}

async function openSettings(page: Page): Promise<void> {
  await page.goto('/')
  const token = page.getByTestId('token-input')
  if (await token.isVisible()) {
    await token.fill(ADMIN_TOKEN)
    await page.getByTestId('connect-button').click()
  }
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  const enableAdmin = page.getByTestId('config-enable-admin')
  if (await enableAdmin.isVisible()) await enableAdmin.click()
}

async function save(page: Page): Promise<void> {
  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
}

for (const width of [1280, 390]) {
  test(`single pacing configuration persists and excludes retired controls at ${width}px`, async ({ page, request }, testInfo) => {
    await page.setViewportSize({ width, height: width < 600 ? 844 : 960 })
    const original = await readConfiguration(request)
    const field = (key: string) => page.getByTestId('config-field-' + key)
    const input = (key: string) => field(key).locator('input.config-input')
    try {
      for (const key of retiredReplyFields) expect(original.view.behavior[key]).toBeUndefined()
      // Other specs share this host and legitimately save settings; isolate these four values.
      await writePacingValues(request, defaultPacingValues)
      await openSettings(page)
      for (const [key, value] of Object.entries(defaultPacingValues)) {
        await expect(input(key)).toHaveValue(String(value))
        await expect(input(key)).toBeEnabled()
      }
      for (const key of retiredReplyFields) await expect(field(key)).toHaveCount(0)
      await input('groupInterjectionProbability').fill('101')
      await page.getByTestId('config-save').click()
      await expect(input('groupInterjectionProbability')).toHaveAttribute('aria-invalid', 'true')
      await expect(page.getByTestId('config-field-errors')).toBeVisible()
      expect((await readConfiguration(request)).view.behavior.groupInterjectionProbability?.value).toBe(20)
      const changed = { groupInterjectionProbability: 0, groupInterjectionCooldownSeconds: 45,
        groupContinuationWaitSeconds: 4, privateContinuationWaitSeconds: 2.5 }
      for (const [key, value] of Object.entries(changed)) await input(key).fill(String(value))
      await save(page)
      const saved = await readConfiguration(request)
      for (const [key, value] of Object.entries(changed)) expect(saved.view.behavior[key]?.value).toBe(value)
      await openSettings(page)
      for (const [key, value] of Object.entries(changed)) await expect(input(key)).toHaveValue(String(value))
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await page.screenshot({ path: testInfo.outputPath(`pacing-only-${width}.png`), fullPage: true })
      await page.getByTestId('sysconfig-tab-conversation').click()
      const pane = page.getByTestId('sysconfig-pane-conversation')
      await expect(pane).toBeVisible()
      for (const key of [...retiredReplyFields, ...keys]) await expect(pane.getByTestId('config-field-' + key)).toHaveCount(0)
      await expect(pane.getByTestId('config-field-enableProactive')).toBeVisible()
      await expect(pane.getByTestId('config-field-splitReplies')).toBeVisible()
    } finally {
      await writePacingValues(request, Object.fromEntries(keys.map(key => [key, original.view.behavior[key]?.value])))
      const restored = await readConfiguration(request)
      for (const key of keys) expect(restored.view.behavior[key]?.value).toEqual(original.view.behavior[key]?.value)
    }
  })
}

test('retired input rejects the whole structured or legacy save without advancing version', async ({ request }) => {
  const before = await readConfiguration(request)
  for (const key of retiredReplyFields) {
    const value = key.startsWith('enable') || key === 'replyPacingEnabled' ? false : 1
    const response = await request.post('/zba/v1/configuration/save', { headers, data: {
      contractRevision: before.contractRevision, expectedVersion: before.version,
      key: `cfg1:${before.generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`,
      changes: { behavior: { groupInterjectionProbability: { kind: 1, value: 75 }, [key]: { kind: 1, value } } }, secrets: []
    } })
    expect(response.status(), key).toBe(400)
    const legacy = await request.post('http://127.0.0.1:18127/api/settings', {
      headers: { 'X-Panel-Token': 'synthetic-web-panel-token' },
      data: { groupInterjectionProbability: 75, [key]: value }
    })
    expect(legacy.status(), key).toBe(400)
    const after = await readConfiguration(request)
    expect(after.version).toBe(before.version)
    expect(after.view.behavior.groupInterjectionProbability?.value).toBe(before.view.behavior.groupInterjectionProbability?.value)
  }
})
