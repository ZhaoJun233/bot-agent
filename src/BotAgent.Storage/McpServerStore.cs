using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Ops;
using BotAgent.Services;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

public sealed record ConsoleMcpServerRecord(
    string Id,
    bool Enabled,
    string Endpoint,
    ImmutableArray<string> AllowedTools,
    ImmutableArray<string> ReadOnlyTools,
    int CallTimeoutSeconds,
    int MaxResultBytes,
    bool HasAuthToken)
{
    public override string ToString() => $"ConsoleMcpServerRecord({Id})";
}

public sealed record ConsoleMcpServerMutation(
    string Id,
    bool Enabled,
    string Endpoint,
    IReadOnlyList<string> AllowedTools,
    IReadOnlyList<string>? ReadOnlyTools = null,
    int CallTimeoutSeconds = McpLimits.DefaultCallTimeoutSeconds,
    int MaxResultBytes = McpLimits.DefaultResultBytes,
    string? AuthToken = null,
    bool ClearAuthToken = false)
{
    public override string ToString() => $"ConsoleMcpServerMutation({Id})";
}

/// <summary>
/// 控制台托管 MCP 服务器持久化仓储（P4.3，0 新增 SQL 字面量）：
/// 元数据存于 <c>meta['configuration_mcp_servers']</c>，Bearer Token 仅写入 <c>secrets</c> 表 <c>mcp_server_token:&lt;id&gt;</c>
/// 且绝不回显明文；通过 <see cref="ConfigurationStorageBridge.CommitPackageSettings{T}" /> + <see cref="OperationRecoveryStore" /> 原子落库。
/// </summary>
public sealed class McpServerStore
{
    public const string OwnerId = "system.mcp-tools";
    public const string MetaKey = "configuration_mcp_servers";
    public const string SaveOperationName = "mcp.servers.save";
    public const string DeleteOperationName = "mcp.servers.delete";
    public const string SecretPrefix = "mcp_server_token:";
    public const int MaxJsonBytes = 64 * 1024;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public sealed record Snapshot(long Revision, ImmutableArray<ConsoleMcpServerRecord> Servers);

    public sealed record WriteReceipt(
        bool Accepted,
        bool Replayed,
        bool ApplicationFailed,
        string? ErrorCode,
        Snapshot Snapshot);

    private sealed record PersistedItem(
        string Id,
        bool Enabled,
        string Endpoint,
        List<string> AllowedTools,
        List<string>? ReadOnlyTools,
        int CallTimeoutSeconds,
        int MaxResultBytes);

    private sealed record Envelope(int Version, long Revision, List<PersistedItem> Servers);

    public Snapshot Load()
    {
        using var connection = AppDatabase.Open();
        return LoadInTransaction(connection);
    }

