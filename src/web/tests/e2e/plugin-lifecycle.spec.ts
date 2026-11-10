import { expect, test, type Page } from '@playwright/test'
import type { PluginPackageManifest } from '../../src/features/capabilities/plugin-manifest'

const ADMIN = 'synthetic-web-admin-token'
const READ = 'synthetic-web-read-token'
const ID = 'example.lifecycle'
const SOURCE = 'https://github.com/example-org/example-plugins'
const REVISION = 'synthetic-lifecycle-r1'
type Kind = 'update' | 'rollback' | 'uninstall'
interface Candidate { origin: 'catalog' | 'repository' | 'json'; manifest?: PluginPackageManifest; discoveryId?: string; version?: string }
interface PreviewRequest { kind: Kind; pluginId: string; expectedRevision: string; candidate?: Candidate }
interface Mutation { manifest?: PluginPackageManifest; expectedRevision: string; idempotencyKey: string; previewId: string; trustAcknowledged: boolean }

function manifest(version: string): PluginPackageManifest {
  return { id: ID, version, entryAssembly: 'Example.dll', entryType: 'Example.Plugin',
    sha256: (version === '2.0.0' ? 'b' : version === '0.5.0' ? 'c' : 'a').repeat(64),
    downloadUrl: SOURCE + '/releases/download/' + ID + '-' + version + '/' + ID + '-' + version + '.zip',
    apiVersion: 1, targetFramework: 'net8.0', minimumHostVersion: null,
    permissions: version === '2.0.0' ? ['network'] : [], dependencies: [], settings: [] }
}
const CURRENT = manifest('1.0.0')
const PREVIOUS = manifest('0.5.0')
const NEXT = manifest('2.0.0')
function changes(before: PluginPackageManifest, after: PluginPackageManifest | null) {
  const keys = Object.keys(before) as Array<keyof PluginPackageManifest>
  return keys.filter(key => JSON.stringify(before[key]) !== JSON.stringify(after?.[key]))
    .sort().map(field => ({ field, before: JSON.stringify(before[field]) ?? null, after: after ? JSON.stringify(after[field]) ?? null : null }))
}
async function openMarket(page: Page, admin = true) {
  await page.goto('/')
  await page.getByTestId('token-input').fill(admin ? ADMIN : READ)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible()
  await page.getByTestId('nav-capabilities').click()
  if (admin) await page.getByTestId('cap-enable-admin').click()
  await page.getByTestId('cap-marketplace').click()
  await expect(page.getByTestId('marketplace')).toBeVisible()
}
async function fakeLifecycleServer(page: Page, options: { blocked?: boolean; unknown?: boolean; restart?: boolean; failedPending?: boolean; failedInstall?: boolean } = {}) {
  const previews: PreviewRequest[] = []
  const writes: { kind: Kind; path: string; body: Mutation }[] = []
  const recoveries: string[] = []
  let current = options.failedInstall ? NEXT : CURRENT
  let previous: PluginPackageManifest | null = options.failedPending || options.failedInstall ? null : PREVIOUS
  let pending: PluginPackageManifest | null = options.failedPending || options.failedInstall ? NEXT : null
  let installed = true
  let revision = REVISION
  const entry = (candidate: PluginPackageManifest) => ({ manifest: candidate, installedVersion: installed && !options.failedInstall ? current.version : null,
    enabled: installed && !options.failedInstall, status: pending ? 'reload_required' : installed ? 'installed' : 'not_installed',
    restartRequired: false, previousManifest: previous, pendingManifest: pending })
  await page.route('**/zba/v1/plugins/catalog', route => route.fulfill({ json: {
    revision, source: SOURCE, trustNotice: '权限声明与 SHA-256 不是沙箱。', categories: [], builtinPlugins: [], plugins: [entry(NEXT)]
  } }))
  await page.route('**/zba/v1/plugins/installed', route => route.fulfill({ json: {
    revision, plugins: installed ? [entry(current)] : [], builtinPlugins: []
  } }))
  await page.route('**/zba/v1/plugins/lifecycle/preview', route => {
    const body: PreviewRequest = route.request().postDataJSON()
    previews.push(body)
    const target = body.kind === 'uninstall' ? null : body.kind === 'rollback' ? (options.failedPending ? current : previous) : body.candidate?.manifest ?? NEXT
    return route.fulfill({ json: {
      previewId: 'lifecycle-preview-' + previews.length, expiresAt: new Date(Date.now() + 600_000).toISOString(),
      revision, kind: body.kind, pluginId: ID, currentManifest: current, targetManifest: target, pendingManifest: pending,
      targetSource: target ? { origin: body.kind === 'rollback' ? 'installed' : body.candidate?.origin ?? 'catalog',
        url: body.kind === 'rollback' ? target.downloadUrl : body.candidate?.origin === 'json' ? null : SOURCE, revision: null } : null,
      metadata: null, enabled: !options.failedInstall, currentStatus: pending ? 'reload_required' : 'installed', restartRequired: false,
      canApply: !options.blocked, recovery: options.failedInstall ? 'failed-install' : options.failedPending ? 'failed-pending' : 'none',
      changes: changes(current, target), preservation: { settings: true, userData: true, enabledIntent: true },
      trustNotice: '保留原配置、用户数据和启用意图；插件在进程内运行，不是沙箱。',
      checks: [{ code: 'dependencies', status: options.blocked ? 'blocked' : 'passed', message: options.blocked ? '仍有依赖者固定当前版本，不能卸载或切换版本。' : '依赖与反向依赖已检查。' },
        { code: 'settings', status: 'passed', message: '现有配置兼容且保留，不回显任何当前值。' },
        { code: 'artifact', status: body.kind === 'uninstall' ? 'passed' : 'pending', message: '确认后验证固定包，预览不会执行插件。' }]
    } })
  })
  for (const kind of ['update', 'rollback', 'uninstall'] as const) {
    const path = kind === 'uninstall' ? '/plugins/' + ID + '/uninstall' : '/plugins/' + kind
    await page.route('**/zba/v1' + path, route => {
      const body: Mutation = route.request().postDataJSON()
      writes.push({ kind, path: new URL(route.request().url()).pathname, body })
      if (options.unknown) return route.abort('failed')
      if (kind === 'uninstall') installed = false
      else { if (!options.failedPending) previous = current; current = body.manifest!; pending = null }
      revision = 'synthetic-lifecycle-r2'
      return route.fulfill({ json: { revision, operation: { id: 'po-synthetic-lifecycle', pluginId: ID, kind,
        status: options.restart ? 'restart_required' : kind === 'uninstall' ? 'not_installed' : 'installed', restartRequired: !!options.restart } } })
    })
  }
  await page.route('**/zba/v1/plugins/operations/*', route => {
    const key = decodeURIComponent(new URL(route.request().url()).pathname.split('/').at(-1)!)
    recoveries.push(key)
    const write = writes.at(-1)!
    return route.fulfill({ json: { revision, operation: { id: 'po-synthetic-lifecycle',
      pluginId: recoveries.length === 1 ? 'example.wrong-target' : ID, kind: write.kind,
      status: 'failed', restartRequired: false, errorCode: recoveries.length < 3 ? 'plugin_operation_unknown' : 'plugin_reload_failed' } } })
  })
  return { previews, writes, recoveries }
}

