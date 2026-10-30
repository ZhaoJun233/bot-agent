import { expect, test } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'
import { quota } from '../fixtures/quotas'
const OUT = process.env.VISUAL_OUT ?? path.resolve('test-results/quotas')
for (const width of [1440, 1024, 768, 390, 375, 320]) {
  for (const theme of ['light', 'dark'] as const) {
    test('quota dialog ' + width + ' ' + theme, async ({ page }) => {
      await page.setViewportSize({ width, height: width < 640 ? 844 : 1000 })
      await page.addInitScript(value => localStorage.setItem('botagent-appearance', value), theme)
      await page.route('**/zba/v1/quotas', route => route.fulfill({ json: { items: [
        quota({ displayName: '群聊 100***01 · synthetic-long-display-label-for-wrapping', usedTokens: 2147483647, remainingTokens: 0, energySaving: true }),
        quota({ sourceKey: 'group:20002', displayName: '群聊 200***02' })
      ] } }))
      await page.goto('/web/')
      await page.getByTestId('token-input').fill('synthetic-web-admin-token')
      await page.getByTestId('connect-button').click()
      await expect(page.getByTestId('overview')).toBeVisible()
      await page.getByTestId('enable-admin').click()
      await expect(page.getByTestId('mode-badge')).toHaveText('管理员模式')
      await page.getByTestId('nav-capabilities').click()
      await page.getByTestId('cap-settings-plugin:preset.feature.token-quota').click()
      await expect(page.getByTestId('quota-source-0')).toContainText('节能模式')
      await expect(page.getByTestId('quota-plugin-toggle')).toBeEnabled()
      fs.mkdirSync(OUT, { recursive: true })
      await page.screenshot({ path: path.join(OUT, width + '-' + theme + '-quota-overview.png'), animations: 'disabled' })
      await page.getByTestId('quota-edit-0').click()
      await page.getByTestId('quota-limit').fill('120000')
      await page.getByTestId('quota-save').scrollIntoViewIfNeeded()
      const dialog = page.getByRole('dialog')
      const box = await dialog.boundingBox()
      expect(box).not.toBeNull(); expect(box!.x).toBeGreaterThanOrEqual(0); expect(box!.y).toBeGreaterThanOrEqual(0)
      expect(box!.x + box!.width).toBeLessThanOrEqual(width + 1)
      expect(box!.y + box!.height).toBeLessThanOrEqual(page.viewportSize()!.height + 1)
      const overflow = await dialog.evaluate(node => ({ client: node.clientWidth, scroll: node.scrollWidth }))
      expect(overflow.scroll).toBeLessThanOrEqual(overflow.client + 1)
      const pageOverflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
      expect(pageOverflow).toBeLessThanOrEqual(1)
      expect(await dialog.innerText()).not.toContain('group:10001')
      fs.mkdirSync(OUT, { recursive: true })
      await page.screenshot({ path: path.join(OUT, width + '-' + theme + '-quota-dialog.png'), animations: 'disabled' })
      await page.keyboard.press('Escape')
      await expect(page.getByTestId('quota-discard')).toBeVisible()
      await page.getByTestId('quota-discard-confirm').click()
      await expect(dialog).toHaveCount(0)
    })
  }
}
