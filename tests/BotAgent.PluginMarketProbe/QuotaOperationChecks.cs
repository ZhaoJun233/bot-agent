using System.Globalization;
using System.Text.Json;
using BotAgent.Domain.Ports;
using BotAgent.Panel;
using BotAgent.Services.Ops;

internal partial class Program
{
    private static int RunQuotaOperationChecks(IQuotaOperations operations)
    {
        var count = 0;
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); count++; }
        void Reject(Action action, int status, string code)
        {
            try { action(); }
            catch (QuotaOperationException e) when (e.Status == status && e.Code == code) { count++; return; }
            throw new InvalidOperationException("expected_" + code);
        }
        const string tenant = "synthetic:quota-http";
        const string caller = "synthetic:http-caller";
        bool Known(string key) => key == tenant;
        string Name(string key) => key == tenant ? "Source A" : "Source Z";
        var page = operations.List(["synthetic:z-http", tenant], Name);
        var first = page.Items[0];
        Check(first.SourceKey == tenant && first.DisplayName == "Source A" && first.DailyTokenLimit == 50000
            && first.MinimumDailyTokenLimit == 1 && first.MaximumDailyTokenLimit == 1000000000,
            "package owns sorted HTTP projection and limit metadata");
        Check(DateTimeOffset.Parse(first.NextResetAt, CultureInfo.InvariantCulture)
            == DateTimeOffset.Parse(first.ResetDate + "T00:00:00Z", CultureInfo.InvariantCulture).AddDays(1), "HTTP next reset remains UTC next day");
        var request = new ZbaQuotaSaveRequest(tenant, first.Revision, "synthetic-http-save", 75);
        Reject(() => operations.Save(request with { SourceKey = "synthetic:missing" }, caller, Known, Name), 404, "quota_source_not_found");
        Reject(() => operations.Save(request with { SourceKey = " " }, caller, Known, Name), 400, "invalid_source_key");
        Reject(() => operations.Save(request with { ExpectedRevision = "-1" }, caller, Known, Name), 400, "invalid_quota_revision");
        Reject(() => operations.Save(request with { IdempotencyKey = "bad/key0" }, caller, Known, Name), 400, "invalid_idempotency_key");
        Reject(() => operations.Save(request with { DailyTokenLimit = 0 }, caller, Known, Name), 400, "invalid_daily_token_limit");
        var maskedAfterCommit = false;
        var saved = operations.Save(request, caller, Known, key =>
        {
            maskedAfterCommit = operations.Find(request.IdempotencyKey, caller, _ => "Masked").Found;
            return Name(key);
        }).Quota;
        Check(maskedAfterCommit, "response masking samples current configuration after the durable commit");
        Check(saved.Revision == "1" && saved.DailyTokenLimit == 75 && saved.DisplayName == "Source A",
            "package HTTP save returns committed typed projection");
        Check(operations.Save(request, caller, Known, Name).Quota == saved, "HTTP original key replay precedes stale revision");
        Reject(() => operations.Save(request with { IdempotencyKey = "synthetic-http-stale" }, caller, Known, Name), 409, "quota_revision_conflict");
        Reject(() => operations.Save(request with { DailyTokenLimit = 76 }, caller, Known, Name), 409, "idempotency_conflict");
        var found = operations.Find(request.IdempotencyKey, caller, _ => "Masked");
        Check(found.Found && found.Quota == saved with { DisplayName = "Masked" }
            && !operations.Find(request.IdempotencyKey, caller + ":other", _ => "Masked").Found,
            "HTTP reconciliation retains caller scope and applies current masking");
        DateTimeOffset UnexpectedClock() => throw new InvalidOperationException("unexpected_legacy_fallback_clock");
        var legacy = operations.LegacyGet(" " + tenant + " ", Known, UnexpectedClock);
        Check(legacy.Status == 200 && legacy.Body.GetProperty("tenant").GetString() == tenant
            && legacy.Body.GetProperty("dailyTokenLimit").GetInt32() == 75
            && legacy.Body.GetProperty("reason").ValueKind == JsonValueKind.Null, "legacy shape and tenant trimming preserved");
        Check(operations.LegacyGet(" ", Known, UnexpectedClock).Status == 400
            && operations.LegacyGet("synthetic:missing", Known, UnexpectedClock).Status == 404, "legacy missing sources refuse");
        AuditEvent? audit = null;
        QuotaLegacyResponse LegacySave(object body, Action<AuditEvent>? append = null) => operations.LegacySave(
            JsonSerializer.SerializeToElement(body), Known, _ => "Masked", "panel:synthetic", append, UnexpectedClock);
        Check(LegacySave(new { tenant, dailyTokenLimit = 0 }).Status == 400
            && LegacySave(new { tenant, dailyTokenLimit = 1.5 }).Status == 400
            && LegacySave(new { dailyTokenLimit = 99 }).Status == 400, "legacy JSON and integer bounds preserved");
        var legacySaved = LegacySave(new { tenant, dailyTokenLimit = 90 }, e => audit = e);
        Check(legacySaved.Status == 200 && legacySaved.Body.GetProperty("ok").GetBoolean()
            && legacySaved.Body.GetProperty("dailyTokenLimit").GetInt32() == 90
            && audit is { ActorId: "panel:synthetic", TenantId: "panel" }
            && audit.ActionDetail.Contains("Masked", StringComparison.Ordinal) && !audit.ActionDetail.Contains(tenant, StringComparison.Ordinal),
            "legacy save retains response and masked audit after commit");
        var auditFailed = false;
        try { LegacySave(new { tenant, dailyTokenLimit = 91 }, _ => throw new InvalidOperationException("synthetic_legacy_audit")); }
        catch (InvalidOperationException e) when (e.Message == "synthetic_legacy_audit") { auditFailed = true; }
        Check(auditFailed && operations.LegacyGet(tenant, Known, UnexpectedClock).Body.GetProperty("dailyTokenLimit").GetInt32() == 91,
            "legacy post-commit audit failure does not pretend rollback");
        Check(operations.Find(request.IdempotencyKey, caller, _ => "Masked").Quota!.DailyTokenLimit == 75,
            "later legacy edits never rewrite the original modern receipt");
        Type[] dto = [typeof(ZbaQuota), typeof(ZbaQuotaPage), typeof(ZbaQuotaSaveRequest), typeof(ZbaQuotaSaveResult),
            typeof(ZbaQuotaFindRequest), typeof(ZbaQuotaFindResult)];
        Check(dto.All(t => t.Assembly == typeof(IQuotaOperations).Assembly && typeof(ZbaEndpoint).Assembly.GetType(t.FullName!) == t),
            "all six old Panel DTO identities forward to the quota contract");
        return count;
    }
}
