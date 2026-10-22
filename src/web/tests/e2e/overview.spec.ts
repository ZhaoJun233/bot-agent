import { expect, test, type Page } from '@playwright/test'

const READ_TOKEN = 'synthetic-web-read-token'
const ADMIN_TOKEN = 'synthetic-web-admin-token'

async function connect(page: Page, token: string): Promise<void> {
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
}

test('真实 Headless：只读令牌可加载概览，令牌只在内存', async ({ page }) => {
  await page.goto('/')

  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByRole('heading', { name: '连接 ZBA' })).toBeVisible()
  await expect(page.getByTestId('not-configured')).toHaveCount(0)

  await connect(page, READ_TOKEN)

  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('connection-state')).toContainText('已连接')
  await expect(page.getByTestId('overview-state')).not.toHaveText('')
  await expect(page.getByTestId('last-success')).not.toHaveText('尚未成功读取')
  await expect(page.getByTestId('components-empty')).toHaveCount(0)

  const persisted = await page.evaluate(() => JSON.stringify({
    local: { ...localStorage },
    session: { ...sessionStorage },
    href: location.href,
    body: document.body.innerText
  }))
  expect(persisted).not.toContain(READ_TOKEN)
})

test('错误令牌保留认证门并显示安全错误码', async ({ page }) => {
  await page.goto('/')
  await connect(page, 'synthetic-wrong-token')

  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('error-notice')).toContainText('令牌无效')
  await expect(page.getByTestId('error-notice')).toContainText('unauthorized')
  await expect(page.getByTestId('overview')).toHaveCount(0)
})

test('真实 401 后可修正令牌并重试，认证门不会卡在连接中', async ({ page }) => {
  await page.goto('/')
  await connect(page, 'synthetic-wrong-token')

  await expect(page.getByTestId('error-notice')).toContainText('unauthorized')
  await expect(page.getByTestId('token-input')).toBeEnabled()
  await expect(page.getByTestId('connect-button')).toBeEnabled()

  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')
})

test('只读令牌不能进入管理员模式，且不产生写请求', async ({ page }) => {
  const zbaRequests: Array<{ method: string; path: string }> = []
  page.on('request', (request) => {
    const url = new URL(request.url())
    if (url.pathname.startsWith('/zba/v1/')) {
      zbaRequests.push({ method: request.method(), path: url.pathname })
    }
  })

  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  await page.getByTestId('enable-admin').click()

  await expect(page.getByTestId('error-notice')).toContainText('当前令牌')
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('enable-admin')).toBeEnabled()

  expect(zbaRequests.length).toBeGreaterThan(0)
  expect(zbaRequests.some((request) => request.path === '/zba/v1/tasks/s1-permission-check/result')).toBe(true)
  expect(zbaRequests.filter((request) => request.method !== 'GET')).toEqual([])
})

test('管理员令牌可显式进入/退出管理员模式', async ({ page }) => {
  await page.goto('/')
  await connect(page, ADMIN_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')

  await page.getByTestId('enable-admin').click()
  await expect(page.getByTestId('mode-badge')).toHaveText('管理员模式')

  await page.getByTestId('disable-admin').click()
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')
})

test('锁定会使进行中的管理员探测失效，重连只读令牌仍为只读', async ({ page }) => {
  await page.goto('/')
  await connect(page, ADMIN_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  let releaseProbe!: () => void
  const probeGate = new Promise<void>((resolve) => { releaseProbe = resolve })
  let probeStarted = false
  await page.route('**/zba/v1/tasks/s1-permission-check/result', async (route) => {
    probeStarted = true
    await probeGate
    await route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: JSON.stringify({ error: { code: 'task_not_found', message: '任务不存在。' } })
    })
  })

  const pendingClick = page.getByTestId('enable-admin').click()
  await expect.poll(() => probeStarted).toBe(true)
  await page.getByTestId('lock').click()
  await expect(page.getByTestId('auth-gate')).toBeVisible()

  releaseProbe()
  await pendingClick
  await page.unroute('**/zba/v1/tasks/s1-permission-check/result')

  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('enable-admin')).toBeEnabled()
})

