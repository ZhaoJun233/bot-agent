import { expect, test } from '@playwright/test'

/**
 * 认证失败限流是**进程级**状态：同一远端地址在 1 分钟窗口内失败 10 次后，
 * 后续所有请求都会 429（包括正确令牌）。
 *
 * 因此这个用例必须排在整套的最后（文件名前缀 zz-），
 * 否则它会把后面的用例一起封掉——那是测试互相污染，不是产品缺陷。
 */
test('连续错误令牌触发 429，页面不伪装成功', async ({ page }) => {
  await page.goto('/')

  for (let index = 0; index < 10; index += 1) {
    const response = await page.request.get('/zba/v1/status', {
      headers: { Authorization: `Bearer synthetic-bad-${index}` }
    })
    expect(response.status()).toBe(401)
  }

  await page.getByTestId('token-input').fill('synthetic-bad-after-limit')
  await page.getByTestId('connect-button').click()

  await expect(page.getByTestId('auth-gate')).toBeVisible()
  await expect(page.getByTestId('error-notice')).toContainText('次数过多')
  await expect(page.getByTestId('error-notice')).toContainText('too_many_authentication_failures')
  await expect(page.getByTestId('overview')).toHaveCount(0)
})