test('生命周期：升级先显示完整差异，再提交固定确认', async ({ page }, testInfo) => {
  const calls = await fakeLifecycleServer(page)
  await openMarket(page)
  await page.getByTestId('marketplace-update').click()
  await expect(page.getByTestId('lifecycle-preview')).toBeVisible()
  await expect(page.getByTestId('lifecycle-changes')).toContainText('network')
  await expect(page.getByTestId('lifecycle-preview')).toContainText('1.0.0')
  await expect(page.getByTestId('lifecycle-preview')).toContainText('2.0.0')
  await expect(page.getByTestId('lifecycle-confirm')).toBeDisabled()
  expect(calls.writes).toHaveLength(0)
  expect(calls.previews[0]).toMatchObject({ kind: 'update', pluginId: ID, expectedRevision: REVISION, candidate: { origin: 'catalog', manifest: NEXT } })
  await page.getByTestId('lifecycle-acknowledge').check()
  await page.screenshot({ path: testInfo.outputPath('upgrade-preview.png') })
  await page.getByTestId('lifecycle-confirm').click()
  await expect.poll(() => calls.writes.length).toBe(1)
  expect(calls.writes[0]).toMatchObject({ kind: 'update', body: { previewId: 'lifecycle-preview-1', manifest: NEXT, trustAcknowledged: true, expectedRevision: REVISION } })
  await expect(page.getByTestId('marketplace-notice')).toBeVisible()
})