    public Snapshot LoadInTransaction(SqliteConnection connection)
    {
        var raw = AppDatabase.GetMetaInTransaction(connection, MetaKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new Snapshot(0, ImmutableArray<ConsoleMcpServerRecord>.Empty);
        if (StrictUtf8.GetByteCount(raw) > MaxJsonBytes)
            throw new JsonException("mcp_servers_payload_too_large");

        var env = JsonSerializer.Deserialize<Envelope>(raw, Json) ?? throw new JsonException("mcp_servers_corrupt");
        if (env.Version != 1 || env.Revision < 0 || env.Servers is null)
            throw new JsonException("mcp_servers_corrupt");

        var list = ImmutableArray.CreateBuilder<ConsoleMcpServerRecord>(env.Servers.Count);
        foreach (var item in env.Servers)
        {
            if (!ValidId(item.Id)) continue;
            var secret = SecretsStore.LoadNamedInTransaction(connection, SecretKey(item.Id));
            list.Add(new ConsoleMcpServerRecord(
                item.Id,
                item.Enabled,
                item.Endpoint,
                (item.AllowedTools ?? []).ToImmutableArray(),
                (item.ReadOnlyTools ?? []).ToImmutableArray(),
                Math.Clamp(item.CallTimeoutSeconds, 1, 120),
                Math.Clamp(item.MaxResultBytes, 1024, McpLimits.MaxResultBytes),
                !string.IsNullOrWhiteSpace(secret)));
        }
        return new Snapshot(env.Revision, list.ToImmutable());
    }

    public IReadOnlyList<McpServerOptions> LoadRuntimeServers(Func<string, (bool Ok, string NormalizedUrl, string Reason)>? validateEndpoint = null)
    {
        using var connection = AppDatabase.Open();
        var snap = LoadInTransaction(connection);
        var list = new List<McpServerOptions>(snap.Servers.Length);
        foreach (var rec in snap.Servers)
        {
            if (!rec.Enabled) continue;
            if (rec.AllowedTools.Length == 0) continue;
            var check = (validateEndpoint ?? DefaultValidateEndpoint)(rec.Endpoint);
            if (!check.Ok) continue;
            var token = SecretsStore.LoadNamedInTransaction(connection, SecretKey(rec.Id));
            list.Add(new McpServerOptions(
                rec.Id,
                check.NormalizedUrl,
                token,
                rec.AllowedTools,
                TimeSpan.FromSeconds(rec.CallTimeoutSeconds),
                rec.MaxResultBytes,
                rec.ReadOnlyTools));
        }
        return list;
    }

    public string? LoadToken(string serverId)
    {
        if (!ValidId(serverId)) return null;
        using var connection = AppDatabase.Open();
        return SecretsStore.LoadNamedInTransaction(connection, SecretKey(serverId));
    }

    public WriteReceipt Upsert(
        long expectedRevision,
        string idempotencyKey,
        string callerScope,
        ConsoleMcpServerMutation mutation,
        Func<string, (bool Ok, string NormalizedUrl, string Reason)>? validateEndpoint = null,
        Action? onPublish = null)
    {
        if (!ValidId(mutation.Id))
            return Fail("invalid_server_id", Load());
        if (!ValidateCommonInputs(expectedRevision, idempotencyKey, callerScope, out var bad))
            return Fail(bad!, Load());

        var endpointCheck = (validateEndpoint ?? DefaultValidateEndpoint)(mutation.Endpoint);
        if (!endpointCheck.Ok)
            return Fail(string.IsNullOrWhiteSpace(endpointCheck.Reason) ? "invalid_endpoint" : endpointCheck.Reason, Load());

        var allowed = NormalizeTools(mutation.AllowedTools, out var toolErr);
        if (toolErr is not null) return Fail(toolErr, Load());
        if (allowed.Count == 0) return Fail("allowed_tools_required", Load());
        var readOnly = NormalizeTools(mutation.ReadOnlyTools ?? Array.Empty<string>(), out var roErr);
        if (roErr is not null) return Fail(roErr, Load());
        if (!ValidAuthToken(mutation.AuthToken))
            return Fail("invalid_auth_token", Load());
        if (mutation.ClearAuthToken && !string.IsNullOrWhiteSpace(mutation.AuthToken))
            return Fail("invalid_auth_token", Load());

        var normalizedItem = new PersistedItem(
            mutation.Id.Trim(),
            mutation.Enabled,
            endpointCheck.NormalizedUrl,
            allowed,
            readOnly,
            Math.Clamp(mutation.CallTimeoutSeconds, 1, 120),
            Math.Clamp(mutation.MaxResultBytes, 1024, McpLimits.MaxResultBytes));

        var tokenDigest = string.IsNullOrWhiteSpace(mutation.AuthToken)
            ? string.Empty
            : OperationRecoveryStore.Digest(mutation.AuthToken.Trim());
        var digest = OperationRecoveryStore.Digest(JsonSerializer.Serialize(new
        {
            expectedRevision,
            normalizedItem,
            tokenDigest,
            mutation.ClearAuthToken
        }, Json));

        var commit = ConfigurationStorageBridge.CommitPackageSettings(OwnerId, connection =>
        {
            var current = LoadInTransaction(connection);
            var existing = OperationRecoveryStore.Read(
                connection, OwnerId, OperationRecoveryStore.Digest(callerScope), OperationRecoveryStore.Digest(idempotencyKey));
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestDigest, digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("mcp_servers_idempotency_conflict");
                return new PackageSettingsWrite<WriteReceipt>(
                    new WriteReceipt(true, true, false, null, current), Replayed: true);
            }

            if (current.Revision != expectedRevision)
            {
                return new PackageSettingsWrite<WriteReceipt>(
                    new WriteReceipt(false, false, false, "revision_conflict", current), Replayed: true);
            }

            var items = new List<PersistedItem>();
            var replaced = false;
            foreach (var s in current.Servers)
            {
                if (string.Equals(s.Id, normalizedItem.Id, StringComparison.Ordinal))
                {
                    items.Add(normalizedItem);
                    replaced = true;
                }
                else
                {
                    items.Add(new PersistedItem(s.Id, s.Enabled, s.Endpoint, s.AllowedTools.ToList(), s.ReadOnlyTools.ToList(), s.CallTimeoutSeconds, s.MaxResultBytes));
                }
            }
            if (!replaced)
            {
                if (items.Count >= McpLimits.MaxServers)
                    throw new InvalidOperationException("mcp_server_limit_exceeded");
                items.Add(normalizedItem);
            }

            items.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
            var nextRev = checked(current.Revision + 1);
            var envJson = JsonSerializer.Serialize(new Envelope(1, nextRev, items), Json);
            if (StrictUtf8.GetByteCount(envJson) > MaxJsonBytes)
                return new PackageSettingsWrite<WriteReceipt>(
                    new WriteReceipt(false, false, false, "mcp_servers_payload_too_large", current), Replayed: true);

            var nowSec = Clock.UtcNow.ToUnixTimeSeconds();
            if (mutation.ClearAuthToken)
                SecretsStore.SaveNamedInTransaction(connection, SecretKey(normalizedItem.Id), null, nowSec);
            else if (!string.IsNullOrWhiteSpace(mutation.AuthToken))
                SecretsStore.SaveNamedInTransaction(connection, SecretKey(normalizedItem.Id), mutation.AuthToken.Trim(), nowSec);

            AppDatabase.SetMetaInTransaction(connection, MetaKey, envJson);

            var cfgVersion = ConfigurationStorageBridge.ReadConfigurationVersion(connection);
            OperationRecoveryStore.Record(
                connection,
                new OperationReceiptBinding(OwnerId, SaveOperationName, "zba-1.0", cfgVersion.Value.ToString(CultureInfo.InvariantCulture)),
                new OperationReceiptWrite(callerScope, idempotencyKey, digest, "committed"));

            var nextSnap = LoadInTransaction(connection);
            return new PackageSettingsWrite<WriteReceipt>(
                new WriteReceipt(true, false, false, null, nextSnap),
                Replayed: false,
                Publish: onPublish);
        });

        var result = commit.Result;
        return commit.ApplicationFailed ? result with { ApplicationFailed = true, ErrorCode = "mcp_reload_failed" } : result;
    }

