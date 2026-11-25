import fs from 'node:fs'
import path from 'node:path'
import { createHash } from 'node:crypto'
import { DatabaseSync } from 'node:sqlite'

/** A new synthetic database; never touches application/user directories. */
export function seedOperationRecovery(dataDir: string): void {
  const plugin = path.join(dataDir, 'data/plugins/synthetic.broken')
  fs.mkdirSync(plugin, { recursive: true })
  fs.writeFileSync(path.join(plugin, 'installed.json'), '{synthetic broken metadata')
  const hash = (value: string) => createHash('sha256').update(value).digest('hex').toUpperCase()
  const db = new DatabaseSync(path.join(dataDir, 'data/botagent.db'))
  try {
    db.exec('CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL)')
    const put = db.prepare('INSERT INTO meta(key,value) VALUES(?,?)')
    const owner = 'preset.feature.token-quota', scope = hash('zba:admin')
    put.run('quota:receipts:' + scope, JSON.stringify({ [hash('synthetic-legacy-key')]: {
      Fingerprint: hash('synthetic-request'), Result: { futureOpaqueBusiness: 'synthetic-private-result' }
    } }))
    const key = hash('synthetic-unknown-key')
    put.run('operation-recovery:v1:' + hash(owner) + ':' + scope + ':' + key, JSON.stringify({
      FormatVersion: 1, Owner: owner, ScopeDigest: scope, KeyDigest: key, RequestDigest: hash('synthetic-request'),
      Contract: 'quota.management', ContractVersion: '1', Generation: 'synthetic-prior-generation', State: 'unknown', Provenance: 'recorded'
    }))
  } finally { db.close() }
}
