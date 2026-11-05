import { expect, test, type Page } from '@playwright/test'
import type { PluginPresentationMetadata, ZbaBuiltinPlugin } from '../../src/api/plugin-presentation'

const presetNames = [
  ['music', '点歌'], ['voice', '语音'], ['stickers', '表情包'], ['poke', '戳一戳'],
  ['vibes', '氛围发言'], ['profiles', '成员档案'], ['websearch', '联网研究'], ['token-quota', 'Token 配额']
] as const
const hash = 'a'.repeat(64)
const manifest = {
  id: 'community.example.hello', version: '1.0.0', entryAssembly: 'HelloPlugin.dll',
  entryType: 'Example.HelloPlugin', sha256: hash, apiVersion: 1, targetFramework: 'net8.0',
  downloadUrl: 'https://github.com/example-owner/example-repo/releases/download/community.example.hello-1.0.0/community.example.hello-1.0.0.zip',
  permissions: [], dependencies: [], settings: []
}
function metadata(name: string, builtin = true): PluginPresentationMetadata {
  return {
    name, summary: '合成测试：中文能力介绍', description: '合成测试：完整功能说明', category: '功能扩展',
    author: '示例维护者', repository: 'https://github.com/example-owner/example-repo', license: 'MIT',
    usage: '目录展示用法，不执行外部插件。', configuration: '详细配置说明来自展示目录',
    compatibility: '兼容性为合成测试说明', changelog: '首次展示版本', maintenance: '测试数据维护',
    permissionNotes: '权限声明不是强制隔离', riskNotice: '不得把校验和视作安全保证', tags: ['中文测试'],
    runtime: builtin ? 'builtin' : 'dotnet', sourceKind: builtin ? 'builtin' : 'community',
    review: { status: 'unreviewed', version: '1.0.0', sha256: builtin ? null : hash }
  }
}
function builtins(local: boolean): ZbaBuiltinPlugin[] {
  return presetNames.map(([id, name]) => ({
    pluginId: 'preset.feature.' + id, version: '1.0.0', enabled: !local,
    metadata: { ...metadata(name), configuration: local ? '本机简略回退说明' : '详细配置说明来自展示目录' }
  }))
}
async function mockMarket(page: Page, offline = false): Promise<string[]> {
  const writes: string[] = []
  await page.route('**/zba/v1/plugins/**', async route => {
    const request = route.request()
    const pathname = new URL(request.url()).pathname
    if (request.method() !== 'GET') {
      writes.push(pathname)
      await route.fulfill({ status: 400, json: { error: { code: 'synthetic_write_forbidden' } } })
    } else if (pathname.endsWith('/catalog')) {
      await route.fulfill(offline
        ? { status: 503, json: { error: { code: 'service_unavailable', message: 'Synthetic catalog unavailable' } } }
        : { json: { revision: 'synthetic-rev', source: 'https://github.com/example-owner/example-repo',
          trustNotice: '不是沙箱，请核对风险。', categories: ['功能扩展'], builtinPlugins: builtins(false),
          plugins: [{ manifest, enabled: false, status: 'available', restartRequired: false, metadata: metadata('生命周期示例', false) }] } })
    } else if (pathname.endsWith('/installed')) {
      await route.fulfill({ json: { revision: 'synthetic-rev', plugins: [], builtinPlugins: builtins(true) } })
    } else await route.continue()
  })
  return writes
}
async function openMarket(page: Page): Promise<void> {
  await page.goto('/')
  await page.getByTestId('token-input').fill('synthetic-web-admin-token')
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
}

test('中文市场：九项统一展示、状态来自注册表、内置不走安装接口', async ({ page }, testInfo) => {
  const writes = await mockMarket(page)
  await openMarket(page)
  const rows = page.locator('.catalog-list .catalog-entry')
  await expect(rows).toHaveCount(9)
  await page.getByTestId('catalog-source').selectOption('builtin')
  await expect(rows).toHaveCount(8)
  await page.getByTestId('marketplace-search').fill('点歌')
  await expect(rows).toHaveCount(1)
  const detail = page.getByTestId('marketplace-detail')
  await expect(detail).toContainText('详细配置说明来自展示目录')
  await expect(page.getByTestId('marketplace-package-state')).toContainText('停用')
  await expect(page.getByTestId('review-badge')).not.toContainText('已审核')
  await expect(page.getByTestId('marketplace-install')).toHaveCount(0)
  await expect(page.getByTestId('marketplace-uninstall')).toHaveCount(0)
  await page.screenshot({ path: testInfo.outputPath('builtin-desktop.png'), fullPage: false })
  await page.getByTestId('configure-builtin').click()
  await expect(page.getByTestId('marketplace')).toHaveCount(0)
  expect(writes).toEqual([])
})

test('中文市场：远端目录离线仍可查看本机内置能力', async ({ page }) => {
  await mockMarket(page, true)
  await openMarket(page)
  await expect(page.locator('.catalog-list .catalog-entry')).toHaveCount(8)
  await expect(page.getByTestId('marketplace').getByRole('alert')).toBeVisible()
  await expect(page.getByTestId('marketplace-detail')).toContainText('本机简略回退说明')
  await expect(page.getByTestId('configure-builtin')).toBeEnabled()
})

test('中文市场：真实合成宿主注册表与 Wire 的八项内置可被客户端解析', async ({ page }) => {
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({
    status: 503, json: { error: { code: 'service_unavailable', message: 'Synthetic offline catalog' } }
  }))
  await openMarket(page)
  await expect(page.locator('.catalog-list .catalog-entry')).toHaveCount(8)
  await page.getByTestId('marketplace-search').fill('preset.feature.token-quota')
  await expect(page.locator('.catalog-list .catalog-entry')).toHaveCount(1)
  await expect(page.getByTestId('marketplace-detail')).toContainText('Token 配额')
  await expect(page.getByTestId('marketplace-install')).toHaveCount(0)
})

test('中文市场：移动端社区包保留未审核和执行风险提示', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const writes = await mockMarket(page)
  await openMarket(page)
  await page.getByTestId('catalog-source').selectOption('community')
  await expect(page.locator('.catalog-list .catalog-entry')).toHaveCount(1)
  const detail = page.getByTestId('marketplace-detail')
  await expect(detail).toContainText('生命周期示例')
  await expect(page.getByTestId('review-badge')).toContainText('未审核')
  await expect(detail).toContainText('不是沙箱')
  await expect(page.getByTestId('marketplace-install')).toBeDisabled()
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)).toBe(true)
  await detail.scrollIntoViewIfNeeded()
  await page.screenshot({ path: testInfo.outputPath('package-mobile.png'), fullPage: false })
  expect(writes).toEqual([])
})
