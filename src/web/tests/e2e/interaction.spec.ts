import { expect, test, type Page } from '@playwright/test'

const ADMIN = 'synthetic-web-admin-token'

async function openTasks(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(ADMIN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()
  await expect(page.getByTestId('tasks')).toBeVisible()
  await page.getByTestId('task-enable-admin').click()
  await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')
}

const approval = (overrides: Record<string, unknown> = {}) => ({
  id: 'approval-synthetic-1',
  toolId: 'tool.synthetic',
  summary: '合成审批：发送一条测试消息',
  session: 'synthetic-session',
  expiresAt: '2030-01-01T00:00:00+00:00',
  policyVersion: 3,
  ...overrides
})

/**
 * 审批队列的**浏览器层**合成响应。
 *
 * 为什么这里用 route 而不是真实宿主：合成宿主的模型端点是故意不可达的
 * （127.0.0.1:1），不会真的产生一条待审批请求；后端审批台账的语义由
 * ZbaProbe 与集成场景覆盖。这里验证的是**前端在拿到真实契约形状的响应之后**
 * 怎么表达"批准 / 拒绝 / 过期 / 回执不一致"，属于 PNL-07 的界面一侧。
 * 因此本文件不声称"真实审批副作用已发生"。
 */
test('审批：批准成功后该行消失，并明确说明批准不等于工具已成功', async ({ page }) => {
  let listReads = 0
  let decided: string | null = null
  await page.route('**/zba/v1/approvals**', async (route) => {
    const url = route.request().url()
    if (url.endsWith('/decide')) {
      decided = route.request().postDataJSON().approve === true ? 'approve' : 'reject'
      await route.fulfill({ status: 200, contentType: 'application/json', json: { ok: true, decision: 'approve', reasonCode: 'approved' } })
      return
    }
    listReads += 1
    await route.fulfill({ status: 200, contentType: 'application/json', json: decided ? [] : [approval()] })
  })

  await openTasks(page)
  await expect(page.getByTestId('task-approval-0')).toBeVisible()
  await page.getByTestId('task-approve-0').click()

  await expect(page.getByTestId('task-decision-notice')).toContainText('已提交决定：批准')
  // 关键语义：批准只是"同意执行"，不能写成工具已经成功。
  await expect(page.getByTestId('task-decision-notice')).toContainText('不代表工具已经成功')
  await expect(page.getByTestId('task-approval-0')).toHaveCount(0)
  expect(decided).toBe('approve')
  // 成功之后也补读了一次队列（kick），不是只靠本地删行。
  expect(listReads).toBeGreaterThan(1)
})

test('审批：服务端判定过期时不冒报成功，该行标记未生效且队列被补读', async ({ page }) => {
  let listReads = 0
  await page.route('**/zba/v1/approvals**', async (route) => {
    const url = route.request().url()
    if (url.endsWith('/decide')) {
      await route.fulfill({
        status: 409,
        contentType: 'application/json',
        json: { error: { code: 'approval_rejected', message: '审批未执行。', reasonCode: 'expired' } }
      })
      return
    }
    listReads += 1
    await route.fulfill({ status: 200, contentType: 'application/json', json: [approval()] })
  })

  await openTasks(page)
  const before = listReads
  await page.getByTestId('task-reject-0').click()

  // 失败必须如实说明，并且该行不能被当成"已处理"删掉。
  await expect(page.getByTestId('task-decision-notice')).toHaveCount(0)
  await expect(page.getByTestId('task-approval-stale-0')).toContainText('未生效')
  await expect(page.getByTestId('task-approval-0')).toBeVisible()
  await expect(page.getByTestId('task-approve-0')).toBeDisabled()
  // 决定失败后必须补读独立队列（S3-09），不是靠反复提交争取成功。
  await expect.poll(() => listReads).toBeGreaterThan(before)
})

test('审批：回执与本次决定不一致时不显示成已批准', async ({ page }) => {
  await page.route('**/zba/v1/approvals**', async (route) => {
    if (route.request().url().endsWith('/decide')) {
      // 发送 approve，回执却写 reject：这是契约错配，不能采纳。
      await route.fulfill({ status: 200, contentType: 'application/json', json: { ok: true, decision: 'reject', reasonCode: 'approved' } })
      return
    }
    await route.fulfill({ status: 200, contentType: 'application/json', json: [approval()] })
  })

  await openTasks(page)
  await page.getByTestId('task-approve-0').click()

  await expect(page.getByTestId('task-decision-notice')).toHaveCount(0)
  await expect(page.locator('body')).toContainText('回执与本次决定不一致')
  await expect(page.getByTestId('task-approval-0')).toBeVisible()
})

test('取消：结果未知时不写"已取消"，保留"已请求（未确认）"', async ({ page }) => {
  await page.route('**/zba/v1/approvals**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', json: [] })
  })
  await page.route('**/zba/v1/tasks**', async (route) => {
    const url = route.request().url()
    // 管理员探测（GET .../s1-permission-check/result）必须放行到真实宿主，
    // 否则会被下面的列表桩一起吃掉，页面永远进不了管理员模式。
    if (url.includes('/s1-permission-check/')) { await route.continue(); return }
    if (url.endsWith('/cancel')) {
      // 服务端无法确认这次取消：outcome=unknown、未登记 cancellationRequested。
      await route.fulfill({
        status: 200,
        contentType: 'application/json',
        json: { taskId: 'zt-synthetic-1', state: 1, cancellationRequested: false, outcome: 'unknown' }
      })
      return
    }
    if (route.request().method() === 'POST') { await route.continue(); return }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      json: {
        items: [{
          id: 'zt-synthetic-1', state: 1, target: 'server', sessionId: 'synthetic-session',
          createdAt: '2026-10-24T10:00:00+00:00', updatedAt: '2026-10-24T10:01:00+00:00',
          toolCalls: 0, cancellationRequested: false
        }],
        nextCursor: null
      }
    })
  })

  await openTasks(page)
  const cancel = page.getByTestId('task-cancel-zt-synthetic-1')
  await expect(cancel).toBeVisible()
  await cancel.click()

  // 界面不能出现"已取消"这种断言，只能报服务端的真实结论 + 未知。
  await expect(page.locator('body')).toContainText('服务端没有登记这次取消')
  await expect(page.getByTestId('task-item-zt-synthetic-1')).not.toContainText('已取消')
})

