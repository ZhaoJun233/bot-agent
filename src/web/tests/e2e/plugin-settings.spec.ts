import { expect, test, type Page } from '@playwright/test'
// Keep this browser fixture standalone: the unit fixture imports JSON through Vite.
const syntheticSettings = [
  { key: 'caption', label: '标题', type: 'string', defaultValue: '示例' },
  { key: 'secret', label: '密钥', type: 'secret' }
]
const syntheticManifest = {
  id: 'example.plugin', version: '1.0.0', entryAssembly: 'Example.Plugin.dll', entryType: 'Example.Plugin',
  sha256: 'a'.repeat(64), downloadUrl: 'https://example.com/fixture.zip', apiVersion: 1,
  targetFramework: 'net10.0', permissions: ['network'], settings: syntheticSettings
}

async function connect(page: Page): Promise<void> {
  await page.goto('/web/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
}

test('基础配置切到模型供应商路由设置时保留基础草稿且不混入模块字段', async ({ page }) => {
  await connect(page)
  await page.getByTestId('nav-configuration').click()
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-nav-behavior')).toHaveCount(0)
  const basic = page.getByTestId('config-field-groupInterjectionCooldownSeconds').locator('input')
  const draft = String(Number(await basic.inputValue()) + 1)
  await basic.fill(draft)
  await page.getByTestId('nav-providers').click()
  await expect(page.getByTestId('config-leave-dialog')).toBeVisible()
  await page.getByTestId('config-leave-keep').click()
  await page.getByTestId('providers-routing-settings').click()
  const editor = page.getByTestId('ability-configuration-dialog')
  await expect(editor).toBeVisible()
  await expect(editor.getByTestId('config-field-fastReply')).toBeVisible()
  await expect(editor.getByTestId('config-field-model')).toHaveCount(0)
  await expect(editor.getByTestId('config-field-modelBaseUrl')).toHaveCount(0)
  await expect(editor.getByTestId('config-secrets')).toHaveCount(0)
  await expect(editor.getByTestId('config-field-groupInterjectionCooldownSeconds')).toHaveCount(0)
  await expect(page.getByTestId('nav-config-draft')).toHaveText('1')
  await page.getByTestId('ability-configuration-dialog-close').click()
  await page.getByTestId('nav-configuration').click()
  await expect(basic).toHaveValue(draft)
  await page.getByTestId('config-discard').click()
})

test('系统包与功能插件清晰分域；预设字段和懒加载配额均由同一设置入口打开', async ({ page }) => {
  await connect(page)
  await page.getByTestId('nav-configuration').click()
  await page.getByTestId('sysconfig-tab-system-packages').click()
  await expect(page.locator('[data-testid^="syspkg-row-system."]')).toHaveCount(9)
  await page.getByTestId('syspkg-jump-system.model-services').click()
  await expect(page.getByTestId('providers-page')).toBeVisible()

  await page.getByTestId('nav-plugins').click()
  await expect(page.getByTestId('plugins-grid')).toBeVisible()
  await page.getByTestId('plugins-search').fill('点歌')
  await expect(page.getByTestId('cap-item-plugin:preset.feature.music')).toBeVisible()
  await page.getByTestId('plugins-search').fill('')
  await page.getByTestId('cap-settings-plugin:preset.feature.music').click()
  await expect(page.getByTestId('ability-configuration-dialog')).toBeVisible()
  await expect(page.getByTestId('config-field-enableMusic')).toBeVisible()
  await expect(page.getByTestId('config-field-enableVoice')).toHaveCount(0)
  await page.getByTestId('ability-configuration-dialog-close').click()
  await page.getByTestId('cap-settings-plugin:preset.feature.voice').click()
  await expect(page.getByTestId('config-secrets').locator('li')).toHaveCount(1)
  await page.getByTestId('ability-configuration-dialog-close').click()
  await page.getByTestId('cap-settings-plugin:preset.feature.token-quota').click()
  await expect(page.getByTestId('token-quota-dialog')).toBeVisible()
  await expect(page.getByTestId('ability-configuration-dialog')).toHaveCount(0)
  await page.getByTestId('token-quota-dialog-close').click()
  await expect(page.getByTestId('token-quota-dialog')).toHaveCount(0)
})

test('市场仅交接设置入口，声明式设置在独立窄屏弹窗中保护草稿', async ({ page }, testInfo) => {
  const entry = { manifest: syntheticManifest, installedVersion: syntheticManifest.version,
    enabled: false, status: 'installed', restartRequired: false }
  await page.route('**/plugins/catalog', route => route.fulfill({ json: {
    revision: 'p1', source: 'synthetic', trustNotice: 'Synthetic only', categories: [], plugins: [entry]
  } }))
  await page.route('**/plugins/installed', route => route.fulfill({ json: { revision: 'p1', plugins: [entry] } }))
  let writes = 0
  await page.route('**/plugins/example.plugin/settings', route => {
    if (route.request().method() !== 'GET') writes++
    return route.fulfill({ json: { revision: 's1', schema: syntheticSettings,
      values: { caption: 'Synthetic caption' }, configuredSecrets: {}, restartRequired: false } })
  })
  await page.setViewportSize({ width: 390, height: 844 })
  await connect(page)
  await page.getByTestId('nav-plugins').click()
  await page.getByTestId('plugins-enable-admin').click()
  await page.getByTestId('plugins-tab-catalog').click()
  await expect(page.getByTestId('marketplace-settings')).toBeVisible()
  await expect(page.getByTestId('plugin-setting-caption')).toHaveCount(0)
  await page.getByTestId('marketplace-settings').click()
  const editor = page.getByTestId('declared-plugin-settings-dialog')
  await expect(editor).toBeVisible()
  await expect(page.getByRole('dialog')).toHaveCount(1)
  await expect(page.getByTestId('plugin-setting-caption')).toHaveValue('Synthetic caption')
  await page.getByTestId('plugin-setting-caption').fill('Synthetic retained draft')
  await page.getByTestId('declared-plugin-settings-dialog-close').click()
  await expect(page.getByTestId('declared-plugin-settings-discard-confirmation')).toBeVisible()
  await page.getByTestId('declared-plugin-settings-discard-cancel').click()
  await expect(page.getByTestId('plugin-setting-caption')).toHaveValue('Synthetic retained draft')
  const dialog = page.getByRole('dialog')
  expect(await dialog.evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('declared-settings-mobile.png'), animations: 'disabled' })
  await page.getByTestId('declared-plugin-settings-dialog-close').click()
  await page.getByTestId('declared-plugin-settings-discard-confirm').click()
  await expect(editor).toHaveCount(0)
  expect(writes).toBe(0)
})