    public WriteReceipt Delete(
        long expectedRevision,
        string idempotencyKey,
        string callerScope,
        string id,
        Action? onPublish = null)
    {
        if (!ValidId(id)) return Fail("invalid_server_id", Load());
        if (!ValidateCommonInputs(expectedRevision, idempotencyKey, callerScope, out var bad))
            return Fail(bad!, Load());

        var targetId = id.Trim();
        var digest = OperationRecoveryStore.Digest(JsonSerializer.Serialize(new { expectedRevision, deleteId = targetId }, Json));

        var commit = ConfigurationStorageBridge.CommitPackageSettings(OwnerId, connection =>
        {
            var current = LoadInTransaction(connection);
            var existing = OperationRecoveryStore.Read(
                connection, OwnerId, OperationRecoveryStore.Digest(callerScope), OperationRecoveryStore.Digest(idempotencyKey));
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestDigest, digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("mcp_servers_idempotency_conflict");
                return new PackageSettingsWrite<WriteReceipt>(
                    new WriteReceipt(true, true, false, null, current), Replayed: true);
            }

            if (current.Revision != expectedRevision)
            {
                return new PackageSettingsWrite<WriteReceipt>(
                    new WriteReceipt(false, false, false, "revision_conflict", current), Replayed: true);
            }

            var items = new List<PersistedItem>();
            var found = false;
            foreach (var s in current.Servers)
            {
                if (string.Equals(s.Id, targetId, StringComparison.Ordinal))
                {
                    found = true;
                    continue;
                }
                items.Add(new PersistedItem(s.Id, s.Enabled, s.Endpoint, s.AllowedTools.ToList(), s.ReadOnlyTools.ToList(), s.CallTimeoutSeconds, s.MaxResultBytes));
            }
            if (!found)
            {
                return new PackageSettingsWrite<WriteReceipt>(
                    new WriteReceipt(false, false, false, "server_not_found", current), Replayed: true);
            }

            SecretsStore.SaveNamedInTransaction(connection, SecretKey(targetId), null, Clock.UtcNow.ToUnixTimeSeconds());
            var nextRev = checked(current.Revision + 1);
            var envJson = JsonSerializer.Serialize(new Envelope(1, nextRev, items), Json);
            AppDatabase.SetMetaInTransaction(connection, MetaKey, envJson);

            var cfgVersion = ConfigurationStorageBridge.ReadConfigurationVersion(connection);
            OperationRecoveryStore.Record(
                connection,
                new OperationReceiptBinding(OwnerId, DeleteOperationName, "zba-1.0", cfgVersion.Value.ToString(CultureInfo.InvariantCulture)),
                new OperationReceiptWrite(callerScope, idempotencyKey, digest, "committed"));

            var nextSnap = LoadInTransaction(connection);
            return new PackageSettingsWrite<WriteReceipt>(
                new WriteReceipt(true, false, false, null, nextSnap),
                Replayed: false,
                Publish: onPublish);
        });

