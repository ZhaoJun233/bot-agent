using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Ops;
using BotAgent.Domain.Permissions;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

/// <summary>
/// 跨平台轻量 RBAC 持久化桥（<c>meta['configuration_access_roles']</c>，0 新增 SQL 字面量）。
/// 复用 <see cref="ConfigurationStorageBridge.CommitPackageSettings{T}" /> 与 <see cref="OperationRecoveryStore" />。
/// </summary>
public sealed class AccessRoleStore
{
    public const string OwnerId = "system.agent";
    public const string MetaKey = "configuration_access_roles";
    public const string OperationName = "access.roles.save";

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16,
    };

    public sealed record Snapshot(long Revision, ImmutableArray<AccessRoleBinding> Bindings);

    public sealed record SaveReceipt(
        bool Accepted,
        bool Replayed,
        bool ApplicationFailed,
        string? ErrorCode,
        Snapshot Snapshot);

    private sealed record Envelope(int Version, long Revision, List<AccessRoleBinding> Entries);
    private sealed record ReceiptPayload(long Revision, int Count);

    public Snapshot Load()
    {
        using var connection = AppDatabase.Open();
        return LoadInTransaction(connection);
    }

    public Snapshot LoadInTransaction(SqliteConnection connection)
    {
        var raw = AppDatabase.GetMetaInTransaction(connection, MetaKey);
        if (string.IsNullOrWhiteSpace(raw))
            return new Snapshot(0, ImmutableArray<AccessRoleBinding>.Empty);
        if (StrictUtf8.GetByteCount(raw) > AccessRoleValidator.MaxJsonBytes)
            throw new JsonException("access_roles_payload_too_large");

        var env = JsonSerializer.Deserialize<Envelope>(raw, Json) ?? throw new JsonException("access_roles_corrupt");
        if (env.Version != 1 || env.Revision < 0) throw new JsonException("access_roles_corrupt");
        var frozen = AccessRoleValidator.FreezeAll(env.Entries);
        return new Snapshot(env.Revision, frozen);
    }

    public SaveReceipt Save(
        long expectedRevision,
        string idempotencyKey,
        string callerScope,
        IEnumerable<AccessRoleBinding> bindings,
        Action<ImmutableArray<AccessRoleBinding>>? onPublish = null)
    {
        if (expectedRevision < 0)
            return new SaveReceipt(false, false, false, "invalid_revision", Load());
        if (string.IsNullOrWhiteSpace(idempotencyKey) || string.IsNullOrWhiteSpace(callerScope)
            || idempotencyKey.Length > 256 || callerScope.Length > 256
            || idempotencyKey.Any(char.IsControl) || callerScope.Any(char.IsControl))
            return new SaveReceipt(false, false, false, "invalid_idempotency_key", Load());

        ImmutableArray<AccessRoleBinding> frozen;
        try
        {
            frozen = AccessRoleValidator.FreezeAll(bindings);
        }
        catch (ArgumentException ex)
        {
            return new SaveReceipt(false, false, false, ex.Message, Load());
        }

        var requestPayload = JsonSerializer.Serialize(new
        {
            expectedRevision,
            entries = frozen
        }, Json);
        if (StrictUtf8.GetByteCount(requestPayload) > AccessRoleValidator.MaxJsonBytes)
            return new SaveReceipt(false, false, false, "access_roles_payload_too_large", Load());
        var requestDigest = OperationRecoveryStore.Digest(requestPayload);

        var commit = ConfigurationStorageBridge.CommitPackageSettings(OwnerId, connection =>
        {
            var current = LoadInTransaction(connection);
            var existing = OperationRecoveryStore.Read(
                connection,
                OwnerId,
                OperationRecoveryStore.Digest(callerScope),
                OperationRecoveryStore.Digest(idempotencyKey));
            if (existing is not null)
            {
                if (!string.Equals(existing.RequestDigest, requestDigest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("access_roles_idempotency_conflict");
                return new PackageSettingsWrite<SaveReceipt>(
                    new SaveReceipt(true, true, false, null, current),
                    Replayed: true);
            }

            if (current.Revision != expectedRevision)
            {
                return new PackageSettingsWrite<SaveReceipt>(
                    new SaveReceipt(false, false, false, "revision_conflict", current),
                    Replayed: true);
            }

            var nextRev = checked(current.Revision + 1);
            var nextEnv = new Envelope(1, nextRev, frozen.ToList());
            var json = JsonSerializer.Serialize(nextEnv, Json);
            if (StrictUtf8.GetByteCount(json) > AccessRoleValidator.MaxJsonBytes)
                throw new InvalidOperationException("access_roles_payload_too_large");
            AppDatabase.SetMetaInTransaction(connection, MetaKey, json);

            var cfgVersion = ConfigurationStorageBridge.ReadConfigurationVersion(connection);
            OperationRecoveryStore.Record(
                connection,
                new OperationReceiptBinding(
                    OwnerId,
                    OperationName,
                    "zba-1.0",
                    cfgVersion.Value.ToString(CultureInfo.InvariantCulture)),
                new OperationReceiptWrite(callerScope, idempotencyKey, requestDigest, "committed"));

            var nextSnap = new Snapshot(nextRev, frozen);
            return new PackageSettingsWrite<SaveReceipt>(
                new SaveReceipt(true, false, false, null, nextSnap),
                Replayed: false,
                Publish: onPublish is null ? null : () => onPublish(frozen));
        });

        var res = commit.Result;
        if (commit.ApplicationFailed)
            return res with { ApplicationFailed = true, ErrorCode = "access_roles_apply_failed" };
        return res;
    }

    public OperationRecoveryReceipt? Find(string idempotencyKey, string callerScope)
        => new OperationRecoveryStore().Find(OwnerId, callerScope, idempotencyKey);
}
