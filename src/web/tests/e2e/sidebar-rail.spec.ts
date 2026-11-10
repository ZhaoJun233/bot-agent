import { expect, test } from '@playwright/test'

// 宽屏侧栏可收起成图标栏：文字仍留在可访问树里，偏好跨刷新保留；窄屏不显示切换按钮。
test('sidebar collapses to an icon rail and remembers the choice', async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 })
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-read-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()

  const toggle = page.getByTestId('sidebar-rail-toggle')
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  await expect(toggle).toHaveAccessibleName('收起侧栏')
  const sidebar = page.locator('.sidebar')
  const wide = (await sidebar.boundingBox())!.width

  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await expect(toggle).toHaveAccessibleName('展开侧栏')
  await expect.poll(async () => (await sidebar.boundingBox())!.width).toBeLessThan(wide)
  await expect(page.locator('.brand-text')).toBeHidden()
  // 收起后导航按钮的名字不变，可以照常按名字找到并切页。
  const logs = page.getByRole('button', { name: '运行日志', exact: true })
  await expect(logs).toHaveAccessibleName('运行日志')
  await logs.click()
  await expect(page.getByTestId('nav-logs')).toHaveClass(/active/)
  await expect(page.getByTestId('nav-overview')).toHaveAttribute('title', /.+/)
  const frame = (await page.locator('.app-frame').boundingBox())!
  expect(frame.x).toBeLessThanOrEqual(64)

  expect(await page.evaluate(() => localStorage.getItem('botagent.sidebar.rail'))).toBe('1')
  // Real reload: reconnect because the session token is intentionally memory-only.
  await page.reload()
  await page.getByTestId('token-input').fill('synthetic-web-read-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(toggle).toHaveAttribute('aria-expanded', 'false')
  await expect(toggle).toHaveAccessibleName('展开侧栏')
  await expect.poll(async () => (await sidebar.boundingBox())!.width).toBeLessThan(wide)
  await expect(page.locator('.brand-text')).toBeHidden()
  await expect(page.getByRole('button', { name: '运行日志', exact: true })).toHaveAccessibleName('运行日志')
  expect(await page.evaluate(() => localStorage.getItem('botagent.sidebar.rail'))).toBe('1')
  await toggle.click()
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  expect(await page.evaluate(() => localStorage.getItem('botagent.sidebar.rail'))).toBe('0')
  await page.reload()
  await page.getByTestId('token-input').fill('synthetic-web-read-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(toggle).toHaveAttribute('aria-expanded', 'true')
  await expect(toggle).toHaveAccessibleName('收起侧栏')
  await expect.poll(async () => (await sidebar.boundingBox())!.width).toBe(wide)
  await expect(page.locator('.brand-text')).toBeVisible()
  expect(await page.evaluate(() => localStorage.getItem('botagent.sidebar.rail'))).toBe('0')
})

test('rail toggle is hidden on narrow layouts', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 })
  await page.addInitScript(() => localStorage.setItem('botagent.sidebar.rail', '1'))
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-read-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await expect(page.getByTestId('sidebar-rail-toggle')).toBeHidden()
  expect(await page.evaluate(() => localStorage.getItem('botagent.sidebar.rail'))).toBe('1')
  await expect(page.locator('.brand-text')).toBeVisible()
  const logs = page.getByRole('button', { name: '运行日志', exact: true })
  await expect(logs).toHaveAccessibleName('运行日志')
  await expect(logs.locator('.side-nav-text')).toHaveCSS('position', 'static')
  await expect(page.locator('.side-nav')).toHaveCSS('display', 'grid')
  // 390px 命中末段 max-width: 640px 的三列规则，覆盖早段四列布局。
  expect(await page.locator('.side-nav').evaluate((nav) => getComputedStyle(nav).gridTemplateColumns.split(/\s+/).length)).toBe(3)
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
})
