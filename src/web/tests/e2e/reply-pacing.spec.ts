import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const headers = { Authorization: 'Bearer ' + ADMIN_TOKEN }
const keys = ['replyPacingEnabled', 'groupInterjectionProbability', 'groupInterjectionCooldownSeconds',
  'groupContinuationWaitSeconds', 'privateContinuationWaitSeconds', 'aiDesire'] as const

type Configuration = {
  version: string
  generation: string
  view: { behavior: Record<string, { value?: unknown }> }
}

async function readConfiguration(request: APIRequestContext): Promise<Configuration> {
  const response = await request.post('/zba/v1/configuration/read', { headers, data: { kind: 1 } })
  expect(response.ok()).toBeTruthy()
  return response.json()
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
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId('ability-settings-conversation').click()
  await expect(page.getByTestId('ability-configuration-dialog')).toBeVisible()
}

async function save(page: Page): Promise<void> {
  const dialog = page.getByTestId('ability-configuration-dialog')
  await dialog.getByTestId('config-save').click()
  await expect(dialog.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
}

test('pacing settings persist through real host saves, reloads and mode changes', async ({ page, request }, testInfo) => {
  const original = await readConfiguration(request)
  const dialog = page.getByTestId('ability-configuration-dialog')
  const field = (key: string) => dialog.getByTestId('config-field-' + key)
  const input = (key: string) => field(key).locator('input.config-input')
  const toggle = () => field('replyPacingEnabled').locator('select.config-input')
  try {
    expect(original.view.behavior.replyPacingEnabled?.value).toBe(false)
    await openSettings(page)
    await expect(toggle()).toHaveValue('false')
    for (const [key, value] of Object.entries({ groupInterjectionProbability: '20',
      groupInterjectionCooldownSeconds: '30', groupContinuationWaitSeconds: '3', privateContinuationWaitSeconds: '1.5' })) {
      await expect(input(key)).toHaveValue(value)
    }
    await expect(input('aiDesire')).toBeEnabled()
    await input('aiDesire').fill('42')
    await toggle().selectOption('true')
    await expect(input('aiDesire')).toBeDisabled()
    await expect(field('aiDesire').getByRole('button', { name: '还原', exact: true })).toBeDisabled()
    // Real host marks this field non-clearable; all offered mutation controls stay locked.
    await expect(field('aiDesire').locator('.config-value button:enabled')).toHaveCount(0)
    await input('groupInterjectionProbability').fill('101')
    await dialog.getByTestId('config-save').click()
    await expect(input('groupInterjectionProbability')).toHaveAttribute('aria-invalid', 'true')
    await expect(dialog.getByTestId('config-field-errors')).toBeVisible()
    expect((await readConfiguration(request)).view.behavior.groupInterjectionProbability?.value).toBe(20)
    await input('groupInterjectionProbability').fill('0')
    await input('groupInterjectionCooldownSeconds').fill('45')
    await input('groupContinuationWaitSeconds').fill('4')
    await input('privateContinuationWaitSeconds').fill('2.5')
    await save(page)
    const enabled = await readConfiguration(request)
    for (const [key, value] of Object.entries({ replyPacingEnabled: true, groupInterjectionProbability: 0,
      groupInterjectionCooldownSeconds: 45, groupContinuationWaitSeconds: 4,
      privateContinuationWaitSeconds: 2.5, aiDesire: 42 })) {
      expect(enabled.view.behavior[key]?.value).toBe(value)
    }
    await openSettings(page)
    await expect(toggle()).toHaveValue('true')
    await expect(input('groupInterjectionProbability')).toHaveValue('0')
    await expect(input('privateContinuationWaitSeconds')).toHaveValue('2.5')
    await expect(input('aiDesire')).toHaveValue('42')
    await expect(input('aiDesire')).toBeDisabled()
    await page.screenshot({ path: testInfo.outputPath('pacing-enabled.png'), fullPage: true })
    await toggle().selectOption('false')
    await expect(input('aiDesire')).toBeEnabled()
    await expect(input('aiDesire')).toHaveValue('42')
    await save(page)
    await openSettings(page)
    await expect(toggle()).toHaveValue('false')
    await expect(input('aiDesire')).toBeEnabled()
    await expect(input('aiDesire')).toHaveValue('42')
    expect((await readConfiguration(request)).view.behavior.replyPacingEnabled?.value).toBe(false)
  } catch (error) {
    console.error('SCENARIO FAILURE', String(error))
    throw error
  } finally {
    const latest = await readConfiguration(request)
    const behavior = Object.fromEntries(keys.map(key => [key, original.view.behavior[key]?.value === undefined
      ? { kind: 2 } : { kind: 1, value: original.view.behavior[key].value }]))
    const response = await request.post('/zba/v1/configuration/save', { headers, data: {
      contractRevision: 'cfg-0.3.3', expectedVersion: latest.version,
      key: `cfg1:${latest.generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`,
      changes: { behavior }, secrets: []
    } })
    const restored = await response.json()
    expect(response.ok(), JSON.stringify({ status: response.status(), restored, version: latest.version })).toBeTruthy()
    const afterRestore = await readConfiguration(request)
    for (const key of keys) expect(afterRestore.view.behavior[key]?.value).toEqual(original.view.behavior[key]?.value)
  }
})
