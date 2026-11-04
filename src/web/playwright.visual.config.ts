import base from './playwright.config'
import { defineConfig, devices } from '@playwright/test'

// S5 视觉采集：复用 e2e 的合成宿主与静态预览，只跑 tests/visual，不并入 CI 矩阵。
export default defineConfig({
  ...base,
  testDir: './tests/visual',
  timeout: 60_000,
  projects: [{ name: 'visual-chromium', use: { ...devices['Desktop Chrome'] } }]
})
