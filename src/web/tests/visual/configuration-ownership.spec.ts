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

    for (const tab of ['conversation', 'agent', 'reporting', 'system-packages'] as const) {
      await page.getByTestId('sysconfig-tab-' + tab).click()
      expect(await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)).toBeLessThanOrEqual(1)
      await page.screenshot({ path: path.join(out, width + '-' + theme + '-' + tab + '.png'), fullPage: true, animations: 'disabled' })
    }

    await page.getByTestId('nav-providers').click()
    await page.getByTestId('providers-routing-settings').click()
    {
      const dialog = page.getByTestId('ability-configuration-dialog')
      await expect(dialog.getByTestId('config-fields')).toBeVisible()
      await expect(dialog.getByTestId('config-field-model')).toHaveCount(0)
      await expect(dialog.getByTestId('config-field-modelBaseUrl')).toHaveCount(0)
      await expect(dialog.getByTestId('config-secrets')).toHaveCount(0)
      const box = await dialog.getByRole('dialog').boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(0)
      expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1)
      expect(await dialog.getByRole('dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
      await page.screenshot({ path: path.join(out, width + '-' + theme + '-models-dialog.png'), animations: 'disabled' })
      await dialog.getByTestId('ability-configuration-dialog-close').click()
      await expect(dialog).toHaveCount(0)
    }
    await page.getByTestId('cap-provider-edit-provider:primary').click()
    {
      const dialog = page.getByTestId('provider-editor')
      await expect(dialog.getByTestId('provider-primary-secret')).toBeVisible()
      const box = await dialog.getByRole('dialog').boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(0)
      expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1)
      expect(await dialog.getByRole('dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
      await page.screenshot({ path: path.join(out, width + '-' + theme + '-provider-primary-dialog.png'), animations: 'disabled' })
      await dialog.getByTestId('provider-editor-close').click()
      await expect(dialog).toHaveCount(0)
    }

    await page.getByTestId('nav-platforms').click()
    await expect(page.getByTestId('platforms-open-settings')).toHaveCount(0)
    for (const [driverId, suffix] of [['qq.private', 'qq-private'], ['feishu', 'feishu']] as const) {
      await page.getByTestId('platform-policy-' + driverId).click()
      const dialog = page.getByTestId('ability-configuration-dialog')
      await expect(dialog.getByTestId('config-fields')).toBeVisible()
      const box = await dialog.getByRole('dialog').boundingBox()
      expect(box!.x).toBeGreaterThanOrEqual(0)
      expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1)
      expect(await dialog.getByRole('dialog').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
      await page.screenshot({ path: path.join(out, width + '-' + theme + '-platform-' + suffix + '-dialog.png'), animations: 'disabled' })
      await dialog.getByTestId('ability-configuration-dialog-close').click()
      await expect(dialog).toHaveCount(0)
    }
  })
}
