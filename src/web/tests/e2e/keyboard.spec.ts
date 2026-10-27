import { expect, test, type Page } from '@playwright/test'

const READ_TOKEN = 'synthetic-web-read-token'
const ADMIN_TOKEN = 'synthetic-web-admin-token'

/** 只为下方局部键盘行为用例准备场景；不代表从认证开始的纯键盘主线。 */
async function connectWithPointer(page: Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}

/** 从产品的初始自动焦点开始认证；不修正焦点，也不跳过连接按钮的 Tab 路径。 */
async function connectWithKeyboard(page: Page, token: string): Promise<void> {
  await page.goto('/')
  await expect(page.getByTestId('auth-gate')).toBeVisible()
  const tokenInput = page.getByTestId('token-input')
  await expect(tokenInput).toBeFocused()
  await page.keyboard.type(token)
  await expect(tokenInput).toHaveValue(token)
  await page.keyboard.press('Tab')
  await expect(page.getByTestId('connect-button')).toBeFocused()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('auth-gate')).toBeHidden()
  await expect(page.getByTestId('overview')).toBeVisible()
}

/** 当前焦点元素的稳定描述：用于断言"焦点在谁身上"，而不是只看可见性。 */
async function focused(page: Page): Promise<string> {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null
    if (!el || el === document.body) return 'body'
    const testId = el.getAttribute('data-testid')
    if (testId) return testId
    return el.tagName.toLowerCase() + (el.id ? '#' + el.id : '')
  })
}

/**
 * 纯键盘定位：一直按 Tab 直到焦点落在目标上（S4-L2）。
 *
 * 不用 click、不用 element.focus()——后者等于绕过键盘路径，
 * 测出来的就不是"键盘用户能不能走到这里"。
 */
async function tabTo(page: Page, target: string, max = 200): Promise<void> {
  for (let step = 0; step < max; step += 1) {
    await page.keyboard.press('Tab')
    if ((await focused(page)) === target) return
  }
  throw new Error(`Tab ${max} 步仍未到达 ${target}，最后停在 ${await focused(page)}`)
}

/** 焦点所在行/控件所属的 testid（输入框本身没有 testid，挂在行上）。 */
async function focusedRow(page: Page): Promise<string> {
  return page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null
    if (!el || el === document.body) return 'body'
    const own = el.getAttribute('data-testid')
    if (own) return own
    const row = el.closest('[data-testid]')
    return row?.getAttribute('data-testid') ?? el.tagName.toLowerCase()
  })
}

/** 纯键盘定位到某个字段行的输入控件。 */
async function tabToRow(page: Page, target: string, max = 200): Promise<void> {
  for (let step = 0; step < max; step += 1) {
    await page.keyboard.press('Tab')
    if ((await focusedRow(page)) === target) return
  }
  throw new Error(`Tab ${max} 步仍未到达行 ${target}，最后停在 ${await focusedRow(page)}`)
}

/**
 * 纯键盘定位到"某个 testid 前缀"的第一个控件（S4-L2）。
 *
 * 能力卡片的动作按钮 testid 由宿主目录决定（`cap-action-<kind>:<key>-<rev>`），
 * 把具体 key 写进用例等于和目录内容耦合：目录一变就假红。
 */
async function tabToPrefix(page: Page, prefix: string, max = 200): Promise<string> {
  for (let step = 0; step < max; step += 1) {
    await page.keyboard.press('Tab')
    const id = await focused(page)
    if (id.startsWith(prefix)) return id
  }
  throw new Error(`Tab ${max} 步仍未到达 ${prefix}*，最后停在 ${await focused(page)}`)
}

/**
 * 反向纯键盘定位（Shift+Tab）。
 *
 * 保存之后焦点停在保存条——那是文档末尾。此时再向后 Tab 会走出文档、
 * 进入浏览器外壳，而三个引擎的回绕行为并不一致（WebKit 直接把焦点交出去，
 * Firefox 停在最后几个控件上）。向后找不到目标不是"键盘路径坏了"，
 * 是"从末尾往后走本来就到不了前面的字段"。反向走是确定序列，也仍然是纯键盘。
 */
async function tabBackToRow(page: Page, target: string, max = 200): Promise<void> {
  for (let step = 0; step < max; step += 1) {
    await page.keyboard.press('Shift+Tab')
    if ((await focusedRow(page)) === target) return
  }
  throw new Error(`Shift+Tab ${max} 步仍未到达行 ${target}，最后停在 ${await focusedRow(page)}`)
}

test('键盘：认证页自动聚焦令牌输入，全程打字 + Enter 直接提交', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByTestId('auth-gate')).toBeVisible()
  // 自动聚焦本身就是键盘路径的一部分：用户落地即可打字。
  expect(await focused(page)).toBe('token-input')

  // S4-L2：这里**不用** fill——fill 是程序赋值，不经过键盘事件。
  await page.keyboard.type(READ_TOKEN)
  await expect(page.getByTestId('token-input')).toHaveValue(READ_TOKEN)
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('overview')).toBeVisible()
})

