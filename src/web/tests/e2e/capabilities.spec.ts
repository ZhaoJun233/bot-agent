import { expect, test } from '@playwright/test'

const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'

async function connect(page: import('@playwright/test').Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()
}

test('能力页：真实宿主读取目录与支持矩阵', async ({ page }) => {
  await connect(page, READ)
  await expect(page.getByTestId('cap-count')).toContainText('已加载')
  const cards = page.locator('.cap-card')
  await expect(cards.first()).toBeVisible()
  const count = await cards.count()
  expect(count).toBeGreaterThan(3)
  // 标准模式只读：动作按钮存在但禁用
  const action = page.locator('[data-testid^="cap-action-"]').first()
  if (await action.count()) {
    await expect(action).toBeDisabled()
  }
  await expect(page.getByTestId('cap-readonly')).toBeVisible()
})

test('能力页：管理员可执行动作并看到真实回执', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  // 找一个真实可用的动作按钮
  const enabled = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  await expect(enabled).toBeVisible()
  await enabled.click()
  await expect(page.getByTestId('cap-confirm')).toBeVisible()
  await page.getByTestId('cap-confirm-ok').click()
  await expect(page.getByTestId('cap-result')).toBeVisible()
  await expect(page.getByTestId('cap-result-code')).toHaveText('0')
  const state = page.getByTestId('cap-result-state')
  await expect(state).toBeVisible()
})

test('能力页：取消二次确认不会发出写请求', async ({ page }) => {
  const writes: string[] = []
  page.on('request', (request) => {
    if (request.method() === 'POST' && new URL(request.url()).pathname.includes('/capabilities/execute')) {
      writes.push(request.url())
    }
  })
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')
  const enabled = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  await enabled.click()
  await page.getByTestId('cap-confirm-cancel').click()
  await expect(page.getByTestId('cap-confirm')).toHaveCount(0)
  expect(writes).toHaveLength(0)
})

test('能力页：从配置页带草稿切到能力页会先询问', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('token-input').fill(ADMIN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')

  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))

  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()
  await page.getByTestId('config-leave-stay').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('capabilities')).toHaveCount(0)

  await page.getByTestId('config-discard').click()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()
})

test('能力页：真实宿主接受自己签发的游标，第二页能加载', async ({ page }) => {
  await connect(page, READ)
  await expect(page.getByTestId('cap-count')).toContainText('已加载 12 项')

  // 只改请求参数，不伪造响应：游标仍然由真实宿主签发、真实宿主接收。
  await page.route('**/zba/v1/capabilities?*', async (route) => {
    const url = new URL(route.request().url())
    url.searchParams.set('limit', '1')
    await route.continue({ url: url.toString() })
  })

  await page.getByTestId('cap-reload').click()
  await expect(page.getByTestId('cap-count')).toContainText('已加载 1 项')
  await expect(page.getByTestId('cap-load-more')).toBeVisible()

  // 第二页：游标经过 URL 编码回传，宿主必须认它。
  await page.getByTestId('cap-load-more').click()
  await expect(page.getByTestId('cap-count')).toContainText('已加载 2 项')
  await expect(page.getByTestId('cap-page-error')).toHaveCount(0)
})

// ── S23-R2 真实宿主恢复路径 ──────────────────────────────────────────────────

test('能力页：真实宿主已受理但响应丢失后，核对入口可用且重试复用同一操作键', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  const attempts: Array<{ key: string; status: number; code: number; replayed: boolean }> = []
  // 前两次故意让浏览器断开（服务端已经真实受理）；第三次放行，验证重放回执。
  await page.route('**/zba/v1/capabilities/execute', async (route) => {
    const key = (route.request().postDataJSON() as { key: string }).key
    const response = await route.fetch()
    const body = (await response.json()) as { code: number; replayed: boolean }
    attempts.push({ key, status: response.status(), code: body.code, replayed: body.replayed })
    if (attempts.length <= 2) {
      await route.abort('connectionreset')
      return
    }
    await route.fulfill({ response })
  })

  const enabled = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  await enabled.click()
  await page.getByTestId('cap-confirm-ok').click()

  // 确认框必须退场，否则核对按钮会被遮罩挡住。
  await expect(page.getByTestId('cap-confirm')).toHaveCount(0)
  await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
  await expect(page.getByTestId('cap-result-query')).toBeVisible()
  await expect.poll(() => attempts.length).toBe(1)
  const firstKey = (await page.getByTestId('cap-op-key').textContent())?.trim()
  expect(firstKey).toBe(attempts[0]?.key)
  expect(attempts[0]?.code).toBe(0)

  // 重试不是第二次执行：同一个键，服务端按幂等回放。
  await page.getByTestId('cap-result-retry').click()
  await expect.poll(() => attempts.length).toBe(2)
  expect(attempts[1]?.key).toBe(attempts[0]?.key)

  // 第三次放行：拿到的是同一次操作的回执，不是新动作。
  await page.getByTestId('cap-result-retry').click()
  await expect.poll(() => attempts.length).toBe(3)
  expect(attempts[2]?.key).toBe(attempts[0]?.key)
  expect(attempts[2]?.replayed).toBe(true)
  await expect(page.getByTestId('cap-result-state')).toBeVisible({ timeout: 15_000 })
})

