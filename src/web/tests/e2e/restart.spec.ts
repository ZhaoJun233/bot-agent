import { expect, test, type APIRequestContext, type Page } from '@playwright/test'
import { spawn, type ChildProcess } from 'node:child_process'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

/**
 * PNL-03：保存成功的配置在宿主进程重启后仍与已保存版本一致。
 *
 * 共享 webServer 由 Playwright 托管，用例里没法重启它，所以这里自起一对独立进程：
 * 专用数据目录的 Headless 宿主 + 指向它的 vite preview。宿主被强杀（不给优雅退出的机会）
 * 后用同一数据目录重启，页面刷新并重新认证，再核对字段值和版本。全部是合成数据。
 */

const webRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..')
const repoRoot = path.resolve(webRoot, '../..')
const headlessDll = process.env.BOTAGENT_HEADLESS_DLL
  ?? path.join(repoRoot, 'src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll')
const viteBin = path.join(webRoot, 'node_modules/vite/bin/vite.js')

const HOST_PORT = 18129
const WS_PORT = 13078
const PREVIEW_PORT = Number(process.env.BOTAGENT_WEB_TEST_PORT ?? 4174) + 2
const PREVIEW_URL = `http://127.0.0.1:${PREVIEW_PORT}`
const ADMIN_TOKEN = 'synthetic-web-admin-token'

const inheritedEnv = Object.fromEntries(
  Object.entries(process.env).filter(([key, value]) =>
    value !== undefined && !/^(BOTAGENT|QQCHAT|OPENAI|MINIMAX|TTS|HARNESS)_/i.test(key))
) as Record<string, string>

let dataDir = ''
let host: ChildProcess | undefined
let preview: ChildProcess | undefined

function hostEnv(): Record<string, string> {
  return {
    ...inheritedEnv,
    QQCHAT_DATA_DIR: dataDir,
    QQCHAT_API_KEY: 'sk-mock-restart',
    QQCHAT_BASE_URL: 'http://127.0.0.1:1/v1',
    QQCHAT_MODEL: 'mock-model',
    QQCHAT_ONEBOT_PROTOCOL: 'ReverseWebSocket',
    QQCHAT_ONEBOT_URL: `http://0.0.0.0:${WS_PORT}`,
    QQCHAT_UIN: '10001',
    QQCHAT_WHITELIST: '99999',
    QQCHAT_GROUP_COOLDOWN: '0',
    QQCHAT_IDLE_FALLBACK: '0',
    QQCHAT_HEALTH_PORT: String(HOST_PORT),
    QQCHAT_HEALTH_BIND: '127.0.0.1',
    QQCHAT_AGENT: '1',
    QQCHAT_AGENT_SERVER: '1',
    QQCHAT_AGENT_SERVER_URL: 'http://127.0.0.1:1/v1',
    QQCHAT_AGENT_SERVER_MODEL: 'mock-model',
    QQCHAT_AGENT_SERVER_TOOLS: '',
    QQCHAT_PANEL_TOKEN: 'synthetic-web-panel-token',
    BOTAGENT_ZBA_TOKEN: ADMIN_TOKEN,
    BOTAGENT_ZBA_READ_TOKEN: 'synthetic-web-read-token'
  }
}

async function waitFor(url: string, timeoutMs: number): Promise<void> {
  const deadline = Date.now() + timeoutMs
  let lastError = ''
  while (Date.now() < deadline) {
    try {
      const response = await fetch(url)
      if (response.ok) return
      lastError = `HTTP ${response.status}`
    } catch (error) {
      lastError = String(error)
    }
    await new Promise((resolve) => setTimeout(resolve, 250))
  }
  throw new Error(`等待 ${url} 超时：${lastError}`)
}

async function startHost(): Promise<void> {
  host = spawn('dotnet', [headlessDll], { cwd: repoRoot, env: hostEnv(), stdio: 'ignore' })
  await waitFor(`http://127.0.0.1:${HOST_PORT}/zba/v1/health`, 60_000)
}

