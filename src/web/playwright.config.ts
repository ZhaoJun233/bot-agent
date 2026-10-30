import { defineConfig, devices } from '@playwright/test'
import fs from 'node:fs'
import os from 'node:os'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const webRoot = path.dirname(fileURLToPath(import.meta.url))
const repoRoot = path.resolve(webRoot, '../..')
const headlessDll = process.env.BOTAGENT_HEADLESS_DLL
  ?? path.join(repoRoot, 'src/BotAgent.Headless/bin/Release/net8.0/BotAgent.Headless.dll')

if (!fs.existsSync(headlessDll)) {
  throw new Error(`Headless DLL not found: ${headlessDll}. Build BotAgent.Headless Release first.`)
}

// Override preview ports on hosts where Windows reserves the default range.
const previewPort = Number(process.env.BOTAGENT_WEB_TEST_PORT ?? 4174)
const noTokenPreviewPort = previewPort + 1
const hostPort = 18127
const wsPort = 13076
const noTokenHostPort = 18128
const noTokenWsPort = 13077
const dataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'botagent-web-s1-'))
const noTokenDataDir = fs.mkdtempSync(path.join(os.tmpdir(), 'botagent-web-s1-no-token-'))
const inheritedEnv = Object.fromEntries(
  Object.entries(process.env).filter(([key, value]) =>
    value !== undefined && !/^(BOTAGENT|QQCHAT|OPENAI|MINIMAX|TTS|HARNESS)_/i.test(key))
) as Record<string, string>

const hostEnv: Record<string, string> = {
  ...inheritedEnv,
  QQCHAT_DATA_DIR: dataDir,
  QQCHAT_API_KEY: 'sk-mock-s1',
  QQCHAT_BASE_URL: 'http://127.0.0.1:1/v1',
  QQCHAT_MODEL: 'mock-model',
  QQCHAT_ONEBOT_PROTOCOL: 'ReverseWebSocket',
  QQCHAT_ONEBOT_URL: `http://0.0.0.0:${wsPort}`,
  QQCHAT_UIN: '10001',
  QQCHAT_WHITELIST: '99999',
  QQCHAT_GROUP_COOLDOWN: '0',
  QQCHAT_IDLE_FALLBACK: '0',
  QQCHAT_HEALTH_PORT: String(hostPort),
  QQCHAT_HEALTH_BIND: '127.0.0.1',
  QQCHAT_AGENT: '1',
  QQCHAT_AGENT_SERVER: '1',
  QQCHAT_AGENT_SERVER_URL: 'http://127.0.0.1:1/v1',
  QQCHAT_AGENT_SERVER_MODEL: 'mock-model',
  QQCHAT_AGENT_SERVER_TOOLS: '',
  QQCHAT_PANEL_TOKEN: 'synthetic-web-panel-token',
  BOTAGENT_ZBA_TOKEN: 'synthetic-web-admin-token',
  BOTAGENT_ZBA_READ_TOKEN: 'synthetic-web-read-token'
}

const hostEnvNoToken: Record<string, string> = {
  ...hostEnv,
  QQCHAT_DATA_DIR: noTokenDataDir,
  QQCHAT_ONEBOT_URL: `http://0.0.0.0:${noTokenWsPort}`,
  QQCHAT_HEALTH_PORT: String(noTokenHostPort)
}
delete hostEnvNoToken.BOTAGENT_ZBA_TOKEN
delete hostEnvNoToken.BOTAGENT_ZBA_READ_TOKEN

// 进程级认证限流的用例（zz-rate-limit）会按远端地址封锁 1 分钟。
// 三个浏览器 project 共用同一批 webServer（同一个宿主进程），所以它必须
// 单独放到**最后**一个 project 里跑，否则封锁窗口会漏进下一个引擎的整批用例。
// 这不是浏览器差异，是测试之间互相污染。
const RATE_LIMIT_ONLY = /zz-rate-limit\.spec\.ts$/

export default defineConfig({
  testDir: './tests/e2e',
  timeout: 30_000,
  expect: { timeout: 8_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: `http://127.0.0.1:${previewPort}`,
    // Windows 上 `retain-on-failure` 的 trace 落盘会和 artifacts 目录清理抢同一批文件，
    // 偶发让**已经通过**的用例在 teardown 里以
    // `browserContext.close: ENOENT ... -recordingNNN.trace` 失败（S4-F5）。
    // 那不是断言失败，却会把整套矩阵染红。所以默认关掉，需要排查时用
    // `PW_TRACE=1` 显式打开——不靠 retries 掩盖，也不放宽任何断言。
    trace: process.env.PW_TRACE === '1' ? 'retain-on-failure' : 'off',
    screenshot: 'only-on-failure'
  },
  webServer: [
    {
      command: `dotnet "${headlessDll}"`,
      cwd: repoRoot,
      url: `http://127.0.0.1:${hostPort}/zba/v1/health`,
      timeout: 60_000,
      reuseExistingServer: false,
      env: hostEnv,
      stdout: 'pipe',
      stderr: 'pipe'
    },
    {
      command: `npm run preview -- --port ${previewPort} --strictPort`,
      cwd: webRoot,
      url: `http://127.0.0.1:${previewPort}`,
      timeout: 30_000,
      reuseExistingServer: false,
      env: {
        ...inheritedEnv,
        ZBA_PROXY_TARGET: `http://127.0.0.1:${hostPort}`
      },
      stdout: 'pipe',
      stderr: 'pipe'
    },
    {
      command: `dotnet "${headlessDll}"`,
      cwd: repoRoot,
      url: `http://127.0.0.1:${noTokenHostPort}/zba/v1/health`,
      timeout: 60_000,
      reuseExistingServer: false,
      env: hostEnvNoToken,
      stdout: 'pipe',
      stderr: 'pipe'
    },
    {
      command: `npm run preview -- --port ${noTokenPreviewPort} --strictPort`,
      cwd: webRoot,
      url: `http://127.0.0.1:${noTokenPreviewPort}`,
      timeout: 30_000,
      reuseExistingServer: false,
      env: {
        ...inheritedEnv,
        ZBA_PROXY_TARGET: `http://127.0.0.1:${noTokenHostPort}`
      },
      stdout: 'pipe',
      stderr: 'pipe'
    }
  ],
  projects: [
    // S4：跨浏览器矩阵。三个引擎跑同一套用例、同一套断言——
    // 不给某个引擎定制更宽松的通过标准，否则"矩阵"只是换个名字的单浏览器测试。
    {
      name: 'chromium',
      testIgnore: RATE_LIMIT_ONLY,
      use: { ...devices['Desktop Chrome'] }
    },
    {
      name: 'firefox',
      testIgnore: RATE_LIMIT_ONLY,
      use: { ...devices['Desktop Firefox'] }
    },
    {
      name: 'webkit',
      testIgnore: RATE_LIMIT_ONLY,
      use: { ...devices['Desktop Safari'] }
    },
    {
      name: 'chromium-rate-limit',
      testMatch: RATE_LIMIT_ONLY,
      use: { ...devices['Desktop Chrome'] }
    }
  ]
})