test('生命周期：回滚使用上一份固定清单并显示待重启回执', async ({ page }) => {
  const calls = await fakeLifecycleServer(page, { restart: true })
  await openMarket(page)
  await page.getByTestId('marketplace-rollback').click()
  await expect(page.getByTestId('lifecycle-preview')).toContainText('0.5.0')
  expect(calls.previews[0]).toEqual({ kind: 'rollback', pluginId: ID, expectedRevision: REVISION })
  await expect(page.getByTestId('lifecycle-confirm')).toBeDisabled()
  await page.getByTestId('lifecycle-acknowledge').check()
  await page.getByTestId('lifecycle-confirm').click()
  await expect.poll(() => calls.writes.length).toBe(1)
  expect(calls.writes[0]).toMatchObject({ kind: 'rollback', body: { previewId: 'lifecycle-preview-1', manifest: PREVIOUS, trustAcknowledged: true } })
  await expect(page.getByTestId('marketplace-notice')).toContainText('重启')
})

test('生命周期：首次升级失败显式恢复 Active，不虚构 Previous', async ({ page }) => {
  const calls = await fakeLifecycleServer(page, { failedPending: true })
  await openMarket(page)
  await page.getByTestId('marketplace-rollback').click()
  const preview = page.getByTestId('lifecycle-preview')
  await expect(preview).toBeVisible()
  await expect(preview).toContainText('2.0.0')
  await expect(preview).toContainText(NEXT.sha256)
  await expect(preview).toContainText('1.0.0')
  await expect(page.getByTestId('lifecycle-confirm')).toBeDisabled()
  expect(calls.writes).toHaveLength(0)
  await page.getByTestId('lifecycle-acknowledge').check()
  await page.getByTestId('lifecycle-confirm').click()
  await expect.poll(() => calls.writes.length).toBe(1)
  expect(calls.writes[0]).toMatchObject({ kind: 'rollback', body: {
    manifest: CURRENT, previewId: 'lifecycle-preview-1', expectedRevision: REVISION, trustAcknowledged: true,
  } })
})

test('生命周期：首次安装失败须确认卸载未生效候选', async ({ page }, testInfo) => {
  const calls = await fakeLifecycleServer(page, { failedInstall: true })
  await openMarket(page)
  await page.getByTestId('marketplace-uninstall').click()
  const preview = page.getByTestId('lifecycle-preview')
  await expect(preview).toBeVisible()
  await expect(preview).toContainText('未生效')
  await expect(preview).toContainText(NEXT.version)
  await expect(preview).toContainText(NEXT.sha256)
  await expect(page.getByTestId('lifecycle-confirm')).toBeDisabled()
  expect(calls.writes).toHaveLength(0)
  await page.screenshot({ path: testInfo.outputPath('failed-install-uninstall-preview.png'), fullPage: true })
  await page.getByTestId('lifecycle-acknowledge').check()
  await page.getByTestId('lifecycle-confirm').click()
  await expect.poll(() => calls.writes.length).toBe(1)
  expect(calls.writes[0]).toMatchObject({ kind: 'uninstall', path: '/zba/v1/plugins/' + ID + '/uninstall', body: {
    previewId: 'lifecycle-preview-1', expectedRevision: REVISION, trustAcknowledged: true,
  } })
  expect(calls.writes[0]!.body.manifest).toBeUndefined()
})

test('生命周期：卸载依赖阻断，移动端显示保留内容且不能确认', async ({ page }, testInfo) => {
  await page.setViewportSize({ width: 390, height: 844 })
  const calls = await fakeLifecycleServer(page, { blocked: true })
  await openMarket(page)
  await page.getByTestId('marketplace-uninstall').click()
  await expect(page.getByTestId('lifecycle-preview')).toContainText('依赖者')
  await expect(page.getByTestId('lifecycle-preview')).toContainText('保留')
  await expect(page.getByTestId('lifecycle-confirm')).toBeDisabled()
  expect(calls.writes).toHaveLength(0)
  await page.getByTestId('lifecycle-preview').scrollIntoViewIfNeeded()
  expect(await page.getByTestId('marketplace').evaluate(el => el.scrollWidth <= el.clientWidth + 1)).toBe(true)
  await page.screenshot({ path: testInfo.outputPath('uninstall-blocked-mobile.png') })
})

