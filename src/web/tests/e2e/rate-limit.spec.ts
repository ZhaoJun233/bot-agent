import { expect, test } from '@playwright/test'

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
