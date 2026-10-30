import { expect, test, type Locator, type Page } from '@playwright/test'

async function connect(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}

async function compactField(row: Locator, desktop: boolean): Promise<void> {
  await expect(row).toBeVisible()
  const control = row.locator('input, select').first()
  await expect(control).toBeVisible()
  const geometry = await row.evaluate(element => {
    const identity = element.querySelector('.setting-identity')!.getBoundingClientRect()
    const input = element.querySelector('input, select')!.getBoundingClientRect()
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
    await compactField(page.getByTestId('config-field-privateCooldownSeconds'), width > 600)
    const persona = page.getByTestId('config-field-botPersona').locator('input')
    await expect(persona).toBeVisible()
    expect((await persona.boundingBox())!.width).toBeLessThanOrEqual(640)
    await page.screenshot({ path: testInfo.outputPath('basic-density.png'), fullPage: true })

    await page.getByTestId('nav-capabilities').click()
    for (const [trigger, field] of [
      ['ability-settings-models', 'fastReply'],
      ['ability-settings-agent', 'enableServerAgent'],
      ['ability-settings-conversation', 'enableParticipationGating'],
      ['ability-settings-platform', 'officialEnabled'],
      ['ability-settings-reporting', 'healthReportEnabled'],
      ['cap-settings-plugin:preset.feature.voice', 'enableVoice'],
      ['cap-settings-plugin:preset.feature.music', 'enableMusic'],
      ['cap-settings-plugin:preset.feature.poke', 'enablePoke']
    ]) {
      await page.getByTestId(trigger!).click()
      const dialog = page.getByTestId('ability-configuration-dialog')
      await squareClose(dialog, width <= 640)
      const row = dialog.getByTestId('config-field-' + field)
      await compactField(row, width > 600)
      await row.locator('select').first().focus()
      await row.hover()
      const backgrounds = await row.locator('td').evaluateAll(cells => cells.map(cell => getComputedStyle(cell).backgroundColor))
      expect(backgrounds.every(color => color === 'rgba(0, 0, 0, 0)')).toBe(true)
      await page.screenshot({ path: testInfo.outputPath(trigger!.replace(/[^a-z-]/gi, '-') + '.png') })
      if (field === 'enablePoke') await row.screenshot({ path: testInfo.outputPath('poke-field.png') })
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }
    for (const [trigger, id] of [
      ['cap-settings-plugin:preset.feature.token-quota', 'token-quota-dialog'],
      ['cap-add-provider', 'provider-editor'],
      ['cap-marketplace', 'marketplace']
    ]) {
      await page.getByTestId(trigger!).click()
      const dialog = page.getByTestId(id!)
      await squareClose(dialog, width <= 640)
      if (id === 'provider-editor') {
        const priority = dialog.getByTestId('provider-priority')
        await expect(priority).toBeVisible()
        expect((await priority.boundingBox())!.width).toBeLessThanOrEqual(180)
      }
      await dialog.locator('.icon-close').click()
      await expect(dialog).toHaveCount(0)
    }
    const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth)
    expect(overflow).toBeLessThanOrEqual(1)
  })
}

test('all preset plugin cards expose visible switches; quota toggle persists with a restart receipt', async ({ page }, testInfo) => {
  await connect(page)
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId('cap-enable-admin').click()
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
    expect(await (await response).json()).toMatchObject({ code: 0, receipt: { commitState: 1, applyState: 3 } })
    await expect(toggle).toBeEnabled()
    await expect(toggle).toBeChecked({ checked: !original })
    await expect(card).toContainText('需重启')
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
      expect((await response).ok()).toBe(true)
      await expect(toggle).toBeEnabled()
      await expect(toggle).toBeChecked({ checked: original })
    }
  }
})
