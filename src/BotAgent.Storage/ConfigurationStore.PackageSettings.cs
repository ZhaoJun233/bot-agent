using System.Globalization;
using BotAgent.Adapters.Persistence;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;

namespace BotAgent.Storage;

public sealed partial class ConfigurationStore
{
    // Trusted storage actions only. Package validation and application are not invoked here.
    internal static PackageSettingsCommit<T> CommitPackageSettings<T>(string pluginId,
        Func<SqliteConnection, PackageSettingsWrite<T>> persist)
    {
        // Check before the publish gate: a caller holding the DB writer must not wait on it.
        AppDatabase.RejectNestedWrite();
        var active = Volatile.Read(ref _active);
        if (active is null)
        {
            if (Volatile.Read(ref _activationInProgress) != 0)
                throw new InvalidOperationException("configuration_writer_required");
            // Keep bootstrap admission and its complete transaction together. Activation
            // cannot occur between deciding to bootstrap and acquiring the database writer.
            lock (ActivationGate)
            {
                active = Volatile.Read(ref _active);
                if (active is null)
                {
                    T bootstrap = default!;
                    AppDatabase.Write(connection => bootstrap = persist(connection).Result);
                    return new(bootstrap);
                }
            }
        }

        // Never wait for publication while holding the activation gate.
        T receipt = default!;
        var applicationFailed = false;
        active._participant.CoordinateWrite(() =>
        {
            Action? publish = null;
            AppDatabase.Write(connection =>
            {
                var before = Version(connection);
                var committed = persist(connection);
                if (!committed.Replayed)
                {
                    if (before.Value == long.MaxValue)
                        throw new InvalidOperationException("configuration_version_exhausted");
                    var after = checked(before.Value + 1);
                    AppDatabase.Exec(connection, SaveVersionSql, ("$version", after));
                    var detail = "plugin=" + pluginId + ";observed=" + before.Value.ToString(CultureInfo.InvariantCulture)
                        + ";committed=" + after.ToString(CultureInfo.InvariantCulture);
                    new AuditLogStore().AppendInTransaction(connection,
                        new AuditEvent("configuration_package_settings", "configuration-writer", "bot", detail, Revision));
                }
                receipt = committed.Result;
                if (!committed.Replayed) publish = committed.Publish;
            });
            // Trusted snapshot exchange only, after SQLite commit and before the next writer.
            try { publish?.Invoke(); }
            catch (Exception) { applicationFailed = true; }
        });
        // The immutable original receipt is already durable. A failed application never
        // rolls back desired values or repeats publication when that receipt is replayed.
        return new(receipt, applicationFailed);
    }
}

internal sealed record PackageSettingsWrite<T>(T Result, bool Replayed, Action? Publish = null);
internal sealed record PackageSettingsCommit<T>(T Result, bool ApplicationFailed = false);
