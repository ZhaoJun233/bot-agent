-- Owner: preset.token-quota. Recovery unit: tenant_quotas + host token_usage.
-- The host creates token_usage first, in the same transaction.
CREATE TABLE IF NOT EXISTS tenant_quotas(
  tenant_id TEXT PRIMARY KEY, daily_token_limit INTEGER NOT NULL DEFAULT 50000,
  used_prompt_tokens INTEGER NOT NULL DEFAULT 0, used_completion_tokens INTEGER NOT NULL DEFAULT 0,
  reset_date TEXT NOT NULL DEFAULT '', energy_saving INTEGER NOT NULL DEFAULT 0,
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
INSERT INTO token_usage(tenant_id,used_prompt_tokens,used_completion_tokens,reset_date,updated_at)
  SELECT tenant_id,used_prompt_tokens,used_completion_tokens,reset_date,updated_at FROM tenant_quotas;
ALTER TABLE tenant_quotas DROP COLUMN used_prompt_tokens;
ALTER TABLE tenant_quotas DROP COLUMN used_completion_tokens;
ALTER TABLE tenant_quotas DROP COLUMN reset_date;
ALTER TABLE tenant_quotas DROP COLUMN energy_saving;
