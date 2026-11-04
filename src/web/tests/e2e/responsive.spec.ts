import { expect, test, type Page } from '@playwright/test'

const READ_TOKEN = 'synthetic-web-read-token'
const ADMIN_TOKEN = 'synthetic-web-admin-token'

async function connect(page: Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}

/**
 * 量"横向溢出"用几何而不是截图：
 * - 页面级：documentElement.scrollWidth 超过 clientWidth 就是整页横向滚动。
 * - 容器级：元素自身 scrollWidth 超过 clientWidth 就是内部横向滚动。
 * 两者都是 S1 的 F8 与 PNL-12 的可测条件，截图只作对照。
 */
async function overflow(page: Page, selector: string): Promise<{ client: number; scroll: number }> {
  return page.evaluate((sel) => {
    const el = document.querySelector(sel)
    if (!el) throw new Error('missing ' + sel)
    return { client: el.clientWidth, scroll: el.scrollWidth }
  }, selector)
}

/** 整页是否横向溢出（PNL-12 的可测条件，不是视觉偏好）。 */
async function pageOverflow(page: Page): Promise<{ client: number; scroll: number }> {
  return page.evaluate(() => ({
    client: document.documentElement.clientWidth,
    scroll: document.documentElement.scrollWidth
  }))
}

for (const width of [390, 360]) {
  test(`响应式 ${width}：整页无横向溢出，组件表不靠内部横滚（F8 收敛）`, async ({ page }) => {
    await page.setViewportSize({ width, height: 780 })
    await connect(page, READ_TOKEN)

    const doc = await pageOverflow(page)
    expect(doc.scroll).toBeLessThanOrEqual(doc.client + 1)

    // F8（S1 登记留 S4）：640 断点把组件表收敛为「组件 / 状态」两列之后，
    // 表格自身也不该再需要内部横滚。这里量的是几何，不是截图观感。
    const table = await overflow(page, '[data-testid="components"]')
    expect(table.scroll).toBeLessThanOrEqual(table.client + 1)
  })

  test(`响应式 ${width}：侧栏折叠为顶栏，四个入口仍可点`, async ({ page }) => {
    await page.setViewportSize({ width, height: 780 })
    await connect(page, READ_TOKEN)

    for (const id of ['nav-overview', 'nav-capabilities', 'nav-configuration', 'nav-tasks']) {
      const box = await page.getByTestId(id).boundingBox()
      expect(box, id).not.toBeNull()
      expect(box!.width, id).toBeGreaterThan(40)
    }

    const sidebar = await page.evaluate(() => {
      const el = document.querySelector('.sidebar')
      return el ? getComputedStyle(el).position : 'missing'
    })
    expect(sidebar).toBe('static')
  })

  test(`响应式 ${width}：四个页面都能打开，且整页都不横向溢出`, async ({ page }) => {
    await page.setViewportSize({ width, height: 780 })
    await connect(page, READ_TOKEN)

    for (const [nav, pageId] of [
      ['nav-capabilities', 'capabilities'],
      ['nav-configuration', 'configuration'],
      ['nav-tasks', 'tasks'],
      ['nav-overview', 'overview']
    ] as const) {
      await page.getByTestId(nav).click()
      await expect(page.getByTestId(pageId)).toBeVisible()
      const doc = await pageOverflow(page)
      expect(doc.scroll, pageId).toBeLessThanOrEqual(doc.client + 1)
    }
  })

  test(`响应式 ${width}：移动端焦点不被遮挡，按钮点得到（软键盘/安全区）`, async ({ page }) => {
    await page.setViewportSize({ width, height: 640 })
    await connect(page, READ_TOKEN)
    await page.getByTestId('nav-tasks').click()
    await expect(page.getByTestId('tasks')).toBeVisible()

    // 关键操作（提交入口）必须真的在视口内可点，不被固定的保存条/顶栏盖住。
    const submit = page.getByTestId('task-submit')
    await submit.scrollIntoViewIfNeeded()
    const box = await submit.boundingBox()
    const viewport = page.viewportSize()!
    expect(box).not.toBeNull()
    expect(box!.y).toBeGreaterThanOrEqual(0)
    expect(box!.y + box!.height).toBeLessThanOrEqual(viewport.height + 1)
    // 命中测试：点在这个坐标上，命中的就是它自己（没有被别的元素盖住）。
    const hit = await page.evaluate(({ x, y }) => {
      const el = document.elementFromPoint(x, y) as HTMLElement | null
      return el?.closest('[data-testid="task-submit"]') !== null
    }, { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 })
    expect(hit).toBe(true)
  })
}
/**
 * S4-L2：移动视口下的管理员可操作行为。
 *
 * 原来的移动用例用只读令牌，只量了一个 disabled 提交按钮的几何——
 * 那证明不了管理员在窄屏上能不能真的填表、真的提交。
 * 这里用管理员令牌，走一次真实的"填 → 提交 → 已受理"。
 *
 * 边界：这是 360px 视口模拟，不是 iOS/Android 真机，也不是真实软键盘；
 * 它证明的是布局与命中，不证明真机输入法行为。
 */
for (const width of [390, 360]) {
  test(`响应式 ${width}：管理员在移动视口能填表并真的提交`, async ({ page }) => {
    await page.setViewportSize({ width, height: 780 })
    await connect(page, ADMIN_TOKEN)
    await page.getByTestId('nav-tasks').click()
    await expect(page.getByTestId('tasks')).toBeVisible()
    await page.getByTestId('task-enable-admin').click()
    await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')

    // 输入控件在窄屏上必须真的可点可填（不被顶栏/固定条盖住）。
    await page.getByTestId('task-source-key').fill('panel:synthetic-mobile')
    await page.getByTestId('task-session-id').fill('synthetic-session')
    await page.getByTestId('task-prompt').fill('合成任务：移动视口提交')

    const submit = page.getByTestId('task-submit')
    await submit.scrollIntoViewIfNeeded()
    await expect(submit).toBeEnabled()
    const box = await submit.boundingBox()
    const viewport = page.viewportSize()!
    expect(box).not.toBeNull()
    expect(box!.y).toBeGreaterThanOrEqual(0)
    expect(box!.y + box!.height).toBeLessThanOrEqual(viewport.height + 1)
    const hit = await page.evaluate(({ x, y }) => {
      const el = document.elementFromPoint(x, y) as HTMLElement | null
      return el?.closest('[data-testid="task-submit"]') !== null
    }, { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 })
    expect(hit, '提交按钮没有被遮挡').toBe(true)

    // 真的提交：这是可操作行为，不是几何断言。
    await submit.click()
    await expect(page.getByTestId('task-submission-state')).toContainText('已受理')

    // 提交后整页仍不横向溢出（保存条/回执面板不能把页面撑宽）。
    const doc = await pageOverflow(page)
    expect(doc.scroll).toBeLessThanOrEqual(doc.client + 1)
  })
}
