import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

const READ_TOKEN = 'synthetic-web-read-token'
const ADMIN_TOKEN = 'synthetic-web-admin-token'
const CONFIG_HEADERS = { Authorization: `Bearer ${ADMIN_TOKEN}` }

async function connect(page: Page, token: string): Promise<void> {
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
}

async function openConfiguration(page: Page): Promise<void> {
  await page.goto('/')
  await connect(page, ADMIN_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
}

async function readConfiguration(request: APIRequestContext): Promise<{
  version: string
  generation: string
  view: { behavior: Record<string, { value?: unknown }> }
}> {
  const response = await request.post('/zba/v1/configuration/read', {
    data: { kind: 1 },
    headers: CONFIG_HEADERS
  })
  expect(response.ok()).toBeTruthy()
  return response.json()
}

function operationKey(generation: string): string {
  return `cfg1:${generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`
}

async function externalSave(
  request: APIRequestContext,
  generation: string,
  version: string,
  changes: unknown
): Promise<void> {
  const response = await request.post('/zba/v1/configuration/save', {
    data: {
      contractRevision: 'cfg-0.3.3',
      expectedVersion: version,
      key: operationKey(generation),
      changes,
      secrets: []
    },
    headers: CONFIG_HEADERS
  })
  expect(response.status()).toBe(200)
}

test('配置页：只读令牌可查看 8 分区与秘密元数据，且不发写请求', async ({ page }) => {
  const writes: string[] = []
  page.on('request', (request) => {
    const path = new URL(request.url()).pathname
    if (request.method() === 'POST' && path.startsWith('/zba/v1/') && path !== '/zba/v1/configuration/read') {
      writes.push(`${request.method()} ${path}`)
    }
  })

  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await expect(page.getByTestId('config-field-botPersona')).toBeVisible()
  await expect(page.getByTestId('config-secrets').locator('li')).toHaveCount(6)

  await page.getByTestId('config-nav-model').click()
  await expect(page.getByTestId('config-field-model')).toBeVisible()

  await page.getByTestId('config-nav-registered').click()
  await expect(page.getByTestId('config-plugins')).toBeVisible()
  await expect(page.getByTestId('config-providers')).toBeVisible()

  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()

  expect(writes).toEqual([])
})

test('配置页：管理员令牌进入管理员模式后隐藏只读提示', async ({ page }) => {
  await page.goto('/')
  await connect(page, ADMIN_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-readonly')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()

  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
  await expect(page.getByTestId('config-readonly')).toHaveCount(0)
})

test('配置页：管理员保存一个数字字段并回读，随后恢复', async ({ page, request }) => {
  await openConfiguration(page)
  const before = await readConfiguration(request)

  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-receipt')).toBeVisible({ timeout: 15_000 })
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')

  const after = await readConfiguration(request)
  expect(after.version).not.toBe(before.version)
  expect(after.view.behavior.moodTtlSeconds?.value).toBe(original + 1)

  // 恢复原值，避免影响后续用例
  await page.getByTestId('config-field-moodTtlSeconds').locator('input').fill(String(original))
  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
  const restored = await readConfiguration(request)
  expect(restored.view.behavior.moodTtlSeconds?.value).toBe(original)
})

test('配置页：外部修改造成 409 冲突，草稿保留并可读取最新比较', async ({ page, request }) => {
  await openConfiguration(page)
  const before = await readConfiguration(request)
  const original = Number(before.view.behavior.moodTtlSeconds?.value)
  await externalSave(request, before.generation, before.version, {
    behavior: { moodTtlSeconds: { kind: 1, value: original + 1 } }
  })

  const saveRequests: string[] = []
  page.on('request', (req) => {
    if (req.url().includes('/zba/v1/configuration/save')) saveRequests.push(req.url())
  })

  await page.getByTestId('config-nav-behavior').click()
  await page.getByTestId('config-field-moodTtlSeconds').locator('input').fill(String(original + 2))
  await page.getByTestId('config-save').click()

  await expect(page.getByTestId('config-conflict')).toBeVisible({ timeout: 15_000 })
  expect(saveRequests).toHaveLength(1)
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('config-reload-conflict').click()
  await expect(page.getByTestId('config-validation')).toContainText('已读取最新快照', { timeout: 15_000 })
  await expect(page.getByTestId('config-field-moodTtlSeconds')).toContainText('与对方修改重叠')
  await page.getByTestId('config-discard').click()

  const latest = await readConfiguration(request)
  await externalSave(request, latest.generation, latest.version, {
    behavior: { moodTtlSeconds: { kind: 1, value: original } }
  })
})

test('配置页：保存响应丢失后先核对回执，不自动重发', async ({ page, request }) => {
  test.setTimeout(60_000)
  await openConfiguration(page)
  const before = await readConfiguration(request)
  const original = Number(before.view.behavior.moodTtlSeconds?.value)
  const next = original + 5

  await page.getByTestId('config-nav-behavior').click()
  await page.getByTestId('config-field-moodTtlSeconds').locator('input').fill(String(next))

  const saveRequests: string[] = []
  page.on('request', (req) => {
    if (req.url().includes('/zba/v1/configuration/save')) saveRequests.push(req.url())
  })
  // 让请求真正到达服务端，但永不返回响应体。
  await page.route('**/zba/v1/configuration/save', async (route) => {
    await route.fetch()
  })

  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-unconfirmed')).toBeVisible({ timeout: 20_000 })
  expect(saveRequests).toHaveLength(1)

  await page.unroute('**/zba/v1/configuration/save')
  await page.getByTestId('config-check-pending').click()
  await expect(page.getByTestId('config-receipt')).toBeVisible({ timeout: 15_000 })
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
  expect(saveRequests).toHaveLength(1)

  const saved = await readConfiguration(request)
  expect(saved.view.behavior.moodTtlSeconds?.value).toBe(next)

  await page.getByTestId('config-field-moodTtlSeconds').locator('input').fill(String(original))
  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
})

test('配置页：切到概览再回来，草稿与输入值保留', async ({ page }) => {
  await openConfiguration(page)
  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('nav-config-draft')).toHaveText('1')

  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-field-moodTtlSeconds').locator('input')).toHaveValue(String(original + 1))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('config-discard').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
  await expect(page.getByTestId('nav-config-draft')).toHaveCount(0)
})

test('配置页：离开配置页会清空秘密替换输入，但保留普通字段草稿', async ({ page }) => {
  await openConfiguration(page)
  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))

  const secretList = page.getByTestId('config-secrets')
  await secretList.getByTestId('secret-replace').first().click()
  await secretList.locator('input[type=password]').first().fill('sk-synthetic-leave-7c41')
  await expect(page.getByTestId('config-save-bar')).toContainText('2 项修改')

  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('nav-config-draft')).toHaveText('1')

  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-field-moodTtlSeconds').locator('input')).toHaveValue(String(original + 1))
  await expect(page.getByTestId('config-secrets').locator('input[type=password]')).toHaveCount(0)
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('config-discard').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
})

