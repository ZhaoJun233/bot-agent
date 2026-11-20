using System.Globalization;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Adapters.Persistence;

/// <summary>Persistent metering remains available without the quota policy or its tables.</summary>
public class TokenUsageStore : ITokenUsageRecorder, ITokenUsageReader
{
    void ITokenUsageRecorder.RecordUsage(string tenantId, int promptTokens, int completionTokens)
        => RecordUsage(tenantId, promptTokens, completionTokens);

    public TokenUsageSnapshot GetUsage(string tenantId)
    {
        var today = Today();
        var tenant = NormalizeTenant(tenantId);
        TokenUsageSnapshot result = default;
        AppDatabase.Write(conn => result = ReadInTransaction(conn, tenant, today));
        return result;
    }

    public TokenUsageSnapshot RecordUsage(string tenantId, int promptTokens, int completionTokens)
    {
        var today = Today();
        var tenant = NormalizeTenant(tenantId);
        TokenUsageSnapshot result = default;
        AppDatabase.Write(conn => result = RecordInTransaction(conn, tenant, today, promptTokens, completionTokens));
        return result;
    }

    internal static TokenUsageSnapshot ReadInTransaction(SqliteConnection conn, string tenant, string today)
        => RecordInTransaction(conn, tenant, today, 0, 0);

    internal static TokenUsageSnapshot RecordInTransaction(SqliteConnection conn, string tenant, string today,
        int promptTokens, int completionTokens)
    {
        using var command = conn.CreateCommand();
        command.CommandText = """
            INSERT INTO token_usage(tenant_id, used_prompt_tokens, used_completion_tokens, reset_date, updated_at)
            VALUES($id,$prompt,$completion,$reset,datetime('now'))
            ON CONFLICT(tenant_id) DO UPDATE SET
              used_prompt_tokens=(CASE WHEN token_usage.reset_date=$reset THEN token_usage.used_prompt_tokens ELSE 0 END)+$prompt,
              used_completion_tokens=(CASE WHEN token_usage.reset_date=$reset THEN token_usage.used_completion_tokens ELSE 0 END)+$completion,
              reset_date=excluded.reset_date, updated_at=excluded.updated_at
            RETURNING used_prompt_tokens,used_completion_tokens;
            """;
        command.Parameters.AddWithValue("$id", tenant); command.Parameters.AddWithValue("$reset", today);
        command.Parameters.AddWithValue("$prompt", Math.Max(0, promptTokens));
        command.Parameters.AddWithValue("$completion", Math.Max(0, completionTokens));
        try
        {
            using var reader = command.ExecuteReader();
            if (!reader.Read()) throw new InvalidOperationException("token_usage_write_missing");
            return new(tenant, reader.GetInt32(0), reader.GetInt32(1), today);
        }
        // Both CHECK constraints enforce the existing Int32 counter bounds. Preserve its public overflow contract.
        catch (SqliteException exception) when (exception.SqliteExtendedErrorCode == 275)
        {
            throw new OverflowException("Token usage exceeded the counter range.", exception);
        }
    }

    internal static string Today() => Clock.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    internal static string NormalizeTenant(string? tenantId)
        => string.IsNullOrWhiteSpace(tenantId) ? "default:general" : tenantId.Trim();
}