async function killHost(): Promise<void> {
  const child = host
  host = undefined
  if (!child || child.exitCode !== null) return
  const exited = new Promise<void>((resolve) => child.once('exit', () => resolve()))
  child.kill('SIGKILL')
  await exited
}

async function readConfiguration(request: APIRequestContext): Promise<{
  version: string
  view: { behavior: Record<string, { value?: unknown }> }
}> {
  const response = await request.post(`${PREVIEW_URL}/zba/v1/configuration/read`, {
    data: { kind: 1 },
    headers: { Authorization: `Bearer ${ADMIN_TOKEN}` }
  })
  expect(response.ok()).toBeTruthy()
  return response.json()
}

async function openConfiguration(page: Page): Promise<void> {
  await page.getByTestId('token-input').fill(ADMIN_TOKEN)
  await page.getByTestId('connect-button').click()
  await expect(page.getByTestId('overview')).toBeVisible({ timeout: 15_000 })
  await page.getByTestId('nav-configuration').click()
  await expect(page.getByTestId('config-fields')).toBeVisible()
  await page.getByTestId('config-nav-behavior').click()
}

test.describe.configure({ mode: 'serial' })

test.beforeAll(async () => {
  if (!fs.existsSync(headlessDll)) throw new Error(`Headless DLL not found: ${headlessDll}`)
  dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'botagent-web-restart-'))
  await startHost()
  preview = spawn(process.execPath, [viteBin, 'preview', '--port', String(PREVIEW_PORT), '--strictPort'], {
    cwd: webRoot,
    env: { ...inheritedEnv, ZBA_PROXY_TARGET: `http://127.0.0.1:${HOST_PORT}` },
    stdio: 'ignore'
  })
  await waitFor(PREVIEW_URL, 30_000)
})

test.afterAll(async () => {
  await killHost()
  if (preview && preview.exitCode === null) {
    const exited = new Promise<void>((resolve) => preview!.once('exit', () => resolve()))
    preview.kill('SIGKILL')
    await exited
  }
  preview = undefined
  if (dataDir) fs.rmSync(dataDir, { recursive: true, force: true })
})

test('配置页：保存后强杀并重启宿主，刷新重新认证读回的仍是已保存版本（PNL-03）', async ({ page, request }) => {
  test.setTimeout(120_000)

  await page.goto(PREVIEW_URL)
  await openConfiguration(page)
  await page.getByTestId('config-enable-admin').click()
  await expect(page.getByTestId('config-mode-badge')).toHaveText('管理员模式')

  const input = page.getByTestId('config-field-privateCooldownSeconds').locator('input')
  const target = Number(await input.inputValue()) + 7
  await input.fill(String(target))
  await page.getByTestId('config-save').click()
  await expect(page.getByTestId('config-receipt')).toBeVisible({ timeout: 15_000 })
  await expect(page.getByTestId('config-save-bar')).toContainText('0 项修改')

  const saved = await readConfiguration(request)
  expect(saved.view.behavior.privateCooldownSeconds?.value).toBe(target)

  await killHost()
  await startHost()

  // 令牌只在内存：刷新后必须重新认证，且不会自动重发任何写请求。
  const writes: string[] = []
  page.on('request', (req) => {
    const pathname = new URL(req.url()).pathname
    if (req.method() === 'POST' && pathname.startsWith('/zba/v1/configuration/') && !pathname.endsWith('/read')) {
      writes.push(pathname)
    }
  })
  await page.reload()
  await expect(page.getByTestId('token-input')).toBeVisible()
  await openConfiguration(page)

  // 重新认证后处于标准模式：字段以只读文本呈现，这一步本身不可能发出写请求。
  await expect(page.getByTestId('config-mode-badge')).toHaveText('标准模式')
  await expect(page.getByTestId('config-field-privateCooldownSeconds')).toContainText(String(target))

  const reread = await readConfiguration(request)
  expect(reread.version).toBe(saved.version)
  expect(reread.view.behavior.privateCooldownSeconds?.value).toBe(target)
  expect(writes).toEqual([])
})
