-- Owner: preset.token-quota. Recovery unit: tenant_quotas + host token_usage.
-- Restore from current counters, never from a database backup. Unknown dependants refuse.
CREATE TABLE temp.token_usage_recovery_guard(valid INTEGER NOT NULL CHECK(valid=1));
INSERT INTO token_usage_recovery_guard VALUES(
  (SELECT user_version FROM pragma_user_version)=15
  AND (SELECT group_concat(name, ',') FROM pragma_table_info('tenant_quotas'))
    = 'tenant_id,daily_token_limit,updated_at'
  AND (SELECT group_concat(name, ',') FROM pragma_table_info('token_usage'))
    = 'tenant_id,used_prompt_tokens,used_completion_tokens,reset_date,updated_at'
  AND (SELECT group_concat(type || ':' || pk, ',') FROM pragma_table_info('token_usage'))
    = 'TEXT:1,INTEGER:0,INTEGER:0,TEXT:0,TEXT:0'
  AND (SELECT group_concat(type || ':' || pk, ',') FROM pragma_table_info('tenant_quotas'))
    = 'TEXT:1,INTEGER:0,TEXT:0'
  AND NOT EXISTS(SELECT 1 FROM sqlite_master WHERE type IN ('trigger','view')
    AND (tbl_name IN ('tenant_quotas','token_usage')
      OR lower(sql) LIKE '%tenant_quotas%' OR lower(sql) LIKE '%token_usage%'))
  AND NOT EXISTS(SELECT 1 FROM sqlite_master m, pragma_foreign_key_list(m.name) f
    WHERE m.type='table' AND (f."table" IN ('tenant_quotas','token_usage')
      OR m.name IN ('tenant_quotas','token_usage')))
  AND NOT EXISTS(SELECT 1 FROM sqlite_master WHERE type='index' AND sql IS NOT NULL
    AND tbl_name='token_usage')
  AND NOT EXISTS(SELECT 1 FROM token_usage WHERE
    typeof(used_prompt_tokens)!='integer' OR used_prompt_tokens NOT BETWEEN 0 AND 2147483647
    OR typeof(used_completion_tokens)!='integer' OR used_completion_tokens NOT BETWEEN 0 AND 2147483647
    OR tenant_id IS NULL OR reset_date IS NULL OR updated_at IS NULL)
  AND NOT EXISTS(SELECT 1 FROM tenant_quotas WHERE
    typeof(daily_token_limit)!='integer' OR daily_token_limit NOT BETWEEN 1 AND 1000000000
    OR tenant_id IS NULL OR updated_at IS NULL)
);
ALTER TABLE tenant_quotas ADD COLUMN used_prompt_tokens INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tenant_quotas ADD COLUMN used_completion_tokens INTEGER NOT NULL DEFAULT 0;
ALTER TABLE tenant_quotas ADD COLUMN reset_date TEXT NOT NULL DEFAULT '';
ALTER TABLE tenant_quotas ADD COLUMN energy_saving INTEGER NOT NULL DEFAULT 0;
INSERT INTO tenant_quotas(tenant_id,daily_token_limit,updated_at)
  SELECT tenant_id,50000,updated_at FROM token_usage WHERE true
  ON CONFLICT(tenant_id) DO NOTHING;
UPDATE tenant_quotas SET
  used_prompt_tokens=token_usage.used_prompt_tokens,
  used_completion_tokens=token_usage.used_completion_tokens,
  reset_date=token_usage.reset_date,
  energy_saving=(token_usage.used_prompt_tokens+token_usage.used_completion_tokens >= daily_token_limit)
  FROM token_usage WHERE tenant_quotas.tenant_id=token_usage.tenant_id;
DROP TABLE token_usage_recovery_guard;
