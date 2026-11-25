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
    registered: {
      providers: Array<{
        key: string
        credential: number
        credentialKey?: string | null
        credentialConfigured?: boolean
        baseUrl: { value?: string }
        modelName: { value?: string }
      }>
    }
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

async function openCapability(page: Page, id: 'models' | 'qq.private', navKey: string) {
  if (id === 'models') {
    await page.getByTestId('nav-providers').click()
    await page.getByTestId('providers-routing-settings').click()
  } else {
    await page.getByTestId('nav-platforms').click()
    await page.getByTestId(`platform-policy-${id}`).click()
  }
  const dialog = page.getByTestId('ability-configuration-dialog')
  await expect(dialog).toBeVisible()
  const nav = dialog.getByTestId(`config-nav-${navKey}`)
  if (await nav.count()) await nav.click()
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

test('模型供应商：主供应商编辑模型名与集成凭据，路由抽屉不再混入地址与密钥（真实宿主）', async ({ page, request }) => {
  test.setTimeout(60_000)
  const before = await readConfiguration(request)
  const original = before.view.model.model.value
  const updated = original === 'mock-model-e2e' ? 'mock-model-e2e-2' : 'mock-model-e2e'

  await openConfiguration(page)
  await page.getByTestId('nav-providers').click()

  // 路由与采样参数抽屉不再混入 model / modelBaseUrl / 槽位 0 秘密 / 旧供应商表
  await page.getByTestId('providers-routing-settings').click()
  const routingDialog = page.getByTestId('ability-configuration-dialog')
  await expect(routingDialog).toBeVisible()
  await expect(routingDialog.getByTestId('config-field-fastReply')).toBeVisible()
  await expect(routingDialog.getByTestId('config-field-model')).toHaveCount(0)
  await expect(routingDialog.getByTestId('config-field-modelBaseUrl')).toHaveCount(0)
  await expect(routingDialog.getByTestId('config-secrets')).toHaveCount(0)
  await expect(routingDialog.getByTestId('config-providers')).toHaveCount(0)
  await routingDialog.getByTestId('ability-configuration-dialog-close').click()

  // URL、默认模型与主 API Key 全部收口到 primary 供应商抽屉
  await page.getByTestId('cap-provider-edit-provider:primary').click()
  const editor = page.getByTestId('provider-editor')
  await expect(editor).toBeVisible()
  await expect(editor.getByTestId('provider-endpoint')).toBeVisible()
  await expect(editor.getByTestId('provider-primary-secret')).toBeVisible()
  await expect(editor.getByTestId('provider-priority')).toBeDisabled()
  await expect(editor.getByTestId('provider-enabled')).toBeDisabled()
  await expect(editor.getByTestId('provider-delete')).toHaveCount(0)
  const input = editor.getByTestId('provider-model')
  await expect(input).toHaveValue(original)

  try {
    await input.fill(updated)
    await editor.getByTestId('provider-save').click()
    await expect(editor.getByTestId('provider-notice')).toBeVisible({ timeout: 20_000 })

    const after = await readConfiguration(request)
    expect(after.view.model.model.value).toBe(updated)
    expect(after.view.registered.providers.find(p => p.key === 'primary')?.modelName.value).toBe(updated)
  } finally {
    const latest = await readConfiguration(request)
    if (latest.view.model.model.value !== original) {
      await externalSave(request, latest.generation, latest.version, { model: { model: { kind: 1, value: original } } })
    }
  }
})

test('模型供应商：新建供应商一步原子提交 API 根地址与独立 API Key 并回读（真实宿主）', async ({ page, request }) => {
  test.setTimeout(60_000)
  const before = await readConfiguration(request)
  const existingKeys = new Set(before.view.registered.providers.map(p => p.key))
  let createdKey: string | null = null
  let createdCredentialKey: string | null = null

  await openConfiguration(page)
  await page.getByTestId('nav-providers').click()
  await page.getByTestId('cap-add-provider').click()
  const editor = page.getByTestId('provider-editor')
  await expect(editor).toBeVisible()

  try {
    await editor.getByTestId('provider-name').fill('E2E 独立供应商')
    await editor.getByTestId('provider-endpoint').fill('https://example.com/e2e-atomic/v1')
    await editor.getByTestId('provider-model').fill('synthetic-e2e-atomic-model')
    // 无需手写 credentialKey，直接填写 API Key 即可一步保存
    await editor.getByTestId('provider-credential-secret').fill('sk-synthetic-e2e-atomic-key')
    await editor.getByTestId('provider-save').click()
    await expect(editor.getByTestId('provider-notice')).toBeVisible({ timeout: 20_000 })

    const after = await readConfiguration(request)
    const created = after.view.registered.providers.find(p => !existingKeys.has(p.key))
    expect(created).toBeDefined()
    createdKey = created!.key
    createdCredentialKey = created!.credentialKey ?? null
    expect(created!.credential).toBe(2)
    expect(created!.credentialConfigured).toBe(true)
    expect(created!.baseUrl.value).toBe('https://example.com/e2e-atomic/v1')
    expect(created!.modelName.value).toBe('synthetic-e2e-atomic-model')
    expect(JSON.stringify(after)).not.toContain('sk-synthetic-e2e-atomic-key')
  } finally {
    if (createdKey) {
      const latest = await readConfiguration(request)
      if (latest.view.registered.providers.some(p => p.key === createdKey)) {
        const res = await request.post('/zba/v1/configuration/save', {
          data: {
            contractRevision: 'cfg-0.3.3',
            expectedVersion: latest.version,
            key: operationKey(latest.generation),
            changes: {
              registered: {
                plugins: [],
                policies: [],
                providers: [{
                  key: createdKey,
                  operation: 2,
                  changes: {
                    nameRaw: { kind: 0 },
                    priority: { kind: 0 },
                    baseUrl: { kind: 0 },
                    modelName: { kind: 0 },
                    enabled: { kind: 0 },
                    credential: { kind: 0 }
                  }
                }]
              }
            },
            secrets: createdCredentialKey ? [{ kind: 2, credentialKey: createdCredentialKey }] : []
          },
          headers: CONFIG_HEADERS
        })
        expect(res.status()).toBe(200)
      }
    }
  }
})

test('能力设置：白名单通过面板保存并回读（真实宿主）', async ({ page, request }) => {
  test.setTimeout(60_000)
  const before = await readConfiguration(request)
  const original = before.view.platform.whitelistGroups.value
  const updated = '20002, 20003'

  await openConfiguration(page)
  const dialog = await openCapability(page, 'qq.private', 'platform')
  // 确认前端按驱动解耦：QQ 私域抽屉不混入飞书或 QQ 官方字段与飞书密钥
  await expect(dialog.getByTestId('config-field-feishuEnabled')).toHaveCount(0)
  await expect(dialog.getByTestId('config-field-officialEnabled')).toHaveCount(0)
  await expect(dialog.getByTestId('config-secrets')).toHaveCount(0)
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