test('能力页：切走再回来仍能看到待核对操作键', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')
  await page.route('**/zba/v1/capabilities/execute', async (route) => {
    await route.abort('connectionreset')
  })
  const enabled = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  await enabled.click()
  await page.getByTestId('cap-confirm-ok').click()
  await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
  const key = (await page.getByTestId('cap-op-key').textContent())?.trim()

  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()

  await expect(page.getByTestId('cap-op-key')).toHaveText(key ?? '')
  await expect(page.getByTestId('cap-result-query')).toBeVisible()
})

// ── S23-R2-F1 / S23-OWN-F1 ──────────────────────────────────────────────────

test('能力页：离开视图再回来，在途确认不得补发', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  let release!: () => void
  const gate = new Promise<void>((resolve) => { release = resolve })
  let entered = false
  let sent = 0
  await page.route('**/zba/v1/capabilities/inspect', async (route) => {
    const response = await route.fetch()
    entered = true
    await gate
    await route.fulfill({ response })
  })
  await page.route('**/zba/v1/capabilities/execute', async (route) => {
    sent += 1
    await route.abort('connectionreset')
  })

  await page.locator('[data-testid^="cap-action-"]:not([disabled])').first().click()
  await page.getByTestId('cap-confirm-ok').click()
  await expect.poll(() => entered).toBe(true)

  // 切走再切回来：active 又变回 true，但这次确认必须已经永久失效。
  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  release()
  await page.waitForTimeout(2000)
  expect(sent, '离开视图后返回时不得补发写请求').toBe(0)
})

test('能力页：未知操作从卡片重开，预检失败后仍用同一个键重发', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  const action = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  const actionId = (await action.getAttribute('data-testid')) ?? ''
  const keys: string[] = []
  await page.route('**/zba/v1/capabilities/execute', async (route) => {
    keys.push((route.request().postDataJSON() as { key: string }).key)
    await route.abort('connectionreset')
  })

  await action.click()
  await page.getByTestId('cap-confirm-ok').click()
  await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
  const firstKey = (await page.getByTestId('cap-op-key').textContent())?.trim()

  // 同一张卡片重开，这次让提交前核对失败：不能因此否定上一次提交。
  await page.route('**/zba/v1/capabilities/inspect', (route) => route.abort('connectionreset'))
  await page.locator('[data-testid="' + actionId + '"]').click()
  await page.getByTestId('cap-confirm-ok').click()
  await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
  await page.unroute('**/zba/v1/capabilities/inspect')
  expect(keys).toHaveLength(1)

  // 未终态记录关不掉：面板与操作键都留着。
  await page.getByTestId('cap-result-close').click()
  await expect(page.getByTestId('cap-result')).toBeVisible()
  await expect(page.getByTestId('cap-op-key')).toHaveText(firstKey ?? '')

  // 再确认：真实宿主按操作键去重，发出去的必须是同一个键。
  await page.locator('[data-testid="' + actionId + '"]').click()
  await page.getByTestId('cap-confirm-ok').click()
  await expect.poll(() => keys.length).toBe(2)
  expect(keys[1]).toBe(keys[0])
  expect(keys[0]).toBe(firstKey)
})

test('能力页：核对没有回执时未知操作仍保留操作键', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  await page.route('**/zba/v1/capabilities/execute', (route) => route.abort('connectionreset'))
  await page.locator('[data-testid^="cap-action-"]:not([disabled])').first().click()
  await page.getByTestId('cap-confirm-ok').click()
  await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
  const key = (await page.getByTestId('cap-op-key').textContent())?.trim()

  // 服务端回业务错误但没有任何回执：不是「操作结束了」。
  await page.route('**/zba/v1/capabilities/query', (route) => route.fulfill({
    status: 200,
    contentType: 'application/json',
    body: JSON.stringify({ code: 13, replayed: false })
  }))
  await page.getByTestId('cap-result-query').click()
  await expect(page.getByTestId('cap-result-unknown')).toBeVisible()

  await page.getByTestId('cap-result-close').click()
  await expect(page.getByTestId('cap-result')).toBeVisible()
  await expect(page.getByTestId('cap-op-key')).toHaveText(key ?? '')
})

// ── S4-R1：在途动作不得锁死新确认意图的退出路径 ─────────────────────────────

/**
 * 上一个动作还在提交时，用户仍可以打开**另一个**动作的确认框。
 *
 * 这里要求的是「新意图自己可取消」而不是「禁止打开新框」：在途窗口可能很长
 * （服务端慢、网络卡），把入口一关了事会让用户在这段时间里什么都不能做。
 * 所以不变量是：
 *   ① 新框里必须有**可用**的焦点（禁用按钮不算落点）；
 *   ② Escape 必须真的关掉它，且不需要等上一个请求结束；
 *   ③ 取消新意图不能发出第二个写请求。
 */