        var result = commit.Result;
        return commit.ApplicationFailed ? result with { ApplicationFailed = true, ErrorCode = "mcp_reload_failed" } : result;
    }

    public static bool ValidId(string? id)
        => id is { Length: > 0 and <= 32 } trimmed
           && trimmed.Trim() == trimmed
           && trimmed.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-');

    public static bool ValidAuthToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        var trimmed = token.Trim();
        if (trimmed.Length > 4096) return false;
        foreach (var ch in trimmed)
        {
            if (ch < 0x21 || ch > 0x7E) return false;
        }
        return true;
    }

    public static string SecretKey(string id) => SecretPrefix + id.Trim();

    private static WriteReceipt Fail(string code, Snapshot snap) => new(false, false, false, code, snap);

    private static bool ValidateCommonInputs(long expectedRevision, string idempotencyKey, string callerScope, out string? errorCode)
    {
        if (expectedRevision < 0) { errorCode = "invalid_revision"; return false; }
        if (string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(callerScope)
            || idempotencyKey.Length > 256 || callerScope.Length > 256
            || idempotencyKey.Any(char.IsControl) || callerScope.Any(char.IsControl))
        {
            errorCode = "invalid_idempotency_key";
            return false;
        }
        errorCode = null;
        return true;
    }

    private static List<string> NormalizeTools(IReadOnlyList<string> raw, out string? errorCode)
    {
        errorCode = null;
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (raw is null) return list;
        foreach (var entry in raw)
        {
            var token = (entry ?? string.Empty).Trim();
            if (token.Length == 0) continue;
            if (token.Length > 64 || token.Any(char.IsControl))
            {
                errorCode = "invalid_tool_name";
                return list;
            }
            if (seen.Add(token)) list.Add(token);
            if (list.Count > McpLimits.MaxTools)
            {
                errorCode = "too_many_tools";
                return list;
            }
        }
        return list;
    }

    private static (bool Ok, string NormalizedUrl, string Reason) DefaultValidateEndpoint(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return (false, string.Empty, "invalid_endpoint");
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return (false, string.Empty, "userinfo_not_allowed");
        var host = uri.DnsSafeHost.Trim('[', ']').TrimEnd('.');
        if (host.Length == 0 || (!IPAddress.TryParse(host, out var ip) && !host.Contains('.')))
            return (false, string.Empty, "private_endpoint_rejected");
        if (host.EndsWith(".local", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase)
            || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase))
            return (false, string.Empty, "private_endpoint_rejected");
        if (IPAddress.TryParse(host, out ip) && IsBlockedAddress(ip))
            return (false, string.Empty, "private_endpoint_rejected");
        return (true, uri.ToString(), "ok");
    }

    private static bool IsBlockedAddress(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal
            || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any))
            return true;
        if (ip.IsIPv4MappedToIPv6)
            return IsBlockedAddress(ip.MapToIPv4());
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 127
                || b[0] == 0
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || b[0] >= 224;
        }
        if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var b = ip.GetAddressBytes();
            return (b[0] & 0xfe) == 0xfc || b[0] == 0xff || b[0] == 0;
        }
        return false;
    }
}
