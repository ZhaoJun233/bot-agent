import { test, expect, type Page } from '@playwright/test'

const epoch = 'a'.repeat(32)
function feed(start = 1, count = 3, hasMore = false) {
  return { capacity: 600, cursor: 'cursor-' + (start + count - 1), hasMore, items: Array.from({ length: count }, (_, index) => {
    const number = start + index
    return { id: epoch + ':' + number, sequence: String(number), time: 1793773800000 + number,
      level: number % 2 ? 'info' : 'warn', source: 'Agent', message: '安全摘要 ' + number, redacted: true }
  }) }
}
async function openLogs(page: Page) {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-read-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByRole('button', { name: '运行日志', exact: true }).click()
  await expect(page.getByRole('heading', { name: '运行日志' })).toBeVisible()
}
async function installClipboard(page: Page) {
  await page.addInitScript(() => {
    Object.defineProperty(navigator, 'clipboard', { configurable: true, value: { writeText: async (text: string) => {
      document.documentElement.dataset.testClipboard = text
    } } })
  })
}

test.describe('安全 JSON 日志终端', () => {
  test('明暗主题的安全提示和控件使用有效调色板', async ({ page }) => {
    await page.route('**/zba/v1/logs/feed**', route => route.fulfill({ json: feed() }))
    await openLogs(page)
    for (const theme of ['light', 'dark']) {
      await page.evaluate(value => { document.documentElement.dataset.theme = value }, theme)
      const colors = await page.getByTestId('runtime-logs-page').evaluate(element => {
        const root = getComputedStyle(document.documentElement)
        const swatch = document.createElement('span')
        element.append(swatch)
        const resolve = (token: string) => { swatch.style.color = root.getPropertyValue(token); return getComputedStyle(swatch).color }
        const safety = getComputedStyle(element.querySelector('.logs-safety')!)
        const input = getComputedStyle(element.querySelector('.logs-search input')!)
        const result = { safetyColor: safety.color, safetyBackground: safety.backgroundColor, inputColor: input.color, inputBackground: input.backgroundColor,
          expectedSafetyColor: resolve('--ok-text'), expectedSafetyBackground: resolve('--ok-bg'), expectedInputColor: resolve('--text'), expectedInputBackground: resolve('--surface') }
        swatch.remove()
        return result
      })
      expect(colors.safetyColor).toBe(colors.expectedSafetyColor)
      expect(colors.safetyBackground).toBe(colors.expectedSafetyBackground)
      expect(colors.inputColor).toBe(colors.expectedInputColor)
      expect(colors.inputBackground).toBe(colors.expectedInputBackground)
    }
  })
  test('首次快照与一秒增量、过滤纯文本且不访问原始接口', async ({ page }) => {
    let calls = 0
    const routes: string[] = []
    page.on('request', request => { if (request.url().includes('/logs')) routes.push(request.url()) })
    await page.route('**/zba/v1/logs/feed**', async route => {
      calls++
      const data = calls === 1 ? feed() : feed(4, 1)
      if (calls === 1) data.items[0]!.message = '<img src=x onerror=alert(1)>'
      await route.fulfill({ json: data })
    })
    await openLogs(page)
    const view = page.getByTestId('runtime-logs-page')
    await expect(view.getByTestId('log-row')).toHaveCount(3)
    expect(await view.locator('img').count()).toBe(0)
    const started = Date.now()
    await expect(view.getByTestId('log-row')).toHaveCount(4, { timeout: 2000 })
    expect(Date.now() - started).toBeLessThan(2000)
    await view.getByLabel('日志级别', { exact: true }).selectOption('warn')
    await expect(view.getByTestId('log-row')).toHaveCount(2)
    expect(routes.every(url => url.includes('/zba/v1/logs/feed'))).toBe(true)
  })

  test('600行上限、只复制视口或选区、清空后保留游标', async ({ page }) => {
    await installClipboard(page)
    const cursors: Array<string | null> = []
    let calls = 0
    await page.route('**/zba/v1/logs/feed**', route => {
      cursors.push(new URL(route.request().url()).searchParams.get('cursor'))
      calls++
      return route.fulfill({ json: calls <= 4 ? feed((calls - 1) * 200 + 1, 200, calls < 4) : feed(800, 1) })
    })
    await openLogs(page)
    const view = page.getByTestId('runtime-logs-page')
    await expect(view.getByTestId('log-row')).toHaveCount(600)
    await expect(view.getByText('本页已淘汰 200 条', { exact: false })).toBeVisible()
    await view.getByRole('button', { name: '复制当前视口' }).click()
    const copied = await page.locator('html').getAttribute('data-test-clipboard')
    expect(copied).toBeTruthy()
    expect(copied!.split('\n').length).toBeLessThan(600)
    expect(copied).not.toContain('安全摘要 201')
    const last = view.getByTestId('log-row').last().locator('code')
    await last.evaluate(element => {
      const range = document.createRange(); range.selectNodeContents(element)
      const selection = window.getSelection()!; selection.removeAllRanges(); selection.addRange(range)
      document.dispatchEvent(new Event('selectionchange'))
    })
    await view.getByRole('button', { name: '复制当前视口' }).click()
    expect(await page.locator('html').getAttribute('data-test-clipboard')).toBe('安全摘要 800')
    await view.getByRole('button', { name: '清空本页' }).click()
    await expect(view.getByTestId('log-row')).toHaveCount(0)
    await page.waitForTimeout(1100)
    await expect(view.getByTestId('log-row')).toHaveCount(0)
    expect(cursors.at(-1)).toBe('cursor-800')
  })

  test('上滚保留阅读锚点，接收不停并可返回最新', async ({ page }) => {
    let append = false
    await page.route('**/zba/v1/logs/feed**', route => route.fulfill({ json: append ? feed(201, 50) : feed(1, 200) }))
    await openLogs(page)
    const view = page.getByTestId('runtime-logs-page')
    await expect(view.getByTestId('log-row')).toHaveCount(200)
    const terminal = view.getByTestId('logs-terminal')
    await terminal.evaluate(element => { element.scrollTop = 1500; element.dispatchEvent(new Event('scroll')) })
    const before = await terminal.evaluate(element => {
      const top = element.getBoundingClientRect().top
      const row = Array.from(element.querySelectorAll<HTMLElement>('[data-log-id]')).find(row => row.getBoundingClientRect().bottom > top)!
      return { id: row.dataset.logId, offset: row.getBoundingClientRect().top - top }
    })
    append = true
    await expect(view.getByTestId('log-row')).toHaveCount(250, { timeout: 2000 })
    const after = await terminal.evaluate((element, id) => {
      const row = Array.from(element.querySelectorAll<HTMLElement>('[data-log-id]')).find(row => row.dataset.logId === id)!
      return row.getBoundingClientRect().top - element.getBoundingClientRect().top
    }, before.id)
    expect(Math.abs(after - before.offset)).toBeLessThan(2)
    await expect(view.getByRole('button', { name: '返回最新', exact: false })).toContainText('50 条未读')
    await view.getByRole('button', { name: '返回最新', exact: false }).click()
    await expect.poll(() => terminal.evaluate(element => element.scrollHeight - element.scrollTop - element.clientHeight)).toBeLessThan(2)
  })

  test('320px 长行局部横滚，页面不溢出', async ({ page }, testInfo) => {
    await page.setViewportSize({ width: 320, height: 900 })
    const data = feed(1, 1); data.items[0]!.message = 'Long-safe-row-'.repeat(35)
    await page.route('**/zba/v1/logs/feed**', route => route.fulfill({ json: data }))
    await openLogs(page)
    const view = page.getByTestId('runtime-logs-page')
    await expect(view.getByTestId('log-row')).toHaveCount(1)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
    const line = view.locator('.log-message')
    expect(await line.evaluate(element => element.scrollWidth > element.clientWidth)).toBe(true)
    await view.getByTestId('logs-terminal').focus()
    await expect(view.getByTestId('logs-terminal')).toBeFocused()
    await page.screenshot({ path: testInfo.outputPath('runtime-logs-320.png'), fullPage: true })
  })
})
