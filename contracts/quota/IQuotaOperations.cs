using System.Globalization;
using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;
using BotAgent.Services.Ops;

namespace BotAgent.Domain.Ports;

public sealed record QuotaLegacyResponse(int Status, JsonElement Body);
public sealed class QuotaOperationException(int status, string code, string detail) : Exception(detail)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}

/// <summary>Authenticated HTTP business operations. Source/masking callbacks are used only during the call.</summary>
public interface IQuotaOperations : IPluginControlContribution
{
    ZbaQuotaPage List(string[] sourceKeys, Func<string, string> displayName);
    ZbaQuotaSaveResult Save(ZbaQuotaSaveRequest request, string callerScope, Func<string, bool> knownSource, Func<string, string> displayName);
    ZbaQuotaFindResult Find(string key, string callerScope, Func<string, string> displayName);
    QuotaLegacyResponse LegacyGet(string? tenant, Func<string, bool> knownSource, Func<DateTimeOffset> utcNow);
    QuotaLegacyResponse LegacySave(JsonElement body, Func<string, bool> knownSource, Func<string, string> mask,
        string auditActor, Action<AuditEvent>? appendAudit, Func<DateTimeOffset> utcNow);
}

/// <summary>Compatibility request schema, shared by old routing adapters and the package.</summary>
public static class QuotaRequestRules
{
    public static void ValidateSave(ZbaQuotaSaveRequest request, Action<string> validateKey)
    {
        if (string.IsNullOrWhiteSpace(request.SourceKey) || request.SourceKey.Length > 256
            || request.SourceKey.Trim() != request.SourceKey || request.SourceKey.Any(char.IsControl))
            throw new QuotaOperationException(400, "invalid_source_key", "Invalid quota source.");
        if (!long.TryParse(request.ExpectedRevision, NumberStyles.None, CultureInfo.InvariantCulture, out var revision) || revision < 0)
            throw new QuotaOperationException(400, "invalid_quota_revision", "Invalid quota revision.");
        validateKey(request.IdempotencyKey);
        if (!BotAgent.Domain.Ops.TenantQuotaPolicy.IsValidDailyTokenLimit(request.DailyTokenLimit))
            throw new QuotaOperationException(400, "invalid_daily_token_limit", "Invalid daily token limit.");
    }

    public static void ValidateKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length is < 8 or > 256
            || key.Any(c => c is < ' ' or > '~' || c == '/' || c == (char)92))
            throw new QuotaOperationException(400, "invalid_idempotency_key", "Invalid operation key.");
    }
}