test('配置页：有草稿时刷新会触发浏览器离开确认', async ({ page }) => {
  await openConfiguration(page)

  const preventedClean = await page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    return event.defaultPrevented
  })
  expect(preventedClean).toBe(false)

  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))

  const preventedDirty = await page.evaluate(() => {
    const event = new Event('beforeunload', { cancelable: true })
    window.dispatchEvent(event)
    return event.defaultPrevented
  })
  expect(preventedDirty).toBe(true)

  await page.getByTestId('config-discard').click()
})

test('配置页：锁定前确认，继续编辑保留草稿，明确放弃后清除', async ({ page }) => {
  await openConfiguration(page)
  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 3))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('lock').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()
  await expect(page.getByTestId('config-leave-dialog')).toContainText('1 项修改')
  await page.getByTestId('config-leave-stay').click()
  await expect(page.getByTestId('config-leave-dialog')).toHaveCount(0)
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.getByTestId('lock').click()
  await page.getByTestId('config-leave-discard').click()
  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('auth-notice')).toHaveCount(0)

  await connect(page, ADMIN_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
  await expect(page.getByTestId('nav-config-draft')).toHaveCount(0)
  // 重连后默认是标准模式，字段以只读文本渲染；值必须回到未修改的宿主基线。
  await expect(page.getByTestId('config-field-moodTtlSeconds')).toContainText(String(original))
})

