using System.Globalization;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services;

namespace BotAgent.Adapters.Persistence;

/// <summary>
/// 租户配额台账：SQLite 的 tenant_quotas 表。
/// 每日重置用量，超出配额后进入单租户节能静默模式，不影响其他租户。
/// </summary>
public class TenantQuotaStore : ITenantQuotaLedger
{
    private readonly object _gate = new();

    void ITenantQuotaLedger.RecordUsage(string tenantId, int promptTokens, int completionTokens)
        => RecordUsage(tenantId, promptTokens, completionTokens);

    public TenantQuotaSnapshot GetQuota(string tenantId)
    {
        var tenant = NormalizeTenant(tenantId);
        var today = Clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        lock (_gate)
        {
            var row = AppDatabase.Query(
                "SELECT tenant_id, daily_token_limit, used_prompt_tokens, used_completion_tokens, reset_date, energy_saving " +
                "FROM tenant_quotas WHERE tenant_id = $id",
                r => new TenantQuotaSnapshot(
                    r.GetString(0),
                    r.GetInt32(1),
                    r.GetInt32(2),
                    r.GetInt32(3),
                    r.GetString(4),
                    r.GetInt32(5) != 0),
                ("$id", tenant)).FirstOrDefault();

            if (row.TenantId is null)
            {
                var initial = new TenantQuotaSnapshot(tenant, TenantQuotaPolicy.DefaultDailyTokenLimit, 0, 0, today, false);
                Save(initial);
                return initial;
            }

            if (!string.Equals(row.ResetDate, today, StringComparison.Ordinal))
            {
                var reset = row with
                {
                    ResetDate = today,
                    UsedPromptTokens = 0,
                    UsedCompletionTokens = 0,
                    EnergySaving = false
                };
                Save(reset);
                return reset;
            }

            return row;
        }
    }

    public TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit)
    {
        if (!TenantQuotaPolicy.IsValidDailyTokenLimit(dailyTokenLimit))
        {
            throw new ArgumentOutOfRangeException(
                nameof(dailyTokenLimit),
                dailyTokenLimit,
                $"DailyTokenLimit must be between {TenantQuotaPolicy.MinimumDailyTokenLimit} and {TenantQuotaPolicy.MaximumDailyTokenLimit}.");
        }

        var tenant = NormalizeTenant(tenantId);
        lock (_gate)
        {
            var quota = GetQuota(tenant);
            var updated = quota with
            {
                DailyTokenLimit = dailyTokenLimit,
                EnergySaving = quota.UsedTotalTokens >= dailyTokenLimit
            };
            Save(updated);
            return updated;
        }
    }

    public TenantQuotaSnapshot RecordUsage(string tenantId, int promptTokens, int completionTokens)
    {
        var tenant = NormalizeTenant(tenantId);
        lock (_gate)
        {
            var quota = GetQuota(tenant);
            var newPrompt = checked(quota.UsedPromptTokens + Math.Max(0, promptTokens));
            var newCompletion = checked(quota.UsedCompletionTokens + Math.Max(0, completionTokens));
            var total = (long)newPrompt + newCompletion;
            var energySaving = total >= quota.DailyTokenLimit;

            var updated = quota with
            {
                UsedPromptTokens = newPrompt,
                UsedCompletionTokens = newCompletion,
                EnergySaving = energySaving
            };
            Save(updated);
            return updated;
        }
    }

    public bool IsEnergySaving(string tenantId) => GetQuota(tenantId).EnergySaving;

    private void Save(TenantQuotaSnapshot quota)
    {
        AppDatabase.Write(conn => AppDatabase.Exec(conn, """
            INSERT INTO tenant_quotas
              (tenant_id, daily_token_limit, used_prompt_tokens, used_completion_tokens, reset_date, energy_saving, updated_at)
            VALUES ($id, $limit, $prompt, $completion, $reset, $energy, datetime('now'))
            ON CONFLICT(tenant_id) DO UPDATE SET
              daily_token_limit = excluded.daily_token_limit,
              used_prompt_tokens = excluded.used_prompt_tokens,
              used_completion_tokens = excluded.used_completion_tokens,
              reset_date = excluded.reset_date,
              energy_saving = excluded.energy_saving,
              updated_at = excluded.updated_at;
            """,
            ("$id", quota.TenantId),
            ("$limit", quota.DailyTokenLimit),
            ("$prompt", quota.UsedPromptTokens),
            ("$completion", quota.UsedCompletionTokens),
            ("$reset", quota.ResetDate),
            ("$energy", quota.EnergySaving ? 1 : 0)));
    }

    private static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "default:general" : tenantId.Trim();
}