test('能力页：上一个动作在途时打开的新确认框仍可取消，且不发第二个写请求', async ({ page }) => {
  await connect(page, ADMIN)
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  const actions = page.locator('[data-testid^="cap-action-"]:not([disabled])')
  expect(await actions.count()).toBeGreaterThan(1)

  // 扣住第一次预检：这就是"在途窗口"，不能用快响应把它掩盖掉。
  let release!: () => void
  const held = new Promise<void>((resolve) => { release = resolve })
  let entered!: () => void
  const seen = new Promise<void>((resolve) => { entered = resolve })
  await page.route('**/zba/v1/capabilities/inspect', async (route) => {
    entered()
    await held
    await route.fulfill({
      status: 503,
      contentType: 'application/json',
      body: JSON.stringify({ error: { code: 'service_unavailable', message: 'synthetic delayed preflight' } })
    })
  })

  const writes: string[] = []
  page.on('request', (request) => {
    if (request.method() === 'POST' && request.url().includes('/capabilities/execute')) writes.push(request.url())
  })

  await actions.first().click()
  await page.getByTestId('cap-confirm-ok').click()
  await seen
  await expect(page.getByTestId('cap-confirm')).toHaveCount(0)

  // 第二个意图：键盘 Enter 打开（不点鼠标，顺带覆盖键盘路径）。
  await actions.nth(1).focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('cap-confirm')).toBeVisible()

  // ① 焦点必须在框内，且不是被禁用的按钮。
  const focus = await page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null
    const inDialog = !!el?.closest('[role="dialog"]')
    const disabled = el instanceof HTMLButtonElement ? el.disabled : false
    return { inDialog, disabled, enabledInDialog: document.querySelectorAll('[data-testid="cap-confirm"] button:not([disabled])').length }
  })
  expect(focus.inDialog, '在途时新框里必须有可用焦点').toBe(true)
  expect(focus.disabled, '焦点不能落在被禁用的按钮上（focus() 对禁用元素是空操作）').toBe(false)
  expect(focus.enabledInDialog, '新框至少要有一个可用按钮').toBeGreaterThan(0)

  // ② 不需要释放上一个请求就能退出。
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('cap-confirm')).toHaveCount(0)

  // ③ 取消不等于提交。
  expect(writes, '取消新意图不得发出写请求').toHaveLength(0)

  release()
  await expect(page.getByTestId('cap-result')).toBeVisible({ timeout: 15_000 })
})

// ── S23-R2-F2：未知历史不被「无回执拒绝」抹掉 ───────────────────────────────

for (const mode of ['retry', 'card'] as const) {
  test(`能力页：未知操作同键重发被拒后仍保留旧键（${mode}）`, async ({ page }) => {
    await connect(page, ADMIN)
    await page.getByTestId('cap-enable-admin').click()
    await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

    const action = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
    const actionId = (await action.getAttribute('data-testid')) ?? ''
    const keys: string[] = []
    let firstReceipt = false
    await page.route('**/zba/v1/capabilities/execute', async (route) => {
      keys.push((route.request().postDataJSON() as { key: string }).key)
      if (keys.length === 1) {
        // 真实宿主受理，然后丢掉响应。
        const response = await route.fetch()
        const body = (await response.json()) as { code: number; receipt?: unknown }
        firstReceipt = body.code === 0 && Boolean(body.receipt)
        await route.abort('connectionreset')
        return
      }
      if (keys.length === 2) {
        // 同键重发：服务端重新授权后拒绝，且没有回执。
        await route.fulfill({
          status: 403,
          contentType: 'application/json',
          body: JSON.stringify({ code: 1, replayed: false })
        })
        return
      }
      await route.abort('connectionreset')
    })

    await action.click()
    await page.getByTestId('cap-confirm-ok').click()
    await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
    expect(firstReceipt).toBe(true)
    const key = (await page.getByTestId('cap-op-key').textContent())?.trim()

    if (mode === 'card') await page.locator('[data-testid="' + actionId + '"]').click()
    await page.getByTestId(mode === 'card' ? 'cap-confirm-ok' : 'cap-result-retry').click()
    await expect.poll(() => keys.length).toBe(2)
    await expect(page.getByTestId('cap-result-code')).toHaveText('1')

    // 拒绝只说明本次请求没生效，不说明最初那次没执行：键与面板都留着。
    await expect(page.getByTestId('cap-result-unknown')).toBeVisible()
    await page.getByTestId('cap-result-close').click()
    await expect(page.getByTestId('cap-result')).toBeVisible()
    await expect(page.getByTestId('cap-op-key')).toHaveText(key ?? '')

    // 再确认仍然是同一个键。
    if (mode === 'card') await page.locator('[data-testid="' + actionId + '"]').click()
    await page.getByTestId(mode === 'card' ? 'cap-confirm-ok' : 'cap-result-retry').click()
    await expect.poll(() => keys.length).toBe(3)
    expect(keys[1]).toBe(keys[0])
    expect(keys[2]).toBe(keys[0])
  })
}
