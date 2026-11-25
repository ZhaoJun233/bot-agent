import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { randomUUID } from 'node:crypto'

const ADMIN_TOKEN = 'synthetic-web-admin-token'
const headers = { Authorization: 'Bearer ' + ADMIN_TOKEN }
const SYNTHETIC_KEY = 'group:10056'
const SYNTHETIC_LABEL = '合成协作群演示'
const FEISHU_KEY = 'feishu:group:oc_10056'
const FEISHU_LABEL = '合成飞书群演示'

interface ConfigReadResponse {
  version: string
  generation: string
  contractRevision: string
  view?: {
    registered?: {
      conversationProfiles?: Array<{
        key: { driver: { platformId: string; accountScope: string; instanceId: string }; conversationKey: string }
        conversationKeyDisplay: string
        displayLabel: string
        displayLabelRaw?: string
        pacing?: { groupInterjectionProbability?: number | null; proactiveEnabled?: number } | null
        capabilities?: { toolTier?: number } | null
      }>
    }
  }
}

async function readConfig(request: APIRequestContext, kind: 0 | 1): Promise<ConfigReadResponse> {
  const response = await request.post('/zba/v1/configuration/read', { headers, data: { kind } })
  expect(response.ok()).toBeTruthy()
  return response.json()
}

async function cleanupSyntheticProfile(request: APIRequestContext): Promise<void> {
  const latest = await readConfig(request, 1)
  const targets = (latest.view?.registered?.conversationProfiles ?? []).filter(
    (item) =>
      (item.key.driver.platformId === 'qq.private' && item.key.conversationKey === SYNTHETIC_KEY)
      || (item.key.driver.platformId === 'feishu' && item.key.conversationKey === FEISHU_KEY)
  )
  if (targets.length === 0) return
  const response = await request.post('/zba/v1/configuration/save', {
    headers,
    data: {
      contractRevision: latest.contractRevision,
      expectedVersion: latest.version,
      key: `cfg1:${latest.generation}:${Math.floor(Date.now() / 1000)}:${randomUUID().replace(/-/g, '')}`,
      changes: {
        registered: {
          plugins: [],
          providers: [],
          policies: [],
          conversationProfiles: targets.map((existing) => ({ key: existing.key, operation: 1, profile: null }))
        }
      },
      secrets: []
    }
  })
  expect(response.ok()).toBeTruthy()
}

async function connectAdmin(page: Page): Promise<void> {
  await page.goto('/')
  const token = page.getByTestId('token-input')
  if (await token.isVisible()) {
    await token.fill(ADMIN_TOKEN)
    await page.getByTestId('connect-button').click()
  }
  await expect(page.getByTestId('overview')).toBeVisible()
  const enableAdmin = page.getByTestId('enable-admin')
  if (await enableAdmin.isVisible()) {
    await enableAdmin.click()
    await expect(page.getByTestId('mode-badge')).toHaveText('管理员模式')
  }
}

