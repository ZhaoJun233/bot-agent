import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

// #111：能力设置里的「模型字段」与「白名单管理」两条端到端用例。
// 配置页的保存/冲突/秘密输入等通用行为已在 configuration.spec.ts 覆盖；
// 这里只钉住两个能力弹窗真的能改到真实宿主，并且回读一致。
const ADMIN_TOKEN = 'synthetic-web-admin-token'
const CONFIG_HEADERS = { Authorization: `Bearer ${ADMIN_TOKEN}` }

type ConfigurationSnapshot = {
  generation: string
  version: string
  view: {
    model: { model: { value: string } }
    platform: { whitelistGroups: { value: string } }
  }
}

async function openConfiguration(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(ADMIN_TOKEN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
}

async function openCapability(page: Page, id: string, navKey: string) {
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId(`ability-settings-${id}`).click()
  const dialog = page.getByTestId('ability-configuration-dialog')
  await expect(dialog).toBeVisible()
  await dialog.getByTestId(`config-nav-${navKey}`).click()
  return dialog
}

async function readConfiguration(request: APIRequestContext): Promise<ConfigurationSnapshot> {
  const response = await request.post('/zba/v1/configuration/read', { data: { kind: 1 }, headers: CONFIG_HEADERS })
  expect(response.ok()).toBeTruthy()
  return response.json()
}

function operationKey(generation: string): string {
  return `cfg1:${generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`
}

async function externalSave(request: APIRequestContext, generation: string, version: string, changes: unknown): Promise<void> {
  const response = await request.post('/zba/v1/configuration/save', {
    data: { contractRevision: 'cfg-0.3.3', expectedVersion: version, key: operationKey(generation), changes, secrets: [] },
    headers: CONFIG_HEADERS
  })
  expect(response.status()).toBe(200)
}

test('能力设置：模型字段通过面板保存并回读（真实宿主）', async ({ page, request }) => {
  test.setTimeout(60_000)
  const before = await readConfiguration(request)
  const original = before.view.model.model.value
  const updated = original === 'mock-model-e2e' ? 'mock-model-e2e-2' : 'mock-model-e2e'

  await openConfiguration(page)
  const dialog = await openCapability(page, 'models', 'model')
  const input = dialog.getByTestId('config-field-model').locator('input')
  await expect(input).toHaveValue(original)

  try {
    await input.fill(updated)
    await expect(dialog.getByTestId('config-save-bar')).toContainText('1 项修改')
    await dialog.getByTestId('config-save').click()
    await expect(dialog.getByTestId('config-receipt')).toBeVisible({ timeout: 20_000 })

    const after = await readConfiguration(request)
    expect(after.view.model.model.value).toBe(updated)
  } finally {
    const latest = await readConfiguration(request)
    if (latest.view.model.model.value !== original) {
      await externalSave(request, latest.generation, latest.version, { model: { model: { kind: 1, value: original } } })
    }
  }
})

test('能力设置：白名单通过面板保存并回读（真实宿主）', async ({ page, request }) => {
  test.setTimeout(60_000)
  const before = await readConfiguration(request)
  const original = before.view.platform.whitelistGroups.value
  const updated = '20002, 20003'

  await openConfiguration(page)
  const dialog = await openCapability(page, 'platform', 'platform')
  const input = dialog.getByTestId('config-field-whitelistGroups').locator('input')
  await expect(input).toHaveValue(original)

  try {
    await input.fill(updated)
    await expect(dialog.getByTestId('config-save-bar')).toContainText('1 项修改')
    await dialog.getByTestId('config-save').click()
    await expect(dialog.getByTestId('config-receipt')).toBeVisible({ timeout: 20_000 })

    const after = await readConfiguration(request)
    expect(after.view.platform.whitelistGroups.value).toBe(updated)
  } finally {
    const latest = await readConfiguration(request)
    if (latest.view.platform.whitelistGroups.value !== original) {
      await externalSave(request, latest.generation, latest.version, { platform: { whitelistGroups: { kind: 1, value: original } } })
    }
  }
})
