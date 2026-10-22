import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

const proxyTarget = process.env.ZBA_PROXY_TARGET ?? 'http://127.0.0.1:18127'

export default defineConfig({
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
    sourcemap: false
  }
})