test('conversation profile CRUD with preset, privacy projection, and conversation-tab summary jump', async ({ page, request }) => {
  await page.setViewportSize({ width: 1440, height: 900 })
  await cleanupSyntheticProfile(request)
  try {
    await connectAdmin(page)

    // 1. Open QQ 私域 "接入与策略" drawer
    await page.getByTestId('nav-platforms').click()
    await expect(page.getByTestId('platform-policy-qq.private')).toBeVisible()
    await page.getByTestId('platform-policy-qq.private').click()

    const editor = page.getByTestId('conversation-profile-editor')
    await expect(editor).toBeVisible()
    await expect(editor).toContainText('热更新 · 免重启')

    // 2. Reject mismatched platform prefix locally
    await editor.getByTestId('conversation-profile-new-key').fill('feishu:group:oc_1')
    await editor.getByTestId('conversation-profile-add-blank').click()
    await expect(editor.getByTestId('conversation-profile-add-error')).toBeVisible()

    // 3. Add a profile with the restrained-group preset and hot-save
    await editor.getByTestId('conversation-profile-new-key').fill(SYNTHETIC_KEY)
    await editor.getByTestId('conversation-profile-new-label').fill(SYNTHETIC_LABEL)
    await editor.getByTestId('conversation-profile-add-preset-restrained-group').click()

    const row = editor.getByTestId(`conversation-profile-row-${SYNTHETIC_KEY}`)
    await expect(row).toBeVisible()
    await expect(row.getByTestId(`conversation-profile-tier-${SYNTHETIC_KEY}`)).toHaveValue('2')

    const drawer = page.locator('[role="dialog"]').last()
    await drawer.getByTestId('config-save').click()
    await expect(drawer.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })

    // 4. Verify server Overview masks raw key & label across ENTIRE response (including runtimeEffects)
    const overview = await readConfig(request, 0)
    const overviewText = JSON.stringify(overview)
    expect(overviewText).not.toContain(SYNTHETIC_KEY)
    expect(overviewText).not.toContain(SYNTHETIC_LABEL)
    const overviewProfiles = overview.view?.registered?.conversationProfiles ?? []
    expect(overviewProfiles.length).toBeGreaterThanOrEqual(1)
    expect(overviewProfiles.some((item) => item.key.conversationKey === SYNTHETIC_KEY)).toBe(false)
    expect(overviewProfiles.every((item) => item.displayLabelRaw === undefined)).toBe(true)

    const editable = await readConfig(request, 1)
    const editableEntry = editable.view?.registered?.conversationProfiles?.find(
      (item) => item.key.conversationKey === SYNTHETIC_KEY
    )
    expect(editableEntry?.displayLabelRaw).toBe(SYNTHETIC_LABEL)
    expect(editableEntry?.pacing?.groupInterjectionProbability).toBe(0)
    expect(editableEntry?.pacing?.proactiveEnabled).toBe(2)
    expect(editableEntry?.capabilities?.toolTier).toBe(2)

    // After save + reload, raw label stays in input while legend uses masked display
    await expect(row.getByTestId(`conversation-profile-label-${SYNTHETIC_KEY}`)).toHaveValue(SYNTHETIC_LABEL)
    await expect(row.locator('legend')).not.toContainText(SYNTHETIC_KEY)
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)

    const shotDir = process.env.BOTAGENT_PROFILE_SHOT_DIR
    if (shotDir) {
      await editor.scrollIntoViewIfNeeded()
      await page.waitForTimeout(200)
      await page.screenshot({ path: `${shotDir}/profiles-1440-light.png` })
      await page.evaluate(() => { document.documentElement.dataset.theme = 'dark' })
      await page.waitForTimeout(200)
      await page.screenshot({ path: `${shotDir}/profiles-1440-dark.png` })
      await page.evaluate(() => { document.documentElement.dataset.theme = 'light' })
      await page.setViewportSize({ width: 390, height: 844 })
      await editor.scrollIntoViewIfNeeded()
      await page.waitForTimeout(200)
      expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
      await page.screenshot({ path: `${shotDir}/profiles-390-light.png` })
      await page.setViewportSize({ width: 1440, height: 900 })
    }

    // 5. Close drawer and verify SystemConfiguration → 对话编排 summary + jump to 消息平台
    await drawer.getByRole('button', { name: '关闭' }).click()
    await expect(drawer).toHaveCount(0)
    await page.getByTestId('nav-configuration').click()
    await page.getByTestId('sysconfig-tab-conversation').click()
    const summary = page.getByTestId('conversation-profiles-summary')
    await expect(summary).toBeVisible()
    await expect(summary).toContainText('qq.private')
    await summary.getByTestId('conversation-profiles-open-platforms').click()
    await expect(page.getByTestId('platform-policy-qq.private')).toBeVisible()
    await expect(page.locator('[role="dialog"]')).toHaveCount(0)

    // 6. Verify Feishu drawer saves with declared instanceId 'feishu-main' (regression for undeclared_platform_driver)
    await page.getByTestId('platform-policy-feishu').click()
    const feishuDrawer = page.locator('[role="dialog"]').last()
    const feishuEditor = feishuDrawer.getByTestId('conversation-profile-editor')
    await expect(feishuEditor).toBeVisible()
    await feishuEditor.getByTestId('conversation-profile-new-key').fill(FEISHU_KEY)
    await feishuEditor.getByTestId('conversation-profile-new-label').fill(FEISHU_LABEL)
    await feishuEditor.getByTestId('conversation-profile-add-preset-restrained-group').click()
    await feishuDrawer.getByTestId('config-save').click()
    await expect(feishuDrawer.getByTestId('config-save-bar')).toContainText('0 项修改', { timeout: 15_000 })

    const afterFeishu = await readConfig(request, 1)
    const feishuSaved = afterFeishu.view?.registered?.conversationProfiles?.find(
      (item) => item.key.conversationKey === FEISHU_KEY
    )
    expect(feishuSaved?.key.driver).toEqual({
      platformId: 'feishu',
      accountScope: 'default',
      instanceId: 'feishu-main'
    })
    expect(feishuSaved?.displayLabelRaw).toBe(FEISHU_LABEL)
  } finally {
    await cleanupSyntheticProfile(request)
  }
})
