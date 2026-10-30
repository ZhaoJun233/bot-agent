import { expect, test, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'

const OUT = process.env.VISUAL_OUT ?? path.resolve('test-results/redesign')
const widths = [1440, 1024, 768, 390, 375, 320]

async function shot(page: Page, name: string): Promise<void> {
  fs.mkdirSync(OUT, { recursive: true })
  await page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: !name.includes('dialog') && !name.includes('marketplace'), animations: 'disabled' })
}
async function noOverflow(page: Page): Promise<void> {
  const dimensions = await page.evaluate(() => ({ width: document.documentElement.clientWidth, scroll: document.documentElement.scrollWidth }))
  expect(dimensions.scroll).toBeLessThanOrEqual(dimensions.width + 1)
}
async function dialogFits(page: Page): Promise<void> {
  const dialog = page.getByRole('dialog')
  await expect(dialog).toBeVisible()
  const box = await dialog.boundingBox()
  const vp = page.viewportSize()!
  expect(box).not.toBeNull()
  expect(box!.x).toBeGreaterThanOrEqual(0)
  expect(box!.y).toBeGreaterThanOrEqual(0)
  expect(box!.x + box!.width).toBeLessThanOrEqual(vp.width + 1)
  expect(box!.y + box!.height).toBeLessThanOrEqual(vp.height + 1)
}

for (const width of widths) {
  for (const theme of ['light', 'dark'] as const) {
    test('appearance ' + width + ' ' + theme, async ({ page }) => {
      await page.setViewportSize({ width, height: width < 640 ? 844 : 1000 })
      await page.addInitScript((value) => localStorage.setItem('botagent-appearance', value), theme)
      await page.goto('/web/')
      await expect(page.getByTestId('auth-gate')).toBeVisible()
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme)
      // Text/status colors are measured from the actual browser token cascade.
      const contrast = await page.evaluate(() => {
        const css = getComputedStyle(document.documentElement)
        function luminance(name: string): number {
          let hex = css.getPropertyValue(name).trim().slice(1)
          if (hex.length === 3) hex = [...hex].map(c => c + c).join('')
          const rgb = hex.match(/.{2}/g)!.map(c => parseInt(c, 16) / 255)
            .map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4)
          return .2126 * rgb[0]! + .7152 * rgb[1]! + .0722 * rgb[2]!
        }
        return [['--text', '--surface'], ['--text-muted', '--surface'], ['--text-soft', '--bg'],
          ['--accent-deep', '--accent-soft'], ['--on-accent', '--accent'],
          ['--ok-text', '--ok-bg'], ['--warn-text', '--warn-bg'], ['--bad-text', '--bad-bg']]
          .map(([fg, bg]) => { const a = luminance(fg!); const b = luminance(bg!); return { fg, bg, ratio: (Math.max(a, b) + .05) / (Math.min(a, b) + .05) } })
      })
      for (const pair of contrast) expect(pair.ratio, pair.fg + ' on ' + pair.bg).toBeGreaterThanOrEqual(4.5)
      const controlContrast = await page.evaluate(() => {
        const ctx = document.createElement('canvas').getContext('2d')!
        const css = getComputedStyle(document.documentElement)
        function lum(name: string): number {
          ctx.fillStyle = css.getPropertyValue(name).trim()
          const h = ctx.fillStyle.slice(1)
          const rgb = h.match(/.{2}/g)!.map(c => parseInt(c, 16) / 255).map(c => c <= .04045 ? c / 12.92 : ((c + .055) / 1.055) ** 2.4)
          return .2126 * rgb[0]! + .7152 * rgb[1]! + .0722 * rgb[2]!
        }
        const a = lum('--border-strong'), b = lum('--surface')
        return (Math.max(a, b) + .05) / (Math.min(a, b) + .05)
      })
      expect(controlContrast).toBeGreaterThanOrEqual(3)
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-01-auth')
      await page.getByTestId('token-input').fill('synthetic-web-admin-token')
      await page.getByTestId('connect-button').click()
      await expect(page.getByTestId('overview')).toBeVisible()
      await page.getByTestId('enable-admin').click()
      await expect(page.getByTestId('mode-badge')).toHaveText('管理员模式')
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-02-overview')

      await page.getByTestId('nav-configuration').click()
      await expect(page.getByTestId('config-fields')).toBeVisible()
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-03-configuration')
      await page.getByTestId('nav-capabilities').click()
      await page.getByTestId('ability-settings-models').click()
      await page.getByTestId('config-nav-registered').click()
      await expect(page.getByTestId('config-providers')).toBeVisible()
      await dialogFits(page)
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-04-providers-dialog')
      await page.getByTestId('ability-configuration-dialog-close').click()

      await page.getByTestId('nav-capabilities').click()
      await expect(page.locator('.cap-grid')).toBeVisible()
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-05-capabilities')
      const settings = page.getByTestId('cap-settings-plugin:preset.feature.music')
      await settings.click()
      await expect(page.getByTestId('ability-configuration-dialog').getByTestId('config-fields')).toBeVisible()
      await dialogFits(page)
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-05-plugin-dialog')
      await page.getByTestId('ability-configuration-dialog-close').click()
      await page.getByTestId('cap-search').fill('no-synthetic-match-zzz')
      await expect(page.getByTestId('cap-search-empty')).toBeVisible()
      await page.getByTestId('cap-search').fill('')
      await page.getByTestId('cap-add-provider').click()
      await expect(page.getByTestId('provider-name')).toBeVisible()
      await dialogFits(page)
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-06-provider-dialog')
      await page.getByTestId('provider-save').scrollIntoViewIfNeeded()
      await expect(page.getByTestId('provider-save')).toBeVisible()
      await page.keyboard.press('Escape')
      await expect(page.getByRole('dialog')).toHaveCount(0)

      // Marketplace responses are synthetic, with no external package downloads.
      await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({ json: { schemaVersion: 1, plugins: [] } }))
      await page.route('**/zba/v1/plugins/installed', route => route.fulfill({ json: { plugins: [] } }))
      await page.getByTestId('cap-marketplace').click()
      await expect(page.getByTestId('marketplace-source-input')).toBeVisible()
      await dialogFits(page)
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-07-marketplace')
      await page.getByTestId('marketplace-close').click()

      await page.getByTestId('nav-tasks').click()
      await expect(page.getByTestId('task-prompt')).toBeVisible()
      await noOverflow(page)
      await shot(page, width + '-' + theme + '-08-tasks')
      await page.getByTestId('theme-toggle').click()
      await expect(page.locator('html')).toHaveAttribute('data-theme', theme === 'light' ? 'dark' : 'light')
      await page.getByTestId('lock').click()
      await expect(page.getByTestId('auth-gate')).toBeVisible()
      await expect(page.getByTestId('token-input')).toHaveValue('')
    })
  }
}
