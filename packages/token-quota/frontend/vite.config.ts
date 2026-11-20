import { defineConfig, normalizePath, type Plugin } from 'vite'
import vue from '@vitejs/plugin-vue'

function ownership(): Plugin {
  let root = ''
  return {
    name: 'quota-ui-ownership',
    configResolved(config) { root = normalizePath(config.root) },
    generateBundle(_, bundle) {
      const modules = new Set<string>()
      for (const id of this.getModuleIds()) {
        if (id === 'vue') continue
        if (id.startsWith(String.fromCharCode(0))) continue
        const path = normalizePath(id).split('?')[0]!
        if (!path.startsWith(root + '/')) this.error('Private source outside quota UI: ' + id)
        modules.add(path.slice(root.length + 1))
      }
      for (const chunk of Object.values(bundle)) {
        if (chunk.type !== 'chunk') continue
        for (const dependency of [...chunk.imports, ...chunk.dynamicImports])
          if (dependency !== 'vue' && !Object.hasOwn(bundle, dependency)) this.error('Unexpected external dependency: ' + dependency)
      }
      this.emitFile({ type: 'asset', fileName: 'module-ownership.json',
        source: JSON.stringify({ modules: [...modules].sort(), external: ['vue'] }, null, 2) })
    }
  }
}

export default defineConfig({
  plugins: [vue(), ownership()],
  resolve: { dedupe: ['vue'] },
  build: {
    target: 'es2022',
    lib: { entry: 'index.ts', formats: ['es'], fileName: 'token-quota' },
    rollupOptions: { external: ['vue'] }
  }
})
