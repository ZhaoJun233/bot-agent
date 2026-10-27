import { expect, test } from '@playwright/test'

// 线上 Caddy 只在 /web/* 下静态承载面板（handle_path /web/*）。
// 2026-10-27 白屏：构建漏了 base，index.html 去请求 /assets/*，被反代到后端拿不到脚本。
// 这里在真实预览产物上断言：入口在 /web/，所有脚本/样式都从 /web/ 加载且成功。
test('面板产物挂在 /web/ 下，脚本与样式都从 /web/ 加载成功', async ({ page }) => {
  const failed: string[] = []
  const assets: string[] = []
  page.on('response', (res) => {
    const type = res.request().resourceType()
    if (type === 'script' || type === 'stylesheet') {
      assets.push(new URL(res.url()).pathname)
      if (!res.ok()) failed.push(`${res.status()} ${res.url()}`)
    }
  })
  page.on('requestfailed', (req) => failed.push(`failed ${req.url()}`))

  await page.goto('/')
  await expect(page).toHaveURL(/\/web\/$/)
  await expect(page.getByTestId('auth-gate')).toBeVisible()

  expect(failed).toEqual([])
  expect(assets.length).toBeGreaterThan(0)
  for (const p of assets) expect(p.startsWith('/web/assets/')).toBe(true)

  const refs = await page.evaluate(() => [
    ...Array.from(document.querySelectorAll('script[src]'), (el) => el.getAttribute('src') ?? ''),
    ...Array.from(document.querySelectorAll('link[href]'), (el) => el.getAttribute('href') ?? '')
  ].filter((v) => v.startsWith('/')))
  for (const r of refs) expect(r.startsWith('/web/')).toBe(true)
})