test('键盘：Tab 顺序覆盖四个导航入口，焦点始终可见', async ({ page }) => {
  await connectWithPointer(page, READ_TOKEN)

  // 认证框收起后焦点回到文档起点，所以这里就是自然的"从头 Tab"；
  // 不调用 blur —— 脚本改焦点等于绕过键盘路径（SR-EVIDENCE-01）。
  expect(await focused(page), '认证框收起后焦点应回到文档起点').toBe('body')
  const seen: string[] = []
  const invisible: string[] = []
  for (let step = 0; step < 40; step += 1) {
    await page.keyboard.press('Tab')
    const id = await focused(page)
    seen.push(id)
    // Tab 循环走到文档末尾会短暂回到 body（浏览器地址栏位），这一步不算元素焦点。
    if (id === 'body') continue
    // 每一步焦点都必须落在有可见轮廓的元素上（不是"看不见的焦点"）；
    // 只看第 40 步会随可聚焦元素个数变化而撞上 body，测不到真问题。
    const visible = await page.evaluate(() => {
      const el = document.activeElement as HTMLElement | null
      if (!el || el === document.body) return false
      const style = getComputedStyle(el)
      const outline = style.outlineStyle !== 'none' && parseFloat(style.outlineWidth || '0') > 0
      const shadow = style.boxShadow !== 'none'
      const rect = el.getBoundingClientRect()
      return rect.width > 0 && rect.height > 0 && (outline || shadow)
    })
    if (!visible) invisible.push(id)
  }

  for (const nav of ['nav-overview', 'nav-capabilities', 'nav-configuration', 'nav-tasks']) {
    expect(seen, nav).toContain(nav)
  }
  expect(seen.some((id) => id !== 'body'), '至少有一步焦点落在元素上').toBe(true)
  expect(invisible, '这些焦点没有可见轮廓').toEqual([])
})

test('键盘：配置页离开确认框焦点进框、Tab 循环、Esc 等于继续编辑', async ({ page }) => {
  await connectWithPointer(page, ADMIN_TOKEN)
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
  await page.getByTestId('config-nav-behavior').click()

  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = Number(await input.inputValue())
  await input.fill(String(original + 1))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  // 切回概览**不**弹确认框（草稿保留、秘密丢弃）；只有切到能力/任务或锁定时
  // 才走同一个三选一守卫，所以这里必须用 nav-capabilities 触发。
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()

  // 焦点进入对话框（安全动作优先），不是留在被遮住的背景按钮上。
  expect(await focused(page)).toBe('config-leave-stay')

  // Tab 循环：连续 Tab 只在这三个按钮之间转，不会跑到背景页面。
  for (let step = 0; step < 6; step += 1) {
    await page.keyboard.press('Tab')
    const id = await focused(page)
    expect(['config-leave-stay', 'config-leave-save', 'config-leave-discard']).toContain(id)
  }

  // Esc = 继续编辑（安全动作），对话框关闭且草稿仍在。
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('config-leave-dialog')).toHaveCount(0)
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  // S4-M1：关框后焦点必须回到打开它的导航按钮。只断言"框关了"是不够的——
  // 焦点掉到 body 时键盘用户下一次 Tab 要从页面开头重走一遍。
  expect(await focused(page), '关闭离开框后应把焦点还给 nav-capabilities').toBe('nav-capabilities')

  // 收尾：放弃草稿，避免污染同一宿主的后续用例。
  // 保存条是常驻元素（只换文案），所以断言"回到 0 项修改"而不是"元素消失"。
  await page.getByTestId('config-discard').click()
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
})

test('键盘：能力页确认框焦点落在确认按钮，Tab 不跑出遮罩，Esc 取消不发写请求', async ({ page }) => {
  await connectWithPointer(page, ADMIN_TOKEN)
  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  let writes = 0
  page.on('request', (request) => {
    if (request.method() === 'POST' && request.url().includes('/capabilities/execute')) writes += 1
  })

  // 用真实宿主当前可用的第一个动作（不挑具体 action，避免和目录内容耦合）。
  const action = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  await expect(action).toBeVisible()
  await action.click()
  await expect(page.getByTestId('cap-confirm')).toBeVisible()

  // 焦点落在"确认执行"（危险动作的默认落点不是取消，但必须可预测）。
  expect(await focused(page)).toBe('cap-confirm-ok')

  // Tab 循环：只在两个按钮之间转，不会跑到被遮住的背景。
  for (let step = 0; step < 5; step += 1) {
    await page.keyboard.press('Tab')
    expect(['cap-confirm-ok', 'cap-confirm-cancel']).toContain(await focused(page))
  }

  // Esc = 取消（安全动作），关闭确认框且一个写请求都没发。
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('cap-confirm')).toHaveCount(0)
  expect(writes).toBe(0)

  // S4-M1：焦点回到触发这条动作的按钮，不是掉到 body。
  const triggerId = (await action.getAttribute('data-testid')) ?? ''
  expect(await focused(page), '关闭能力确认框后应把焦点还给触发按钮').toBe(triggerId)
})

