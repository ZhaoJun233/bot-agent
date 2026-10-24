import { expect, test } from '@playwright/test'

const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'

async function openTasks(page: import('@playwright/test').Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()
  await expect(page.getByTestId('tasks')).toBeVisible()
}

test('任务页：真实宿主可读取任务与待审批', async ({ page }) => {
  await openTasks(page, READ)
  await expect(page.getByTestId('task-mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('task-readonly')).toBeVisible()
  await expect(page.getByTestId('task-approvals')).toBeVisible()
  // 只读令牌不能提交
  await expect(page.getByTestId('task-submit')).toBeDisabled()
})

test('任务页：管理员提交任务后进入真实状态', async ({ page }) => {
  await openTasks(page, ADMIN)
  await page.getByTestId('task-enable-admin').click()
  await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')

  await page.getByTestId('task-source-key').fill('panel:synthetic-e2e')
  await page.getByTestId('task-session-id').fill('synthetic-session')
  await page.getByTestId('task-prompt').fill('合成任务：请回复 ok')
  await page.getByTestId('task-submit').click()

  await expect(page.getByTestId('task-submission')).toBeVisible()
  const key = (await page.getByTestId('task-idempotency-key').textContent())?.trim()
  expect(key?.startsWith('task1-')).toBe(true)
  await expect(page.getByTestId('task-submission-state')).toContainText('已受理')
})

test('任务页：提交丢响应时保留幂等键，核对不换键', async ({ page }) => {
  await openTasks(page, ADMIN)
  await page.getByTestId('task-enable-admin').click()
  await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')

  const submitKeys: string[] = []
  const findKeys: string[] = []
  await page.route('**/zba/v1/tasks', async (route) => {
    if (route.request().method() !== 'POST') { await route.continue(); return }
    submitKeys.push(route.request().headers()['idempotency-key'] ?? '')
    await route.abort('connectionreset')
  })
  await page.route('**/zba/v1/tasks/find', async (route) => {
    findKeys.push((route.request().postDataJSON() as { key: string }).key)
    await route.continue()
  })

  await page.getByTestId('task-source-key').fill('panel:synthetic-e2e')
  await page.getByTestId('task-session-id').fill('synthetic-session')
  await page.getByTestId('task-prompt').fill('合成任务：丢响应')
  await page.getByTestId('task-submit').click()

  await expect(page.getByTestId('task-submission-state')).toContainText('结果未知')
  const shown = (await page.getByTestId('task-idempotency-key').textContent())?.trim()
  expect(shown).toBe(submitKeys[0])

  await page.getByTestId('task-find').click()
  await expect(page.getByTestId('task-find-code')).toBeVisible()
  // 核对用的就是提交时那个键，没有换键重提。
  expect(findKeys).toEqual([submitKeys[0]])
  expect(submitKeys).toHaveLength(1)
})

test('任务页：事件恢复真的订阅 events，resyncRequired 触发整份补读', async ({ page }) => {
  const cursors: string[] = []
  const listReads: string[] = []
  let firstEventsCall = true
  await page.route('**/zba/v1/events*', async (route) => {
    const after = new URL(route.request().url()).searchParams.get('after') ?? 'missing'
    cursors.push(after)
    if (firstEventsCall) {
      firstEventsCall = false
      // 服务端要求重读：给出锚点 7
      await route.fulfill({ json: { items: [], nextSequence: 7, resyncRequired: true, generatedAt: new Date().toISOString() } })
      return
    }
    const anchor = /^d+$/.test(after) ? Number(after) : 7
    await route.fulfill({ json: { items: [], nextSequence: anchor, resyncRequired: false, generatedAt: new Date().toISOString() } })
  })
  await page.route('**/zba/v1/tasks?*', async (route) => {
    listReads.push(new URL(route.request().url()).search)
    await route.continue()
  })

  await openTasks(page, ADMIN)
  // 首次 tick 在一个轮询间隔之后（挂载时已经整份读过一次，不该立刻再取一遍）
  await expect.poll(() => cursors.length, { timeout: 20_000 }).toBeGreaterThan(0)
  // resyncRequired 之后必须整份重读任务队列，而不是只认事件
  await expect.poll(() => listReads.length, { timeout: 20_000 }).toBeGreaterThanOrEqual(2)
  await expect(page.getByTestId('task-sync-state')).toHaveText('已追平')
})

test('任务页：事件同步失败时显示旧数据，不冒报已追平', async ({ page }) => {
  await page.route('**/zba/v1/events*', async (route) => {
    await route.fulfill({ status: 500, contentType: 'application/json', body: '{"error":{"code":"http_error","message":"synthetic"}}' })
  })
  await openTasks(page, ADMIN)
  // 队列仍显示（旧数据可用），但同步状态必须老实说没追平
  await expect(page.getByTestId('task-approvals')).toBeVisible()
  await expect(page.getByTestId('task-sync-state')).toHaveText('同步未完成', { timeout: 20_000 })
  await expect(page.getByTestId('task-sync-stale')).toBeVisible()
})

test('任务页：结果未知时再点提交不会换键重提', async ({ page }) => {
  const keys: string[] = []
  await page.route('**/zba/v1/tasks', async (route) => {
    if (route.request().method() !== 'POST') { await route.continue(); return }
    keys.push(route.request().headers()['idempotency-key'] ?? '')
    await route.abort('connectionreset')
  })
  await openTasks(page, ADMIN)
  await page.getByTestId('task-enable-admin').click()
  await page.getByTestId('task-source-key').fill('panel:synthetic-e2e')
  await page.getByTestId('task-session-id').fill('synthetic-session')
  await page.getByTestId('task-prompt').fill('合成任务：不得换键重提')
  await page.getByTestId('task-submit').click()
  await expect(page.getByTestId('task-submission-state')).toContainText('结果未知')

  // 主提交入口被禁用；再点一次也不产生第二次受理
  await expect(page.getByTestId('task-submit')).toBeDisabled()
  await page.getByTestId('task-submit').click({ force: true }).catch(() => undefined)
  await page.waitForTimeout(500)
  expect(keys, '未决意图不能被换成第二个幂等键').toHaveLength(1)

  // 重新执行必须是独立动作 + 明确风险提示
  await page.getByTestId('task-restart-0').click()
  await expect(page.getByTestId('task-restart-warning-0')).toContainText('再执行一次')
})

test('任务页：锁定并重新认证后仍能只读核对上一次未决提交', async ({ page }) => {
  const findKeys: string[] = []
  await page.route('**/zba/v1/tasks', async (route) => {
    if (route.request().method() !== 'POST') { await route.continue(); return }
    await route.abort('connectionreset')
  })
  await page.route('**/zba/v1/tasks/find', async (route) => {
    findKeys.push((route.request().postDataJSON() as { key: string }).key)
    await route.continue()
  })
  await openTasks(page, ADMIN)
  await page.getByTestId('task-enable-admin').click()
  await page.getByTestId('task-source-key').fill('panel:synthetic-e2e')
  await page.getByTestId('task-session-id').fill('synthetic-session')
  await page.getByTestId('task-prompt').fill('合成任务：锁定后核对')
  await page.getByTestId('task-submit').click()
  await expect(page.getByTestId('task-submission-state')).toContainText('结果未知')
  const key = (await page.getByTestId('task-idempotency-key').textContent())?.trim()
  expect(key).toBeTruthy()

  await page.getByTestId('lock').click()
  await expect(page.getByTestId('token-input')).toBeVisible()
  await page.getByTestId('token-input').fill(ADMIN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()

  // 只恢复最小关联信息：幂等键在，任务内容不在
  await expect(page.getByTestId('task-idempotency-key')).toHaveText(key!)
  await expect(page.getByTestId('task-submission-restored')).toBeVisible()
  await expect(page.getByTestId('task-prompt')).toHaveValue('')

  // 显式发起只读核对，用的还是同一个键
  await page.getByTestId('task-enable-admin').click()
  await page.getByTestId('task-find').click()
  await expect(page.getByTestId('task-find-code')).toBeVisible()
  expect(findKeys).toEqual([key])
})

test('任务页：离开视图再回来，在途提交不会补发', async ({ page }) => {
  await openTasks(page, ADMIN)
  await page.getByTestId('task-enable-admin').click()
  await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')

  // 挂起会话确保请求：确认动作已经发出，但还没走到提交。
  let release!: () => void
  const gate = new Promise<void>((resolve) => { release = resolve })
  let entered = false
  let sent = 0
  await page.route('**/zba/v1/sessions/ensure', async (route) => {
    const response = await route.fetch()
    entered = true
    await gate
    await route.fulfill({ response })
  })
  await page.route('**/zba/v1/tasks', async (route) => {
    if (route.request().method() !== 'POST') { await route.continue(); return }
    sent += 1
    await route.abort('connectionreset')
  })

  await page.getByTestId('task-source-key').fill('panel:synthetic-e2e')
  await page.getByTestId('task-session-id').fill('synthetic-session')
  await page.getByTestId('task-prompt').fill('合成任务：离页')
  await page.getByTestId('task-submit').click()
  await expect.poll(() => entered).toBe(true)

  // 切走再切回来：active 又变回 true，但这次提交必须已经永久失效。
  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()
  await expect(page.getByTestId('tasks')).toBeVisible()

  release()
  await page.waitForTimeout(2000)
  expect(sent, '离开视图后返回时不得补发任务提交').toBe(0)
})
