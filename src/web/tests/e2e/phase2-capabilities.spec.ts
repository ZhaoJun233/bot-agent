import { expect, test, type APIRequestContext, type Page } from '@playwright/test'

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const READ_TOKEN = 'synthetic-web-read-token'
const adminHeaders = { Authorization: 'Bearer ' + ADMIN_TOKEN }
const readHeaders = { Authorization: 'Bearer ' + READ_TOKEN }

async function connectAdmin(page: Page): Promise<void> {
  await page.goto('/')
  const token = page.getByTestId('token-input')
  if (await token.isVisible()) {
    await token.fill(ADMIN_TOKEN)
    await page.getByTestId('connect-button').click()
  }
  await expect(page.getByTestId('overview')).toBeVisible()
}

async function resetDeclarativeSettings(request: APIRequestContext): Promise<void> {
  for (const [pluginId, defaults] of [
    ['system.platform-feishu', [
      { key: 'transportMode', kind: 1, value: 'webhook' },
      { key: 'enableInteractiveCards', kind: 1, value: 'true' }
    ]],
    ['preset.feature.voice', [
      { key: 'sttEnabled', kind: 1, value: 'false' },
      { key: 'sttApiKey', kind: 2, value: null }
    ]]
  ] as const) {
    const getRes = await request.get(`/zba/v1/plugins/${pluginId}/settings`, { headers: readHeaders })
    if (!getRes.ok()) continue
    const current = (await getRes.json()) as { revision: string }
    await request.put(`/zba/v1/plugins/${pluginId}/settings`, {
      headers: adminHeaders,
      data: {
        expectedRevision: current.revision,
        idempotencyKey: `e2e-reset-${pluginId}-${Date.now()}`,
        edits: defaults
      }
    })
  }
}

test('Phase 2: 19-package catalog, Feishu LongConnection hybrid settings, Voice STT secret safety, and 8 profile features', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await resetDeclarativeSettings(request)
  try {
    await connectAdmin(page)

    // 1. Plugin Center renders Documents & Digest cards
    await page.getByTestId('nav-plugins').click()
    await expect(page.getByTestId('plugins-page')).toBeVisible()
    const enablePluginsAdmin = page.getByTestId('plugins-enable-admin')
    if (await enablePluginsAdmin.isVisible()) {
      await enablePluginsAdmin.click()
    }
    await expect(page.locator('[data-plugin-id="preset.feature.documents"]')).toBeVisible()
    await expect(page.locator('[data-plugin-id="preset.feature.digest"]')).toBeVisible()

    // 2. Open Voice card configuration dialog -> verify embedded Cloud STT settings & write-only secret
    const voiceCard = page.locator('[data-plugin-id="preset.feature.voice"]')
    await expect(voiceCard).toBeVisible()
    await voiceCard.getByRole('button', { name: /设置/ }).click()

    const voiceDialog = page.getByTestId('ability-configuration-dialog')
    await expect(voiceDialog).toBeVisible()
    const embeddedVoice = voiceDialog.getByTestId('embedded-declared-plugin-settings')
    await expect(embeddedVoice).toBeVisible()
    await embeddedVoice.getByTestId('plugin-setting-sttEnabled').check()
    await embeddedVoice.getByTestId('plugin-setting-sttApiKey-mode').selectOption('set')
    await embeddedVoice.getByTestId('plugin-setting-sttApiKey').fill('sk-synthetic-e2e-stt-key')
    await embeddedVoice.getByTestId('embedded-plugin-settings-save').click()
    await expect(embeddedVoice).toContainText('扩展设置已保存')

    const voiceVerify = await request.get('/zba/v1/plugins/preset.feature.voice/settings', { headers: readHeaders })
    expect(voiceVerify.ok()).toBeTruthy()
    const voiceBodyText = await voiceVerify.text()
    expect(voiceBodyText).not.toContain('sk-synthetic-e2e-stt-key')
    const voiceJson = JSON.parse(voiceBodyText) as { values: Record<string, string>; configuredSecrets: Record<string, boolean> }
    expect(voiceJson.values.sttEnabled).toBe('true')
    expect(voiceJson.configuredSecrets.sttApiKey).toBe(true)

    // Verify dirty embedded settings block accidental close until confirmed
    await embeddedVoice.getByTestId('plugin-setting-sttEnabled').uncheck()
    await voiceDialog.getByRole('button', { name: '关闭' }).click()
    await expect(embeddedVoice.getByTestId('embedded-plugin-discard-confirmation')).toBeVisible()
    await embeddedVoice.getByTestId('embedded-plugin-discard-cancel').click()
    await expect(embeddedVoice.getByTestId('embedded-plugin-discard-confirmation')).toHaveCount(0)
    await expect(voiceDialog).toBeVisible()
    await voiceDialog.getByRole('button', { name: '关闭' }).click()
    await expect(embeddedVoice.getByTestId('embedded-plugin-discard-confirmation')).toBeVisible()
    await embeddedVoice.getByTestId('embedded-plugin-discard-confirm').click()
    await expect(voiceDialog).toHaveCount(0)

    // 3. Open Feishu platform drawer -> verify hybrid embedded LongConnection selector + 8 conversation profile features
    await page.getByTestId('nav-platforms').click()
    await expect(page.getByTestId('platforms-page')).toBeVisible()
    const enablePlatAdmin = page.getByTestId('platforms-enable-admin')
    if (await enablePlatAdmin.isVisible()) {
      await enablePlatAdmin.click()
    }
    await page.getByTestId('platform-policy-feishu').click()

    const feishuDialog = page.getByTestId('ability-configuration-dialog')
    await expect(feishuDialog).toBeVisible()
    const embeddedFeishu = feishuDialog.getByTestId('embedded-declared-plugin-settings')
    await expect(embeddedFeishu).toBeVisible()
    await embeddedFeishu.getByTestId('plugin-setting-transportMode').selectOption('long_connection')
    await embeddedFeishu.getByTestId('embedded-plugin-settings-save').click()
    await expect(embeddedFeishu).toContainText('扩展设置已保存')

    const feishuVerify = await request.get('/zba/v1/plugins/system.platform-feishu/settings', { headers: readHeaders })
    expect(feishuVerify.ok()).toBeTruthy()
    const feishuJson = (await feishuVerify.json()) as { values: Record<string, string> }
    expect(feishuJson.values.transportMode).toBe('long_connection')

    // Verify conversation profile editor inside Feishu drawer includes Documents (11) & Digest (12) features
    const profileEditor = feishuDialog.getByTestId('conversation-profile-editor')
    await profileEditor.getByTestId('conversation-profile-new-key').fill('feishu:group:oc_e2e_phase2')
    await profileEditor.getByTestId('conversation-profile-add-blank').click()
    const row = profileEditor.getByTestId('conversation-profile-row-feishu:group:oc_e2e_phase2')
    await expect(row).toContainText('文档解析')
    await expect(row).toContainText('会话纪要')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
  } finally {
    await resetDeclarativeSettings(request)
  }
})
