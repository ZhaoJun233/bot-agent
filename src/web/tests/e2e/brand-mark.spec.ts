import { expect, test } from '@playwright/test'

for (const width of [390, 1280]) {
  test('friendly brand remains visible in login and sidebar at ' + width, async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 800 })
    await page.goto('/')
    await expect(page.locator('.auth-logo .brand-mark')).toBeVisible()
    await page.locator('.auth-brand-head').screenshot({ path: testInfo.outputPath('login-brand.png') })
    await page.getByTestId('token-input').fill('synthetic-web-read-token')
    await page.getByTestId('connect-button').click()
    await expect(page.getByTestId('overview')).toBeVisible()
    const mark = page.locator('.sidebar-brand .brand-mark')
    await expect(mark).toBeVisible()
    await expect(mark).toHaveAttribute('aria-hidden', 'true')
    const box = await mark.boundingBox()
    expect(box!.width).toBe(box!.height)
    await page.locator('.sidebar-brand').screenshot({ path: testInfo.outputPath('sidebar-brand-light.png') })
    await page.getByRole('button', { name: '切换到深色主题', exact: true }).click()
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark')
    await expect(mark).toBeVisible()
    await page.locator('.sidebar-brand').screenshot({ path: testInfo.outputPath('sidebar-brand-dark.png') })
  })
}