test('键盘：配置代际读取失败时，能力确认框仍能接住焦点并被 Esc 关闭', async ({ page }) => {
  await connectWithPointer(page, ADMIN_TOKEN)

  // 合成"读不到配置代际"：目录照常返回，只有 configuration/read 失败。
  // 此时"确认执行"是禁用的，焦点如果落在它身上就是无声空操作——
  // 焦点留在背景卡片上，Escape 根本到不了模态（S4-M2）。
  await page.route('**/zba/v1/configuration/read', (route) => route.fulfill({
    status: 503,
    contentType: 'application/json',
    body: JSON.stringify({ error: { code: 'service_unavailable', message: 'synthetic unavailable' } })
  }))

  await page.getByTestId('nav-capabilities').click()
  await expect(page.getByTestId('capabilities')).toBeVisible()
  await page.getByTestId('cap-enable-admin').click()
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')
  await expect(page.getByTestId('cap-context-error')).toBeVisible()

  let writes = 0
  page.on('request', (request) => {
    if (request.method() === 'POST' && request.url().includes('/capabilities/execute')) writes += 1
  })

  const action = page.locator('[data-testid^="cap-action-"]:not([disabled])').first()
  await action.focus()
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('cap-confirm')).toBeVisible()

  // 焦点必须在模态**内部**（禁用按钮不算落点）。
  const inside = await page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null
    if (!el || !el.closest('[role="dialog"]')) return false
    return !(el instanceof HTMLButtonElement && el.disabled)
  })
  expect(inside, '确认按钮禁用时焦点必须落到可用的安全按钮上').toBe(true)

  await page.keyboard.press('Escape')
  await expect(page.getByTestId('cap-confirm')).toHaveCount(0)
  expect(writes, '错误态按 Esc 是取消，不得发出写请求').toBe(0)
})
/**
 * S4-L2：配置保存主线走完整键盘路径。
 *
 * 原键盘用例用 click/fill 准备场景，只证明"模态的键盘行为对"，
 * 不证明"管理员能不能全程用键盘完成一次保存"。这条从认证页的初始焦点
 * 出发，只用键盘认证、导航和输入，不用 click/fill，也不重置焦点。
 */
test('键盘：配置保存主线全程只用键盘完成', async ({ page }) => {
  await connectWithKeyboard(page, ADMIN_TOKEN)

  // 进入配置页：Tab 到导航按钮再 Enter（不是 click）。
  await tabTo(page, 'nav-configuration')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('configuration')).toBeVisible()
  await expect(page.getByTestId('config-fields')).toBeVisible()

  // 进管理员模式同样只用键盘。
  await tabTo(page, 'config-enable-admin')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')

  // 定位到行为分区，再 Tab 到目标字段的输入框（行 testid 由 closest 提供）。
  await tabTo(page, 'config-nav-behavior')
  await page.keyboard.press('Enter')
  await tabToRow(page, 'config-field-moodTtlSeconds')

  const input = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  const original = await input.inputValue()
  // 全选后输入：只发键盘事件，不调用 fill。
  await page.keyboard.press('ControlOrMeta+A')
  await page.keyboard.type(String(Number(original) + 2))
  await expect(page.getByTestId('config-save-bar')).toContainText('1 项修改')

  // Tab 到保存按钮并 Enter：真实写请求，真实回执。
  await tabTo(page, 'config-save')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
  await expect(page.getByTestId('config-receipt')).toBeVisible()

  // 收尾：改回原值，避免同一宿主的后续用例看到被改过的配置。
  // 焦点此时在保存条（文档末尾），所以反向 Shift+Tab 回到字段行——
  // 向后 Tab 会走出文档进入浏览器外壳，那不是我们的 Tab 顺序。
  await tabBackToRow(page, 'config-field-moodTtlSeconds')
  await page.keyboard.press('ControlOrMeta+A')
  await page.keyboard.type(original)
  await tabTo(page, 'config-save')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })
})

/**
 * S4-L2：任务提交主线走完整键盘路径（含模态外的一次真实写请求）。
 */