test('生命周期：卸载未知结果保留原键，错误目标回执不能解除恢复', async ({ page }) => {
  const calls = await fakeLifecycleServer(page, { unknown: true })
  await openMarket(page)
  await page.getByTestId('marketplace-uninstall').click()
  await page.getByTestId('lifecycle-acknowledge').check()
  await page.getByTestId('lifecycle-confirm').click()
  await expect(page.getByTestId('marketplace-recovery')).toBeVisible()
  expect(calls.writes).toHaveLength(1)
  expect(calls.writes[0]).toMatchObject({ kind: 'uninstall', body: { previewId: 'lifecycle-preview-1', trustAcknowledged: true, expectedRevision: REVISION } })
  const key = calls.writes[0]!.body.idempotencyKey
  await page.getByTestId('marketplace-recovery-check').click()
  await expect.poll(() => calls.recoveries.length).toBe(1)
  await expect(page.getByTestId('marketplace-recovery')).toBeVisible()
  await page.getByTestId('marketplace-recovery-check').click()
  await expect.poll(() => calls.recoveries.length).toBe(2)
  await expect(page.getByTestId('marketplace-recovery')).toBeVisible()
  await page.getByTestId('marketplace-recovery-check').click()
  await expect(page.getByTestId('marketplace-recovery')).toHaveCount(0)
  expect(calls.recoveries).toEqual([key, key, key])
  expect(calls.writes).toHaveLength(1)
})

test('生命周期：已安装 ID 的 JSON 走升级预览，修改草稿作废确认', async ({ page }) => {
  const calls = await fakeLifecycleServer(page)
  let installs = 0
  await page.route('**/zba/v1/plugins/install', route => { installs++; return route.abort() })
  await openMarket(page)
  await page.getByText('高级：粘贴技术清单 JSON', { exact: true }).click()
  await page.getByTestId('marketplace-json-input').fill(JSON.stringify(NEXT))
  await page.getByTestId('json-preview').click()
  await expect(page.getByTestId('lifecycle-preview')).toBeVisible()
  expect(calls.previews[0]).toMatchObject({ kind: 'update', candidate: { origin: 'json', manifest: NEXT } })
  await page.getByTestId('lifecycle-acknowledge').check()
  await page.getByTestId('marketplace-json-input').fill(JSON.stringify({ ...NEXT, permissions: ['network', 'files'] }))
  await expect(page.getByTestId('lifecycle-preview')).toHaveCount(0)
  expect(calls.writes).toHaveLength(0)
  expect(installs).toBe(0)
})

test('生命周期：真实合成宿主拒绝缺失预览，不预留写回执', async ({ request }) => {
  const headers = { Authorization: 'Bearer ' + ADMIN }
  const before = await request.get('/zba/v1/plugins/installed', { headers })
  expect(before.status()).toBe(200)
  const state = await before.json()
  for (const kind of ['update', 'rollback', 'uninstall'] as const) {
    const idempotencyKey = 'synthetic-m3-no-preview-' + kind
    const path = kind === 'uninstall' ? '/zba/v1/plugins/' + ID + '/uninstall' : '/zba/v1/plugins/' + kind
    const data = { ...(kind === 'uninstall' ? {} : { manifest: NEXT }), expectedRevision: state.revision, idempotencyKey, trustAcknowledged: true }
    const rejected = await request.post(path, { headers, data })
    expect(rejected.status()).toBe(409)
    expect((await rejected.json()).error.code).toBe('plugin_preview_required')
    const receipt = await request.get('/zba/v1/plugins/operations/' + idempotencyKey, { headers })
    expect(receipt.status()).toBe(404)
  }
  const denied = await request.post('/zba/v1/plugins/lifecycle/preview', {
    headers: { Authorization: 'Bearer ' + READ }, data: { kind: 'uninstall', pluginId: ID, expectedRevision: state.revision } })
  expect(denied.status()).toBe(403)
  const after = await request.get('/zba/v1/plugins/installed', { headers })
  expect(await after.json()).toEqual(state)
})