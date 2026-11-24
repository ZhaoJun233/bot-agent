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

async function resetKnowledgeSettings(request: APIRequestContext): Promise<void> {
  const getRes = await request.get('/zba/v1/plugins/preset.feature.knowledge/settings', { headers: readHeaders })
  if (!getRes.ok()) return
  const current = (await getRes.json()) as { revision: string }
  await request.put('/zba/v1/plugins/preset.feature.knowledge/settings', {
    headers: adminHeaders,
    data: {
      expectedRevision: current.revision,
      idempotencyKey: `e2e-p3-reset-kb-${Date.now()}`,
      edits: [
        { key: 'maxDocsPerScope', kind: 1, value: '256' },
        { key: 'maxChunkChars', kind: 1, value: '800' },
        { key: 'defaultTopK', kind: 1, value: '4' }
      ]
    }
  })
}

test('Phase 3: 20-package knowledge settings, unified-memory governance card, and 11 conversation profile features', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await resetKnowledgeSettings(request)
  try {
    await connectAdmin(page)

    // 1. Plugin Center renders 20th bundled package (preset.feature.knowledge) and opens declarative settings dialog
    await page.getByTestId('nav-plugins').click()
    await expect(page.getByTestId('plugins-page')).toBeVisible()
    const enablePluginsAdmin = page.getByTestId('plugins-enable-admin')
    if (await enablePluginsAdmin.isVisible()) {
      await enablePluginsAdmin.click()
    }
    const kbCard = page.locator('[data-plugin-id="preset.feature.knowledge"]')
    await expect(kbCard).toBeVisible()
    await expect(kbCard).toContainText('会话轻量知识库')
    await kbCard.getByRole('button', { name: /设置/ }).click()

    const kbDialog = page.getByTestId('declared-plugin-settings-dialog')
    await expect(kbDialog).toBeVisible()
    const declaredFields = kbDialog.getByTestId('marketplace-declared-settings')
    await expect(declaredFields).toBeVisible()
    await expect(declaredFields.getByTestId('plugin-setting-maxDocsPerScope')).toBeVisible()
    await expect(declaredFields.getByTestId('plugin-setting-maxChunkChars')).toBeVisible()
    await expect(declaredFields.getByTestId('plugin-setting-defaultTopK')).toBeVisible()
    await declaredFields.getByTestId('plugin-setting-defaultTopK').fill('6')
    await kbDialog.getByTestId('declared-plugin-settings-save').click()
    await expect(kbDialog.getByTestId('declared-plugin-settings-notice')).toContainText('设置已保存')

    const kbVerify = await request.get('/zba/v1/plugins/preset.feature.knowledge/settings', { headers: readHeaders })
    expect(kbVerify.ok()).toBeTruthy()
    const kbJson = (await kbVerify.json()) as { values: Record<string, string> }
    expect(kbJson.values.defaultTopK).toBe('6')
    await kbDialog.getByRole('button', { name: '关闭' }).click()
    await expect(kbDialog).toHaveCount(0)

    // 2. Settings -> conversation tab renders read-only UnifiedMemoryGovernanceCard with shape-only metrics
    await page.getByTestId('nav-configuration').click()
    await expect(page.getByTestId('sysconfig-tab-conversation')).toBeVisible()
    await page.getByTestId('sysconfig-tab-conversation').click()
    const govCard = page.getByTestId('unified-memory-governance-card')
    await expect(govCard).toBeVisible()
    await expect(govCard.getByTestId('umem-confirmed-count')).toBeVisible()
    await expect(govCard.getByTestId('umem-pending-count')).toBeVisible()
    await expect(govCard.getByTestId('umem-suppressed-count')).toBeVisible()
    await expect(govCard.getByTestId('umem-admin-status')).toBeVisible()
    await expect(govCard).toContainText('绝不提供他人候选或事实正文列表')

    // 3. Platforms -> Feishu drawer -> conversation profile editor shows Phase 3 features (13 knowledge, 14 episodes, 15 jargon)
    await page.getByTestId('nav-platforms').click()
    await expect(page.getByTestId('platforms-page')).toBeVisible()
    const enablePlatAdmin = page.getByTestId('platforms-enable-admin')
    if (await enablePlatAdmin.isVisible()) {
      await enablePlatAdmin.click()
    }
    await page.getByTestId('platform-policy-feishu').click()
    const feishuDialog = page.getByTestId('ability-configuration-dialog')
    await expect(feishuDialog).toBeVisible()
    const profileEditor = feishuDialog.getByTestId('conversation-profile-editor')
    await profileEditor.getByTestId('conversation-profile-new-key').fill('feishu:group:oc_e2e_phase3')
    await profileEditor.getByTestId('conversation-profile-add-blank').click()
    const row = profileEditor.getByTestId('conversation-profile-row-feishu:group:oc_e2e_phase3')
    await expect(row).toContainText('轻量知识库')
    await expect(row).toContainText('情景记忆')
    await expect(row).toContainText('圈子黑话')
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth + 1)).toBe(true)
  } finally {
    try { await resetKnowledgeSettings(request) } catch {}
  }
})
