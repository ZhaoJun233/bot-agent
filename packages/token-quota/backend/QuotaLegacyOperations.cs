using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services.Ops;

namespace BotAgent.Packages.TokenQuota;

internal sealed partial class QuotaOperations
{
    public QuotaLegacyResponse LegacyGet(string? tenant, Func<string, bool> knownSource, Func<DateTimeOffset> utcNow)
    {
        tenant = tenant?.Trim();
        if (string.IsNullOrWhiteSpace(tenant)) return Error(400, "tenant is required");
        if (!knownSource(tenant)) return Error(404, "tenant is not registered");
        return Run(() => Response(200, LegacyPayload(ledger.GetQuota(tenant), utcNow)));
    }

    public QuotaLegacyResponse LegacySave(JsonElement body, Func<string, bool> knownSource, Func<string, string> mask,
        string auditActor, Action<AuditEvent>? appendAudit, Func<DateTimeOffset> utcNow)
    {
        if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("tenant", out var tenantValue)
            || tenantValue.ValueKind != JsonValueKind.String || !body.TryGetProperty("dailyTokenLimit", out var limitValue)
            || limitValue.ValueKind != JsonValueKind.Number || !limitValue.TryGetInt32(out var limit))
            return Error(400, "tenant and integer dailyTokenLimit are required");
        var tenant = tenantValue.GetString()!.Trim();
        if (tenant.Length == 0) return Error(400, "tenant is required");
        if (!knownSource(tenant)) return Error(404, "tenant is not registered");
        if (!TenantQuotaPolicy.IsValidDailyTokenLimit(limit)) return Response(400, new JsonObject
        {
            ["error"] = $"dailyTokenLimit must be between {TenantQuotaPolicy.MinimumDailyTokenLimit} and {TenantQuotaPolicy.MaximumDailyTokenLimit}",
            ["minimumDailyTokenLimit"] = TenantQuotaPolicy.MinimumDailyTokenLimit,
            ["maximumDailyTokenLimit"] = TenantQuotaPolicy.MaximumDailyTokenLimit
        });
        var (before, snapshot) = Run(() => ledger is QuotaLedger concrete
            ? concrete.SetDailyTokenLimitWithPrevious(tenant, limit)
            : (ledger.GetQuota(tenant), ledger.SetDailyTokenLimit(tenant, limit)));
        // The legacy endpoint retains its existing post-commit audit behavior.
        if (appendAudit is not null) appendAudit(new("config_change", auditActor, "panel", new JsonObject
        {
            ["result"] = "applied", ["resource"] = "tenant_quota", ["tenant"] = mask(tenant),
            ["oldDailyTokenLimit"] = before.DailyTokenLimit, ["newDailyTokenLimit"] = snapshot.DailyTokenLimit
        }.ToJsonString(), "2.1"));
        var payload = LegacyPayload(snapshot, utcNow);
        payload["ok"] = true;
        return Response(200, payload);
    }

    private static QuotaLegacyResponse Error(int status, string error) => Response(status, new JsonObject { ["error"] = error });
    private static QuotaLegacyResponse Response(int status, JsonObject body)
    {
        using var document = JsonDocument.Parse(body.ToJsonString());
        return new(status, document.RootElement.Clone());
    }
    private static JsonObject LegacyPayload(TenantQuotaSnapshot snapshot, Func<DateTimeOffset> utcNow)
    {
        var next = DateTimeOffset.TryParseExact(snapshot.ResetDate, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var date)
            ? date.AddDays(1) : new DateTimeOffset(utcNow().Date.AddDays(1), TimeSpan.Zero);
        var reason = snapshot.EnergySaving ? "quota_energy_saving" : null;
        return new JsonObject
        {
            ["tenant"] = snapshot.TenantId, ["dailyTokenLimit"] = snapshot.DailyTokenLimit,
            ["usedTokens"] = snapshot.UsedTotalTokens, ["usedPromptTokens"] = snapshot.UsedPromptTokens,
            ["usedCompletionTokens"] = snapshot.UsedCompletionTokens, ["remainingTokens"] = snapshot.RemainingTokens,
            ["resetDate"] = snapshot.ResetDate, ["nextResetAt"] = next.ToString("O", CultureInfo.InvariantCulture),
            ["energySaving"] = snapshot.EnergySaving, ["reason"] = reason, ["energySavingReason"] = reason,
            ["minimumDailyTokenLimit"] = TenantQuotaPolicy.MinimumDailyTokenLimit,
            ["maximumDailyTokenLimit"] = TenantQuotaPolicy.MaximumDailyTokenLimit
        };
    }
}