test('键盘：任务提交主线全程只用键盘完成', async ({ page }) => {
  await connectWithKeyboard(page, ADMIN_TOKEN)
  await tabTo(page, 'nav-tasks')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('tasks')).toBeVisible()

  await tabTo(page, 'task-enable-admin')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('task-mode-badge')).toHaveText('管理员模式')

  await tabTo(page, 'task-source-key')
  await page.keyboard.type('panel:synthetic-keyboard')
  await tabTo(page, 'task-session-id')
  await page.keyboard.type('synthetic-session')
  await tabTo(page, 'task-prompt')
  await page.keyboard.type('合成任务：纯键盘提交')

  await tabTo(page, 'task-submit')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('task-submission')).toBeVisible()
  await expect(page.getByTestId('task-submission-state')).toContainText('已受理')
  const key = (await page.getByTestId('task-idempotency-key').textContent())?.trim()
  expect(key?.startsWith('task1-')).toBe(true)
})

/**
 * S4-L2：能力执行主线走完整键盘路径（含一次真实写请求与真实回执）。
 *
 * 前一条能力键盘用例只覆盖"确认框的键盘行为"；这条要证明管理员能
 * 从认证页初始焦点开始打字，只用 Tab / Shift+Tab / Enter 导航到回执；
 * 不用 click/fill，也不通过脚本改变焦点。
 */
test('键盘：能力执行主线全程只用键盘完成', async ({ page }) => {
  await connectWithKeyboard(page, ADMIN_TOKEN)
  await tabTo(page, 'nav-capabilities')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('capabilities')).toBeVisible()

  await tabTo(page, 'cap-enable-admin')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('cap-mode-badge')).toHaveText('管理员模式')

  // 走到第一张卡片的动作按钮：卡片顺序与 key 都由宿主目录决定，不写死。
  const triggerId = await tabToPrefix(page, 'cap-action-')
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('cap-confirm')).toBeVisible()
  expect(await focused(page)).toBe('cap-confirm-ok')

  // Tab 到取消再 Shift+Tab 回来，证明循环仍是纯键盘可达的。
  await page.keyboard.press('Tab')
  expect(await focused(page)).toBe('cap-confirm-cancel')
  await page.keyboard.press('Shift+Tab')
  expect(await focused(page)).toBe('cap-confirm-ok')

  // 真发一次写请求并等真实回执（不是 route 造出来的响应）。
  await page.keyboard.press('Enter')
  await expect(page.getByTestId('cap-result')).toBeVisible({ timeout: 20_000 })
  // 操作键是 `mg1:<generation>:<seconds>:<nonce>`（冒号分隔），不是 mg1-。
  await expect(page.getByTestId('cap-op-key')).toContainText('mg1:')

  // 收尾：焦点回到触发按钮（S4-M1 的同一不变量，纯键盘路径也要成立）。
  expect(await focused(page), '关闭能力确认框后应把焦点还给触发按钮').toBe(triggerId)
})

/**
 * S4-M4：保存期间按钮被禁用时，模态仍必须接得住 Escape。
 *
 * 禁用当前焦点按钮会让浏览器把焦点丢回 body，keydown 于是落到背景页面上，
 * Escape 到不了模态——用户被卡在一个既不能保存也不能退出的框里。
 * 这条用例把保存请求扣住，专门覆盖"正在提交"这个状态。
 */
test('键盘：离开框在保存提交期间仍能接住 Escape', async ({ page }) => {
  await connectWithPointer(page, ADMIN_TOKEN)
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')
  await page.getByTestId('config-nav-behavior').click()
  const field = page.getByTestId('config-field-moodTtlSeconds').locator('input')
  await field.fill(String(Number(await field.inputValue()) + 1))

  let release!: () => void
  const held = new Promise<void>((resolve) => { release = resolve })
  let seenSave!: () => void
  const saveSeen = new Promise<void>((resolve) => { seenSave = resolve })
  await page.route('**/zba/v1/configuration/save', async (route) => {
    const response = await route.fetch()
    seenSave()
    await held
    await route.fulfill({ response })
  })

  await page.getByTestId('lock').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()
  await page.getByTestId('config-leave-save').click()
  await saveSeen

  // 提交期间"保存并锁定"被禁用：焦点必须仍留在框内（不是 body）。
  await expect(page.getByTestId('config-leave-save')).toBeDisabled()
  const inDialog = await page.evaluate(() => {
    const el = document.activeElement as HTMLElement | null
    return !!el?.closest('[role="dialog"]')
  })
  expect(inDialog, '保存期间焦点必须留在模态内').toBe(true)

  // 而且 Escape 必须真的能退出（安全动作）。
  await page.keyboard.press('Escape')
  await expect(page.getByTestId('config-leave-dialog')).toHaveCount(0)
  await expect(page.getByTestId('configuration')).toBeVisible()
  release()
  await page.waitForTimeout(300)

  // 收尾：保存已经完成，草稿清零；旧框不得被迟到的保存重新打开。
  await expect(page.getByTestId('config-leave-dialog')).toHaveCount(0)
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')
})
