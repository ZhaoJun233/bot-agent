import { expect, test } from '@playwright/test'

const READ = 'synthetic-web-read-token'

async function openTraces(page: import('@playwright/test').Page, token: string): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill(token)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-traces').click()
  await expect(page.getByTestId('traces')).toBeVisible()
}

test('轨迹页：只读令牌可打开，只展示形状且不含会话内容', async ({ page }) => {
  await openTraces(page, READ)
  await expect(page.locator('#traces-title')).toHaveText('轨迹与缓存')
  // 汇总卡：未知轮数单独一张，文案明确「供应商没报缓存用量的轮数」
  await expect(page.getByTestId('traces-unknown-turns')).toContainText('供应商没报缓存用量的轮数')
  // 明细可能是空态（新宿主没跑过轮次）也可能是列表；两者都接受
  await expect(
    page.getByTestId('traces-empty').or(page.getByTestId('trace-row').first())
  ).toBeVisible()
  // 页脚声明只读、只展示形状
  await expect(page.getByTestId('traces-footnote')).toContainText('只读')
  // 页面里不应出现任何会话正文/会话 key 入口
  await expect(page.getByTestId('traces').locator('a[href*="conversations"]')).toHaveCount(0)
})
