-- Owner: host.token-usage. The quota policy does not own the metering schema.
CREATE TABLE token_usage(
  tenant_id TEXT PRIMARY KEY,
  used_prompt_tokens INTEGER NOT NULL DEFAULT 0 CHECK(used_prompt_tokens BETWEEN 0 AND 2147483647),
  used_completion_tokens INTEGER NOT NULL DEFAULT 0 CHECK(used_completion_tokens BETWEEN 0 AND 2147483647),
  reset_date TEXT NOT NULL DEFAULT '',
  updated_at TEXT NOT NULL DEFAULT (datetime('now'))
);
