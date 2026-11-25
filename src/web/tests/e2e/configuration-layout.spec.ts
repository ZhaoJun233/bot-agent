import { expect, test, type Locator, type Page, type Response } from '@playwright/test'

async function expectHotApplied(page: Page, response: Response, enabled: boolean): Promise<void> {
  expect(response.status()).toBe(200)
  const saved = await response.json()
  expect(saved.code).toBe(0)
  expect(saved.errors).toEqual([])
  expect(saved.receipt).toMatchObject({
    commitState: 1, applyState: 1,
    steps: expect.arrayContaining([{ step: 7, state: 1 }])
  })
  expect(saved.receipt.key).toBe(response.request().postDataJSON().key)
  expect(saved.receipt.committedVersion).toBeTruthy()

  // Query the durable receipt independently of the save response.
  const headers = { Authorization: 'Bearer synthetic-web-admin-token' }
  const found = await page.request.post('/zba/v1/configuration/find', {
    headers, data: { key: saved.receipt.key }
  })
  expect(found.status()).toBe(200)
  expect(await found.json()).toMatchObject({ code: 0, receipt: saved.receipt })

  // Inspect the registry-backed runtime, not just the persisted switch value.
  await expect.poll(async () => {
    const runtime = await page.request.post('/zba/v1/capabilities/inspect', {
      headers, data: { key: { kind: 'plugin', key: 'preset.feature.token-quota' } }
    })
    expect(runtime.status()).toBe(200)
    const result = await runtime.json()
    expect(result.code).toBe(0)
    return result.descriptor?.runtimeState
  }).toBe(enabled ? 4 : 2) // Running / Disabled; no host restart.
}

async function connect(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}

async function compactField(row: Locator, desktop: boolean): Promise<void> {
  await expect(row).toBeVisible()
  const control = row.locator('input, select, button[role="switch"]').first()
  await expect(control).toBeVisible()
  const geometry = await row.evaluate(element => {
    const identity = element.querySelector('.setting-identity')!.getBoundingClientRect()
    const input = element.querySelector('input, select, button[role="switch"]')!.getBoundingClientRect()
    return { height: element.getBoundingClientRect().height, gap: input.x - identity.right,
      inputHeight: input.height, inputWidth: input.width, identityWidth: identity.width }
  })
  expect(geometry.height).toBeLessThan(desktop ? 150 : 260)
  expect(geometry.inputHeight).toBeLessThanOrEqual(46)
  expect(geometry.inputWidth).toBeLessThanOrEqual(240)
  if (desktop) {
    expect(geometry.gap).toBeLessThanOrEqual(24)
    expect(geometry.identityWidth).toBeLessThanOrEqual(220)
  }
  await expect(row).toContainText('来源')
  await expect(row).toContainText('生效')
  await expect(row).toContainText('规则')
}

async function squareClose(dialog: Locator, mobile: boolean): Promise<void> {
  const close = dialog.locator('.icon-close')
  await expect(close).toBeVisible()
  const box = await close.boundingBox()
  expect(box).not.toBeNull()
  expect(Math.abs(box!.width - box!.height)).toBeLessThanOrEqual(1)
  expect(box!.width).toBeGreaterThanOrEqual(mobile ? 44 : 36)
}

