import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const proxyTarget = process.env.ZBA_PROXY_TARGET ?? 'http://127.0.0.1:18127'

export default defineConfig({
  // 线上由 Caddy 在 /web/* 下静态承载（handle_path /web/*）。base 必须写在这里，
  // 不能靠构建参数：漏了 --base=/web/ 时资源会请求 /assets/*，被反代到后端 → 白屏（2026-10-27）。
  base: '/web/',
  plugins: [vue()],
  server: {
    host: '127.0.0.1',
    port: 4173,
    strictPort: true,
    proxy: {
      '/zba': {
        target: proxyTarget,
        changeOrigin: false
      }
    }
  },
  preview: {
    host: '127.0.0.1',
    port: 4174,
    strictPort: true,
    proxy: {
      '/zba': {
        target: proxyTarget,
        changeOrigin: false
      }
    }
  },
  build: {
    target: 'es2022',
    // Keep the favicon as a fingerprinted file instead of an inline data URI.
    assetsInlineLimit: 0,
    sourcemap: false
  }
})
