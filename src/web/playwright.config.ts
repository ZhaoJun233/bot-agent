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

export default defineConfig({
  testDir: './tests/e2e',
  timeout: 30_000,
  expect: { timeout: 8_000 },
  fullyParallel: false,
  workers: 1,
  reporter: [['list']],
  use: {
    baseURL: 'http://127.0.0.1:4174',
    trace: 'retain-on-failure',
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
      command: 'npm run preview -- --port 4174 --strictPort',
      cwd: webRoot,
      url: 'http://127.0.0.1:4174',
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
      command: 'npm run preview -- --port 4175 --strictPort',
      cwd: webRoot,
      url: 'http://127.0.0.1:4175',
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
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] }
    }
  ]
})
