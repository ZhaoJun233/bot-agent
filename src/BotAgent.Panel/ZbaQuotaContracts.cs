using System.Text.Json.Serialization;
namespace BotAgent.Panel;

public sealed record ZbaQuota(
    [property: JsonRequired] string SourceKey, [property: JsonRequired] string DisplayName,
    [property: JsonRequired] string Revision, [property: JsonRequired] int DailyTokenLimit,
    [property: JsonRequired] int UsedPromptTokens, [property: JsonRequired] int UsedCompletionTokens,
    [property: JsonRequired] long UsedTokens, [property: JsonRequired] int RemainingTokens,
    [property: JsonRequired] string ResetDate, [property: JsonRequired] string NextResetAt,
    [property: JsonRequired] bool EnergySaving, [property: JsonRequired] int MinimumDailyTokenLimit,
    [property: JsonRequired] int MaximumDailyTokenLimit);
public sealed record ZbaQuotaPage([property: JsonRequired] IReadOnlyList<ZbaQuota> Items);
public sealed record ZbaQuotaSaveRequest([property: JsonRequired] string SourceKey,
    [property: JsonRequired] string ExpectedRevision, [property: JsonRequired] string IdempotencyKey,
    [property: JsonRequired] int DailyTokenLimit)
{
    public override string ToString() => nameof(ZbaQuotaSaveRequest);
}
public sealed record ZbaQuotaSaveResult([property: JsonRequired] ZbaQuota Quota);
public sealed record ZbaQuotaFindRequest([property: JsonRequired] string IdempotencyKey);
/// <summary>Quota is the committed snapshot, not current usage; refresh the list after reconciliation.</summary>
public sealed record ZbaQuotaFindResult([property: JsonRequired] bool Found, ZbaQuota? Quota);