test('配置页：未知结果锁定编辑，NotFound 后换新操作键重存', async ({ page, request }) => {
  test.setTimeout(60_000)
  await openConfiguration(page)
  const before = await readConfiguration(request)
  const original = Number(before.view.behavior.moodTtlSeconds?.value)
  const next = original + 7

  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  await input.fill(String(next))

  const keys: string[] = []
  page.on('request', (req) => {
    if (!req.url().includes('/zba/v1/configuration/save')) return
    const body = req.postDataJSON() as { key?: string }
    if (typeof body.key === 'string') keys.push(body.key)
  })
  // 请求根本到不了服务端：结果是“未知”，服务端没有回执。
  await page.route('**/zba/v1/configuration/save', (route) => route.abort('failed'))
  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-unconfirmed')).toBeVisible({ timeout: 20_000 })
  expect(keys).toHaveLength(1)
  await expect(input).toBeDisabled()
  await expect(page.getByTestId('config-save')).toBeDisabled()
  await expect(page.getByTestId('config-discard')).toBeDisabled()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.unroute('**/zba/v1/configuration/save')
  await page.getByTestId('config-check-pending').click()
  await expect(page.getByTestId('config-validation')).toContainText('没有找到这次提交的回执', { timeout: 15_000 })
  await expect(page.getByTestId('config-unconfirmed')).toHaveCount(0)
  await expect(input).toBeEnabled()
  await expect(input).toHaveValue(String(next))

  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-receipt')).toBeVisible({ timeout: 15_000 })
  expect(keys).toHaveLength(2)
  expect(keys[0]).not.toBe(keys[1])

  const saved = await readConfiguration(request)
  expect(saved.view.behavior.moodTtlSeconds?.value).toBe(next)

  await input.fill(String(original))
  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
})

test('配置页：两个浏览器上下文同时编辑，后保存方收到 409 并保留草稿', async ({ browser, request }) => {
  test.setTimeout(60_000)
  const before = await readConfiguration(request)
  const original = Number(before.view.behavior.moodTtlSeconds?.value)
  const contextA = await browser.newContext()
  const contextB = await browser.newContext()
  const pageA = await contextA.newPage()
  const pageB = await contextB.newPage()
  try {
    await openConfiguration(pageA)
    await openConfiguration(pageB)
    await pageA.getByTestId('config-nav-behavior').click()
    await pageB.getByTestId('config-nav-behavior').click()
    const inputA = pageA.getByTestId('config-field-moodTtlSeconds').locator('input')
    const inputB = pageB.getByTestId('config-field-moodTtlSeconds').locator('input')
    await inputA.fill(String(original + 1))
    await inputB.fill(String(original + 2))

    await pageA.getByTestId('config-save').click()
    await expect(pageA.getByTestId('config-receipt')).toBeVisible({ timeout: 15_000 })

    await pageB.getByTestId('config-save').click()
    await expect(pageB.getByTestId('config-conflict')).toBeVisible({ timeout: 15_000 })
    await expect(pageB.getByTestId('config-save-bar')).toContainText('1 项修改')
    await expect(inputB).toHaveValue(String(original + 2))

    await inputA.fill(String(original))
    await pageA.getByTestId('config-save').click()
    await expect(pageA.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
    const restored = await readConfiguration(request)
    expect(restored.view.behavior.moodTtlSeconds?.value).toBe(original)
  } finally {
    await contextA.close()
    await contextB.close()
    // 即使断言失败也把共享宿主恢复原值，避免污染后续用例。
    const latest = await readConfiguration(request)
    if (Number(latest.view.behavior.moodTtlSeconds?.value) !== original) {
      await externalSave(request, latest.generation, latest.version, {
        behavior: { moodTtlSeconds: { kind: 1, value: original } }
      })
    }
  }
})

test('配置页：读取 401 会退回认证页并清掉秘密输入', async ({ page }) => {
  await openConfiguration(page)
  const secrets = page.getByTestId('config-secrets')
  await secrets.getByTestId('secret-replace').first().click()
  await secrets.locator('input[type=password]').first().fill('sk-synthetic-401-7c41')
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  await page.route('**/zba/v1/configuration/read', (route) => route.fulfill({
    status: 401,
    contentType: 'application/json',
    json: { error: { code: 'unauthorized', message: 'synthetic expired' } }
  }))
  await page.getByTestId('config-reload').click()

  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('error-notice')).toContainText('unauthorized')
  await expect(page.getByTestId('configuration')).toHaveCount(0)
})