for (const width of [1850, 1024, 390]) {
  test('configuration density ' + width + ': basic and capability fields stay compact; dialog closes stay square', async ({ page }, testInfo) => {
    await page.setViewportSize({ width, height: 950 })
    await connect(page)
    await page.getByTestId('nav-configuration').click()
    await page.getByTestId('config-enable-admin').click()
    await compactField(page.getByTestId('config-field-aiModeEnabled'), width > 600)
    await compactField(page.getByTestId('config-field-groupInterjectionCooldownSeconds'), width > 600)
    const persona = page.getByTestId('config-field-botPersona').locator('textarea')
    await expect(persona).toBeVisible()
    expect((await persona.boundingBox())!.width).toBeLessThanOrEqual(640)
    await page.screenshot({ path: testInfo.outputPath('basic-density.png'), fullPage: true })

    // System configuration tabs (agent / conversation / reporting)
    for (const [tab, paneId, field] of [
      ['sysconfig-tab-agent', 'sysconfig-pane-agent', 'enableServerAgent'],
      ['sysconfig-tab-conversation', 'sysconfig-pane-conversation', 'enableProactive'],
      ['sysconfig-tab-reporting', 'sysconfig-pane-reporting', 'healthReportEnabled']
    ] as const) {
      await page.getByTestId(tab).click()
      const row = page.getByTestId(paneId).getByTestId('config-field-' + field)
      await compactField(row, width > 600)
    }

    // Domain drawers: Model Providers (routing + editor), Message Platforms (policy), Plugins (voice/music/poke/token-quota)
    await page.getByTestId('nav-providers').click()
    await page.getByTestId('providers-routing-settings').click()
    {
      const dialog = page.getByTestId('ability-configuration-dialog')
      await squareClose(dialog, width <= 640)
      const row = dialog.getByTestId('config-field-fastReply')
      await compactField(row, width > 600)
      await row.locator('input[role="switch"], button[role="switch"], select').first().focus()
      await row.hover()
      const backgrounds = await row.locator('td').evaluateAll(cells => cells.map(cell => getComputedStyle(cell).backgroundColor))
      expect(backgrounds.every(color => color === 'rgba(0, 0, 0, 0)')).toBe(true)
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }
    await page.getByTestId('cap-add-provider').click()
    {
      const dialog = page.getByTestId('provider-editor')
      await squareClose(dialog, width <= 640)
      const priority = dialog.getByTestId('provider-priority')
      await expect(priority).toBeVisible()
      expect(await priority.evaluate(element => (element as HTMLElement).offsetWidth)).toBeLessThanOrEqual(180)
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }

    await page.getByTestId('nav-platforms').click()
    await expect(page.getByTestId('platforms-open-settings')).toHaveCount(0)
    await page.getByTestId('platform-policy-qq.official').click()
    {
      const dialog = page.getByTestId('ability-configuration-dialog')
      await squareClose(dialog, width <= 640)
      await compactField(dialog.getByTestId('config-field-officialEnabled'), width > 600)
      await expect(dialog.getByTestId('config-field-feishuEnabled')).toHaveCount(0)
      await expect(dialog.getByTestId('config-secrets')).toHaveCount(0)
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }
    await page.getByTestId('platform-policy-feishu').click()
    {
      const dialog = page.getByTestId('ability-configuration-dialog')
      await squareClose(dialog, width <= 640)
      await compactField(dialog.getByTestId('config-field-feishuEnabled'), width > 600)
      await expect(dialog.getByTestId('config-field-officialEnabled')).toHaveCount(0)
      await expect(dialog.getByTestId('config-secrets')).toBeVisible()
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }

    await page.getByTestId('nav-plugins').click()
    for (const [trigger, field] of [
      ['cap-settings-plugin:preset.feature.voice', 'enableVoice'],
      ['cap-settings-plugin:preset.feature.music', 'enableMusic'],
      ['cap-settings-plugin:preset.feature.poke', 'enablePoke']
    ] as const) {
      await page.getByTestId(trigger).click()
      const dialog = page.getByTestId('ability-configuration-dialog')
      await squareClose(dialog, width <= 640)
      const row = dialog.getByTestId('config-field-' + field)
      await compactField(row, width > 600)
      await row.locator('input[role="switch"], button[role="switch"], select').first().focus()
      await row.hover()
      const backgrounds = await row.locator('td').evaluateAll(cells => cells.map(cell => getComputedStyle(cell).backgroundColor))
      expect(backgrounds.every(color => color === 'rgba(0, 0, 0, 0)')).toBe(true)
      await page.screenshot({ path: testInfo.outputPath(trigger.replace(/[^a-z-]/gi, '-') + '.png') })
      if (field === 'enablePoke') await row.screenshot({ path: testInfo.outputPath('poke-field.png') })
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }
    await page.getByTestId('cap-settings-plugin:preset.feature.token-quota').click()
    {
      const dialog = page.getByTestId('token-quota-dialog')
      await squareClose(dialog, width <= 640)
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(1)
  })
}

test('all preset plugin cards expose visible switches; quota toggle and restore hot-apply with durable receipts', async ({ page }, testInfo) => {
  await connect(page)
  await page.getByTestId('nav-plugins').click()
  await page.getByTestId('plugins-enable-admin').click()
  for (const key of ['music', 'voice', 'stickers', 'poke', 'vibes', 'profiles', 'websearch', 'token-quota']) {
    const toggle = page.getByTestId('cap-switch-plugin:preset.feature.' + key)
    await expect(toggle).toBeVisible()
    await expect(toggle).toBeEnabled()
    const box = await toggle.boundingBox()
    expect(box!.width).toBeGreaterThanOrEqual(30)
    expect(box!.height).toBeGreaterThanOrEqual(20)
    expect(box!.height).toBeLessThanOrEqual(24)
    expect(await toggle.evaluate(element => getComputedStyle(element).padding)).toBe('0px')
  }
  const card = page.getByTestId('cap-item-plugin:preset.feature.token-quota')
  const toggle = card.getByRole('switch')
  const original = await toggle.isChecked()
  try {
    const response = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/configuration/save')
    await toggle.setChecked(!original)
    await expectHotApplied(page, await response, !original)
    await expect(toggle).toBeEnabled()
    await expect(toggle).toBeChecked({ checked: !original })
    await expect(card).toContainText('已保存')
    await expect(card).not.toContainText('需重启')
    await card.screenshot({ path: testInfo.outputPath('quota-card.png') })
    await card.getByRole('button', { name: '设置 Token 配额', exact: true }).click()
    const dialog = page.getByTestId('token-quota-dialog')
    await expect(dialog.getByRole('switch')).toHaveAttribute('aria-checked', String(!original))
    await dialog.screenshot({ path: testInfo.outputPath('quota-switch-dialog.png') })
    await dialog.locator('.icon-close').click()
  } finally {
    if (await page.getByTestId('token-quota-dialog').count()) await page.getByTestId('token-quota-dialog-close').click()
    await expect(toggle).toBeEnabled()
    if ((await toggle.isChecked()) !== original) {
      const response = page.waitForResponse(r => new URL(r.url()).pathname === '/zba/v1/configuration/save')
      await toggle.setChecked(original)
      await expectHotApplied(page, await response, original)
      await expect(card).toContainText('已保存')
      await expect(card).not.toContainText('需重启')
      await expect(toggle).toBeEnabled()
      await expect(toggle).toBeChecked({ checked: original })
    }
  }
})
