import { test, expect } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
const out = process.env.VISUAL_OUT ?? 'test-results/configuration-ownership'
for (const width of [1440, 768, 390, 320]) for (const theme of ['light', 'dark']) {
  test('ability configuration ' + width + ' ' + theme, async ({ page }) => {
    await page.setViewportSize({ width, height: 1000 })
    await page.addInitScript(value => localStorage.setItem('botagent-appearance', value), theme)
    await page.goto('/web/')
    await page.getByTestId('token-input').fill('synthetic-web-admin-token')
    await page.getByTestId('connect-button').click()
    await expect(page.getByTestId('overview')).toBeVisible()
    await page.getByTestId('nav-configuration').click()
    await page.getByTestId('config-enable-admin').click()
    await expect(page.getByTestId('config-fields')).toBeVisible()
    await expect(page.getByTestId('config-nav-model')).toHaveCount(0)
    await expect(page.getByTestId('config-secrets')).toHaveCount(0)
    fs.mkdirSync(out, { recursive: true })
    await page.screenshot({ path: path.join(out, width + '-' + theme + '-basic.png'), fullPage: true, animations: 'disabled' })
    await page.getByTestId('nav-capabilities').click()
    await expect(page.getByTestId('ability-settings')).toBeVisible()
    await page.screenshot({ path: path.join(out, width + '-' + theme + '-abilities.png'), fullPage: true, animations: 'disabled' })
    for (const id of ['models', 'agent', 'platform', 'reporting', 'conversation']) {
      await page.getByTestId('ability-settings-' + id).click()
      const dialog = page.getByTestId('ability-configuration-dialog')
      await expect(dialog.getByTestId('config-fields')).toBeVisible()
      const box = await dialog.getByRole('dialog').boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(0)
      expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1)
      expect(await dialog.getByRole('dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
      await page.screenshot({ path: path.join(out, width + '-' + theme + '-' + id + '-dialog.png'), animations: 'disabled' })
      await dialog.getByTestId('ability-configuration-dialog-close').click()
      await expect(dialog).toHaveCount(0)
    }
  })
}