test('兼容性变化会使在途管理员探测失效，不进入管理员模式', async ({ page }) => {
  await page.goto('/')
  await connect(page, ADMIN_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  let releaseProbe!: () => void
  const probeGate = new Promise<void>((resolve) => { releaseProbe = resolve })
  let probeStarted = false
  await page.route('**/zba/v1/tasks/s1-permission-check/result', async (route) => {
    probeStarted = true
    await probeGate
    await route.fulfill({
      status: 404,
      contentType: 'application/json',
      body: JSON.stringify({ error: { code: 'task_not_found', message: '任务不存在。' } })
    })
  })

  const probeResponse = page.waitForResponse('**/zba/v1/tasks/s1-permission-check/result')
  await page.getByTestId('enable-admin').click()
  await expect.poll(() => probeStarted).toBe(true)

  await page.route('**/zba/v1/meta', async (route) => {
    const original = await route.fetch()
    const body = await original.json() as Record<string, unknown>
    body.contractVersion = 'zba-9.9'
    await route.fulfill({ response: original, json: body })
  })
  await page.getByTestId('refresh').click()
  await expect(page.getByTestId('compatibility-warning')).toContainText('契约不兼容')

  releaseProbe()
  await probeResponse
  await expect(page.getByTestId('mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('enable-admin')).toBeDisabled()
  await expect(page.getByTestId('error-notice')).toContainText('契约已变化')
})

test('锁定清空内存令牌，刷新后仍需重新认证', async ({ page }) => {
  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  await page.getByTestId('lock').click()
  await expect(page.getByTestId('auth-gate')).toBeVisible()

  await page.reload()
  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('overview')).toHaveCount(0)
})

test('刷新失败时保留旧数据并明确标记 stale', async ({ page }) => {
  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()
  const originalState = await page.getByTestId('overview-state').textContent()

  await page.route('**/zba/v1/status', (route) => route.abort())
  await page.getByTestId('refresh').click()
  await expect(page.getByTestId('stale-banner')).toBeVisible()
  await expect(page.getByTestId('overview-state')).toHaveText(originalState ?? '')

  await page.unroute('**/zba/v1/status')
  await page.getByTestId('refresh').click()
  await expect(page.getByTestId('stale-banner')).toHaveCount(0)
})

test('合法 JSON 的无效 status 形状不能覆盖上次有效快照', async ({ page }) => {
  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  const originalState = await page.getByTestId('overview-state').textContent()
  const originalSuccess = await page.getByTestId('last-success').textContent()
  const originalRows = await page.getByTestId('components').locator('tbody tr').count()

  await page.route('**/zba/v1/status', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: '{}'
  }))
  await page.getByTestId('refresh').click()

  await expect(page.getByTestId('stale-banner')).toBeVisible()
  await expect(page.getByTestId('overview-state')).toHaveText(originalState ?? '')
  await expect(page.getByTestId('last-success')).toHaveText(originalSuccess ?? '')
  await expect(page.getByTestId('components').locator('tbody tr')).toHaveCount(originalRows)
})

test('契约版本不兼容时禁用管理员入口并说明原因', async ({ page }) => {
  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  await page.route('**/zba/v1/meta', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({
      contractVersion: 'zba-9.9',
      serverVersion: '0.0.0-synthetic',
      authentication: 'bearer',
      scopes: ['read', 'admin'],
      sse: false,
      mcpManagement: false,
      dockerManagement: false,
      maxBodyBytes: 1024,
      instanceId: 'zi-synthetic-0001',
      features: ['sessions.list', 'sessions.ensure', 'tasks.result', 'tasks.find']
    })
  }))
  await page.getByTestId('refresh').click()

  await expect(page.getByTestId('compatibility-warning')).toContainText('契约不兼容')
  await expect(page.getByTestId('compatibility-warning')).toContainText('zba-9.9')
  await expect(page.getByTestId('enable-admin')).toBeDisabled()
})

test('真实无令牌宿主返回 503 zba_not_configured，页面不伪装成功', async ({ page }) => {
  await page.goto('http://127.0.0.1:4175/')

  await expect(page.getByTestId('not-configured')).toBeVisible()
  await connect(page, 'synthetic-unconfigured-token')

  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('error-notice')).toContainText('未配置 ZBA 访问令牌')
  await expect(page.getByTestId('error-notice')).toContainText('zba_not_configured')
  await expect(page.getByTestId('overview')).toHaveCount(0)
  await expect(page.getByTestId('token-input')).toBeEnabled()
  await expect(page.getByTestId('connect-button')).toBeEnabled()
})

test('503 保持旧数据并显示不可用，而不是伪装成功', async ({ page }) => {
  await page.goto('/')
  await connect(page, READ_TOKEN)
  await expect(page.getByTestId('overview')).toBeVisible()

  await page.route('**/zba/v1/status', (route) => route.fulfill({
    status: 503,
    contentType: 'application/json',
    body: JSON.stringify({ error: { code: 'unavailable', message: 'synthetic unavailable' } })
  }))
  await page.getByTestId('refresh').click()

  await expect(page.getByTestId('error-notice')).toContainText('暂不可用')
  await expect(page.getByTestId('stale-banner')).toBeVisible()
  await expect(page.getByTestId('overview-state')).toBeVisible()
})
