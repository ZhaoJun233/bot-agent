import { expect, test, type Page } from '@playwright/test'

async function connect(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-read-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}
async function noPageOverflow(page: Page): Promise<void> {
  const dimensions = await page.evaluate(() => ({ client: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }))
  expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.client + 1)
}
const pages = [
  ['nav-overview', 'overview'], ['nav-configuration', 'configuration'],
  ['nav-capabilities', 'capabilities'], ['nav-tasks', 'tasks'],
  ['nav-traces', 'traces'], ['nav-logs', 'runtime-logs-page']
] as const

for (const theme of ['light', 'dark']) {
  test(`issue148 ${theme}: six pages reflow at 320px with doubled root-font proxy`, async ({ page }) => {
    await page.setViewportSize({ width: 320, height: 800 })
    await connect(page)
    await page.evaluate((value) => { document.documentElement.dataset.theme = value }, theme)
    const original = await page.getByTestId('nav-overview').evaluate(el => parseFloat(getComputedStyle(el).fontSize))
    await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
    const enlarged = await page.getByTestId('nav-overview').evaluate(el => parseFloat(getComputedStyle(el).fontSize))
    expect(enlarged / original).toBeGreaterThanOrEqual(1.99)
    for (const [nav, view] of pages) {
      await page.getByTestId(nav).click()
      await expect(page.getByTestId(view)).toBeVisible()
      await noPageOverflow(page)
      const target = await page.getByTestId(nav).boundingBox()
      expect(target?.height).toBeGreaterThanOrEqual(44)
      expect(target?.width).toBeGreaterThanOrEqual(44)
    }
  })
}

test('issue148 auth at 320px and 200% root-font proxy remains usable', async ({ page }) => {
  await page.setViewportSize({ width: 320, height: 800 }); await page.goto('/')
  await page.evaluate(() => { document.documentElement.style.fontSize = '200%' })
  await expect(page.getByTestId('token-input')).toBeVisible()
  await expect(page.getByTestId('connect-button')).toBeVisible()
  await noPageOverflow(page)
})

test('issue148 reduced motion disables looping brand and busy animation', async ({ page }) => {
  await page.emulateMedia({ reducedMotion: 'reduce' }); await connect(page)
  const motion = await page.evaluate(() => Array.from(document.querySelectorAll('.status-dot, .spinner')).map(el => getComputedStyle(el).animationName))
  expect(motion.every(name => name === 'none')).toBe(true)
})