test('未保存保护：任务表单填了内容后切走再回来，草稿仍在', async ({ page }) => {
  await openTasks(page)
  await page.getByTestId('task-source-key').fill('panel:synthetic-draft')
  await page.getByTestId('task-session-id').fill('synthetic-session')
  await page.getByTestId('task-prompt').fill('合成草稿：还没提交')

  await page.getByTestId('nav-overview').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-tasks').click()
  await expect(page.getByTestId('tasks')).toBeVisible()

  await expect(page.getByTestId('task-source-key')).toHaveValue('panel:synthetic-draft')
  await expect(page.getByTestId('task-session-id')).toHaveValue('synthetic-session')
  await expect(page.getByTestId('task-prompt')).toHaveValue('合成草稿：还没提交')
})

test('不可信文本：服务端摘要里的脚本/标签按文本渲染，不执行', async ({ page }) => {
  const payload = '<img src=x onerror="window.__xss=1">合成摘要'
  await page.route('**/zba/v1/approvals**', async (route) => {
    await route.fulfill({ status: 200, contentType: 'application/json', json: [approval({ summary: payload })] })
  })

  await openTasks(page)
  await expect(page.getByTestId('task-approval-0')).toBeVisible()

  // 文本被原样显示（作为文本节点），但没有被解析成元素。
  await expect(page.getByTestId('task-approval-0')).toContainText(payload)
  const injected = await page.evaluate(() => ({
    flagged: (window as unknown as { __xss?: number }).__xss ?? null,
    images: document.querySelectorAll('[data-testid="task-approval-0"] img').length
  }))
  expect(injected.flagged).toBeNull()
  expect(injected.images).toBe(0)
})
/**
 * S4-M3：迟到的保存结果不许批准一个后来新建的离开框。
 *
 * 真实场景：用户点「保存并锁定」→ 保存请求已发出 → 等不及，按「继续编辑」
 * 关掉这个框 → 再点任务导航，弹出新的离开框 → 此时旧保存的响应才回来。
 * 旧回调如果直接 resolve，就会替用户批准那个一个选择都没做过的新框，
 * 并把页面跳到任务页。
 *
 * 这里用真实宿主保存（route.fetch），只暂扣响应，不伪造任何业务码。
 */
test('未保存保护：旧保存迟到返回时不得替新离开框做决定', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('token-input').fill(ADMIN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
  await page.getByTestId('config-nav-behavior').click()
  const field = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  await field.fill(String(Number(await field.inputValue()) + 1))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  let release!: () => void
  const held = new Promise<void>((resolve) => { release = resolve })
  let seenSave!: () => void
  const saveSeen = new Promise<void>((resolve) => { seenSave = resolve })
  await page.route('**/zba/v1/configuration/save', async (route) => {
    // 先让真实宿主处理，拿到真实回执；只把响应压在浏览器这一侧。
    const response = await route.fetch()
    seenSave()
    await held
    await route.fulfill({ response })
  })

  await page.getByTestId('lock').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()
  await page.getByTestId('config-leave-save').click()
  await saveSeen

  // 用户放弃这个框，再从导航开一个新的：两次 requestLeave 属于不同代际。
  await page.getByTestId('config-leave-stay').click()
  await expect(page.getByTestId('config-leave-dialog')).toHaveCount(0)
  await page.getByTestId('nav-tasks').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()

  release()
  // 旧保存确实完成了（草稿清零），但它只更新保存状态，不替新框做决定。
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
  await page.waitForTimeout(300)
  await expect(page.getByTestId('config-leave-dialog'), '新离开框没有被旧保存批准').toBeVisible()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('tasks')).toHaveCount(0)

  // 收尾：这个框由用户自己关掉，页面仍在配置页。
  await page.getByTestId('config-leave-stay').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
})
