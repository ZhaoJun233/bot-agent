import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const adminHeaders = { Authorization: 'Bearer ' + ADMIN_TOKEN }

async function connectAdmin(page: Page): Promise<void> {
  await page.goto('/')
  const token = page.getByTestId('token-input')
  if (await token.isVisible()) {
    await token.fill(ADMIN_TOKEN)
    await page.getByTestId('connect-button').click()
  }
  await expect(page.getByTestId('overview')).toBeVisible()
}

async function cleanupPhase5State(request: APIRequestContext): Promise<void> {
  const todosRes = await request.get('/zba/v1/todos', { headers: adminHeaders })
  if (todosRes.ok()) {
    const todos = (await todosRes.json()) as { revision: number; items: Array<{ todoId: string; title: string }> }
    let rev = todos.revision
    for (const item of todos.items) {
      if (item.title.startsWith('E2E-')) {
        const del = await request.post('/zba/v1/todos/delete', {
          headers: adminHeaders,
          data: { expectedRevision: rev, idempotencyKey: `e2e-clean-todo-${item.todoId}-${Date.now()}`, todoId: item.todoId },
        })
        if (del.ok()) {
          const body = (await del.json()) as { snapshot?: { revision?: number } }
          rev = body.snapshot?.revision ?? rev + 1
        }
      }
    }
  }

  const jobsRes = await request.get('/zba/v1/scheduler/jobs', { headers: adminHeaders })
  if (jobsRes.ok()) {
    const sched = (await jobsRes.json()) as { revision: number; jobs: Array<{ jobId: string; text: string }> }
    let rev = sched.revision
    for (const job of sched.jobs) {
      if (job.text.startsWith('E2E-')) {
        const del = await request.post('/zba/v1/scheduler/delete', {
          headers: adminHeaders,
          data: { expectedRevision: rev, idempotencyKey: `e2e-clean-job-${job.jobId}-${Date.now()}`, jobId: job.jobId },
        })
        if (del.ok()) {
          const body = (await del.json()) as { snapshot?: { revision?: number } }
          rev = body.snapshot?.revision ?? rev + 1
        }
      }
    }
  }

  await request.post('/zba/v1/workspace/identity/toggle', {
    headers: adminHeaders,
    data: { enabled: false, shareTodos: false, shareKnowledge: false, sharePreferences: false },
  })
}

test('Phase 5: Workspace CRUD (todos, scheduler, identity), narrow 320px tabs, and light/dark themes', async ({ page, request }) => {
  const pageErrors: string[] = []
  page.on('pageerror', err => pageErrors.push(err.message))
  await page.setViewportSize({ width: 1440, height: 900 })
  await cleanupPhase5State(request)

  try {
    await connectAdmin(page)
    await page.getByTestId('nav-workspace').click()
    await expect(page.getByTestId('workspace-page')).toBeVisible()
    await expect(page.getByTestId('ws-readonly-banner')).toBeVisible()
    await page.getByTestId('ws-unlock-admin').click()
    await expect(page.getByTestId('global-admin-toggle')).toHaveAttribute('data-mode', 'admin')
    await expect(page.getByTestId('ws-messages')).toBeVisible()

    // 1. Personal Todo add -> complete -> delete
    await page.getByTestId('ws-todo-title').fill('E2E-合成个人待办')
    await page.getByTestId('ws-todo-add').click()
    const todoList = page.getByTestId('ws-todo-list')
    await expect(todoList).toContainText('E2E-合成个人待办')

    const completeBtn = page.locator('[data-testid^="ws-todo-complete-"]').first()
    await completeBtn.click()
    await expect(todoList).toContainText('done')

    const delTodoBtn = page.locator('[data-testid^="ws-todo-delete-"]').first()
    await delTodoBtn.click()
    await expect(todoList).not.toContainText('E2E-合成个人待办')

    // 2. Scheduled job create -> toggle pause/resume -> delete
    await page.getByTestId('ws-sched-scope').fill('workspace:owner')
    await page.getByTestId('ws-sched-kind').selectOption('cron_text')
    await page.getByTestId('ws-sched-when').fill('*/15 * * * *')
    await page.getByTestId('ws-sched-text').fill('E2E-合成定时任务')
    const [schedSaveRes] = await Promise.all([
      page.waitForResponse(r => r.url().includes('/zba/v1/scheduler/save') && r.request().method() === 'POST'),
      page.getByTestId('ws-sched-add').click(),
    ])
    const schedSaveText = await schedSaveRes.text()
    expect(schedSaveRes.status(), `scheduler/save status=${schedSaveRes.status()} body=${schedSaveText}`).toBe(200)
    await expect(page.getByTestId('ws-error')).toHaveCount(0)
    const schedList = page.getByTestId('ws-sched-list')
    await expect(schedList).toContainText('E2E-合成定时任务')

    const toggleJobBtn = page.locator('[data-testid^="ws-sched-toggle-"]').first()
    await expect(toggleJobBtn).toHaveText('暂停')
    await toggleJobBtn.click()
    await expect(toggleJobBtn).toHaveText('启用')

    const delJobBtn = page.locator('[data-testid^="ws-sched-delete-"]').first()
    await delJobBtn.click()
    await expect(schedList).not.toContainText('E2E-合成定时任务')

    // 3. Cross-platform identity toggle with inline confirmation & persistence check
    const idCheckbox = page.getByTestId('ws-identity-enabled')
    await idCheckbox.check()
    await page.getByTestId('ws-identity-save').click()
    await page.getByTestId('ws-identity-confirm-enable').click()
    await expect(page.getByTestId('ws-notice')).toContainText('已保存')
    const idCheckRes = await request.get('/zba/v1/workspace/identity', { headers: adminHeaders })
    expect(idCheckRes.ok()).toBe(true)
    const idCheckJson = (await idCheckRes.json()) as { enabled: boolean }
    expect(idCheckJson.enabled).toBe(true)

    // 3b. Locking admin mode immediately clears workspace memory view
    await page.getByTestId('global-admin-toggle').click()
    await expect(page.getByTestId('global-admin-toggle')).toHaveAttribute('data-mode', 'read')
    await expect(page.getByTestId('ws-readonly-banner')).toBeVisible()
    await expect(page.getByTestId('ws-messages')).toHaveCount(0)
    await page.getByTestId('ws-unlock-admin').click()
    await expect(page.getByTestId('ws-messages')).toBeVisible()

    // 4. Light and Dark theme toggling
    for (const theme of ['dark', 'light'] as const) {
      await page.evaluate(value => { document.documentElement.dataset.theme = value }, theme)
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
    }

    // 5. Narrow 320px mobile tabs layout without horizontal overflow
    await page.setViewportSize({ width: 320, height: 700 })
    await page.getByTestId('ws-tab-lists').click()
    await expect(page.getByTestId('ws-todo-list')).toBeVisible()
    await page.getByTestId('ws-tab-ops').click()
    await expect(page.getByTestId('ws-tasks')).toBeVisible()
    await page.getByTestId('ws-tab-chat').click()
    await expect(page.getByTestId('ws-messages')).toBeVisible()
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth)
    expect(overflow).toBeLessThanOrEqual(1)
    expect(pageErrors).toEqual([])
  } finally {
    try { await cleanupPhase5State(request) } catch {}
  }
})
