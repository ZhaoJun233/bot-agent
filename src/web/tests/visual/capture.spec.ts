import { expect, test, type Page } from '@playwright/test'
import fs from 'node:fs'
import path from 'node:path'

// S5 视觉采集：只截图，不断言外观。合成宿主、合成令牌；输出目录由 VISUAL_OUT 指定。
const OUT = process.env.VISUAL_OUT ?? path.resolve('test-results/visual')
const ADMIN_TOKEN = 'synthetic-web-admin-token'
const VIEWPORTS = [
  { name: 'desktop', width: 1440, height: 900 },
  { name: 'mobile', width: 390, height: 844 }
]

async function shot(page: Page, name: string): Promise<void> {
  fs.mkdirSync(OUT, { recursive: true })
  await page.waitForTimeout(400)
  await page.screenshot({ path: path.join(OUT, name + '.png'), fullPage: true })
}

for (const vp of VIEWPORTS) {
  test('capture ' + vp.name, async ({ page }) => {
    await page.setViewportSize({ width: vp.width, height: vp.height })
    await page.goto('/')
    await expect(page.getByTestId('auth-gate')).toBeVisible()
    await shot(page, vp.name + '-01-auth')

    await page.getByTestId('token-input').fill(ADMIN_TOKEN)
    await page.getByTestId('connect-button').click()
    await expect(page.getByTestId('overview')).toBeVisible()
    await page.getByTestId('enable-admin').click()
    await expect(page.getByTestId('mode-badge')).toHaveText('管理员模式')
    await shot(page, vp.name + '-02-overview')

    for (const [id, label] of [['nav-capabilities', '03-capabilities'], ['nav-configuration', '04-configuration'], ['nav-tasks', '05-tasks']] as const) {
      await page.getByTestId(id).click()
      await page.waitForLoadState('networkidle')
      await shot(page, vp.name + '-' + label)
    }
  })
}
