import { createRequire } from 'node:module'

const require = createRequire(import.meta.url)
const { version } = require('typescript/package.json')
const major = Number(version.split('.')[0])

if (!Number.isInteger(major) || major < 5 || major >= 7) {
  console.error(
    `TypeScript ${version} is incompatible with the current Vue type checker. ` +
    'vue-tsc 3.x requires the classic TypeScript compiler (>=5 <7). ' +
    'Restore the pinned package.json and package-lock.json, then run npm ci. ' +
    'Upgrade TypeScript 7 together with its Vue type-checking toolchain.',
  )
  process.exit(1)
}

// Verify the compiler entry point used by vue-tsc, not just its package version.
require.resolve('typescript/lib/tsc')
