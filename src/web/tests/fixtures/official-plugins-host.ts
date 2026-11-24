import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'
import { fileURLToPath } from 'node:url'

const FEATURE_IDS = [
  'preset.feature.digest',
  'preset.feature.documents',
  'preset.feature.knowledge',
  'preset.feature.music',
  'preset.feature.personal-todo',
  'preset.feature.poke',
  'preset.feature.profiles',
  'preset.feature.scheduler',
  'preset.feature.stickers',
  'preset.feature.token-quota',
  'preset.feature.vibes',
  'preset.feature.voice',
  'preset.feature.websearch'
]

function resolveReleasesRoot(): string {
  const configured = process.env.BOTAGENT_OFFICIAL_PLUGINS_DIR?.trim()
  let marketplaceRoot: string
  if (configured) {
    marketplaceRoot = path.resolve(configured)
  } else {
    const here = path.dirname(fileURLToPath(import.meta.url))
    const repoRoot = path.resolve(here, '../../../..')
    marketplaceRoot = path.resolve(repoRoot, '../bot-agent-plugins')
  }
  const releasesRoot = path.join(marketplaceRoot, 'artifacts', 'official-releases')
  if (!fs.existsSync(releasesRoot)) {
    throw new Error(`Official plugin releases directory not found: ${releasesRoot}`)
  }
  return releasesRoot
}

/** Seeds all 13 official 1.1.0 feature plugins into a test data directory for full-feature Playwright specs. */
export function seedOfficialPlugins(dataDir: string): void {
  const releasesRoot = resolveReleasesRoot()
  const dataSub = path.join(dataDir, 'data')
  const pluginsDir = path.join(dataSub, 'plugins')
  fs.mkdirSync(pluginsDir, { recursive: true })

  const states: Record<string, boolean> = {}
  for (const id of FEATURE_IDS) {
    states[id] = true
    const pkgDir = path.join(pluginsDir, id)
    const installedPath = path.join(pkgDir, 'installed.json')
    if (fs.existsSync(installedPath)) continue

    let sourceDir = path.join(releasesRoot, id)
    if (!fs.existsSync(sourceDir)) sourceDir = path.join(releasesRoot, `${id}-1.1.0`)
    const manifestPath = path.join(sourceDir, 'manifest.json')
    const zipPath = path.join(sourceDir, `${id}-1.1.0.zip`)
    const manifestRaw = fs.readFileSync(manifestPath, 'utf8')
    const manifest = JSON.parse(manifestRaw) as {
      version?: string
      Version?: string
      sha256?: string
      Sha256?: string
    }
    const version = manifest.version ?? manifest.Version ?? '1.1.0'
    const shaLower = (manifest.sha256 ?? manifest.Sha256 ?? '').toLowerCase()
    if (!shaLower) throw new Error(`Missing sha256 in ${manifestPath}`)

    const releasesDir = path.join(pkgDir, 'releases')
    fs.mkdirSync(releasesDir, { recursive: true })
    fs.copyFileSync(zipPath, path.join(releasesDir, `${version}-${shaLower}.zip`))

    const executionText = JSON.stringify({
      id,
      active: manifest,
      previous: null,
      pending: null,
      pendingUninstall: false
    })
    const executionSha256 = createHash('sha256').update(executionText, 'utf8').digest('hex').toLowerCase()
    const envelopeText =
      `{"formatVersion":1,"minimumReaderVersion":12,"id":${JSON.stringify(id)},` +
      `"active":{"version":${JSON.stringify(version)},"sha256":${JSON.stringify(shaLower)}},` +
      `"previous":null,"pending":null,"pendingUninstall":false,` +
      `"executionSha256":${JSON.stringify(executionSha256)},"execution":${executionText}}`
    fs.writeFileSync(installedPath, envelopeText, 'utf8')
  }

  const db = new DatabaseSync(path.join(dataSub, 'botagent.db'))
  try {
    db.exec('CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)')
    db.prepare('INSERT OR IGNORE INTO meta(key, value) VALUES(?, ?)').run('plugins_state', JSON.stringify(states))
  } finally {
    db.close()
  }
}
