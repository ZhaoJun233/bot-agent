import { expect, test, type Page } from '@playwright/test'

/**
 * PNL-12：错误与状态不只靠颜色表达。
 * 所有带语气色的元素（data-tone 徽标、.inline-notice、.error-notice）都必须带可读文字，
 * 去掉颜色后仍能看出含义。这里只检查"有文字"这一可测条件，配色是否美观不在范围内。
 */

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const TONED = '[data-tone], .inline-notice, .error-notice'

async function tonedWithoutText(page: Page): Promise<string[]> {
  return page.locator(TONED).evaluateAll((nodes) =>
    nodes
      .filter((node) => {
        const el = node as HTMLElement
        const style = getComputedStyle(el)
        const visible = el.getClientRects().length > 0 && style.visibility !== 'hidden'
        const label = (el.innerText || el.getAttribute('aria-label') || el.getAttribute('title') || '').trim()
        return visible && label.length === 0
      })
      .map((node) => (node as HTMLElement).outerHTML.slice(0, 160)))
}

test('颜色之外：四个页面里所有带语气色的徽标与提示都有可读文字（PNL-12）', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('token-input').fill(ADMIN_TOKEN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()

  const pages: Array<[string, string]> = [
    ['nav-overview', 'overview'],
    ['nav-capabilities', 'capabilities'],
    ['nav-configuration', 'configuration'],
    ['nav-tasks', 'tasks']
  ]
  let checked = 0
  for (const [navId, viewId] of pages) {
    await page.getByTestId(navId).click()
    await expect(page.getByTestId(viewId)).toBeVisible()
    await page.waitForLoadState('networkidle')
    checked += await page.locator(TONED).count()
    expect(await tonedWithoutText(page), viewId).toEqual([])
  }
  expect(checked).toBeGreaterThan(0)
})

test('颜色之外：认证失败的错误提示带文字与错误码，并以 role=alert 播报（PNL-12）', async ({ page }) => {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-wrong-token')
  await page.getByTestId('connect-button').click()
  const notice = page.getByTestId('error-notice')
  await expect(notice).toBeVisible()
  await expect(notice).toHaveAttribute('role', 'alert')
  expect((await notice.innerText()).trim().length).toBeGreaterThan(0)
  expect(await tonedWithoutText(page)).toEqual([])
})