test('配置页：服务端回报 UnknownCommit 时冻结编辑并保留操作键供核对', async ({ page }) => {
  await openConfiguration(page)
  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))

  let submittedKey = ''
  await page.route('**/zba/v1/configuration/save', async (route) => {
    const body = route.request().postDataJSON() as { key: string; expectedVersion: string }
    submittedKey = body.key
    await route.fulfill({
      status: 503,
      contentType: 'application/json',
      json: {
        code: 12,
        replayed: false,
        currentVersion: body.expectedVersion,
        errors: [],
        receipt: {
          key: body.key,
          contractRevision: 'cfg-0.3.3',
          decision: 0,
          observedVersion: body.expectedVersion,
          commitState: 2,
          applyState: 5,
          steps: [],
          deviceKeyAssignments: []
        }
      }
    })
  })

  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-unconfirmed')).toBeVisible()
  await expect(page.getByTestId('config-check-pending')).toBeVisible()
  await expect(input).toBeDisabled()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  // 核对走真实宿主：合成操作键不存在，因此 NotFound 会让草稿重新可编辑。
  await page.getByTestId('config-check-pending').click()
  await expect(page.getByTestId('config-validation')).toContainText('没有找到这次提交的回执')
  await expect(input).toBeEnabled()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')
  expect(submittedKey).not.toBe('')
})

test('配置页：成功码缺少回执时不清草稿也不显示已保存', async ({ page }) => {
  await openConfiguration(page)
  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))

  await page.route('**/zba/v1/configuration/save', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    json: { code: 0, replayed: false, currentVersion: '1', errors: [] }
  }))
  await page.getByTestId('config-save').click()

  await expect(page.getByTestId('config-unconfirmed')).toBeVisible()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')
  await expect(page.getByTestId('config-save-bar')).not.toContainText('已保存')
  await expect(input).toBeDisabled()
})

test('配置页：核对矛盾响应保留未知态，权威 NotFound 后才恢复编辑', async ({ page }) => {
  await openConfiguration(page)
  await page.getByTestId('config-nav-behavior').click()
  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))

  let submittedKey = ''
  await page.route('**/zba/v1/configuration/save', async (route) => {
    const body = route.request().postDataJSON() as { key: string; expectedVersion: string }
    submittedKey = body.key
    await route.fulfill({
      status: 503,
      contentType: 'application/json',
      json: {
        code: 12,
        replayed: false,
        currentVersion: body.expectedVersion,
        errors: [],
        receipt: {
          key: body.key,
          contractRevision: 'cfg-0.3.3',
          decision: 0,
          observedVersion: body.expectedVersion,
          commitState: 2,
          applyState: 5,
          steps: [],
          deviceKeyAssignments: []
        }
      }
    })
  })

  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-unconfirmed')).toBeVisible()
  expect(submittedKey).not.toBe('')

  // 503 上带 NotFound 业务体不是权威核对结果：必须保留未知态与操作键。
  await page.route('**/zba/v1/configuration/find', (route) => route.fulfill({
    status: 503,
    contentType: 'application/json',
    json: { code: 14 }
  }))
  await page.getByTestId('config-check-pending').click()
  await expect(page.getByTestId('config-validation')).toContainText('核对未完成')
  await expect(page.getByTestId('config-unconfirmed')).toBeVisible()
  await expect(input).toBeDisabled()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  // 撤掉故障注入后，同一操作键的权威 NotFound（HTTP 404）才结束未知态。
  await page.unroute('**/zba/v1/configuration/find')
  await page.getByTestId('config-check-pending').click()
  await expect(page.getByTestId('config-validation')).toContainText('没有找到这次提交的回执')
  await expect(input).toBeEnabled()
})
