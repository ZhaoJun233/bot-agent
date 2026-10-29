extern alias StorageModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Management;
using BotAgent.Services;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;

namespace BotAgent.ReviewRemediationProbe;

internal static class ManagedCapabilityContractTests
{
    internal static async Task<int?> TryRunChildAsync(string[] args)
        => args is ["--managed-validation"] ? await ValidationChildAsync() : null;

    internal static Task RunAsync(Action<bool, string> report)
        => RunIsolatedAsync("--managed-validation", "MANAGED_VALIDATE failed=0", report,
            "C4 Validate uses the real save participant with independent permission and zero database mutations");

    internal static async Task RunIsolatedAsync(string argument, string successMarker,
        Action<bool, string> report, string label)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(typeof(ManagedCapabilityContractTests).Assembly.Location);
        start.ArgumentList.Add(argument);
        foreach (var key in start.Environment.Keys.Where(key => new[]
            { "BOTAGENT", "QQCHAT", "OPENAI", "FEISHU", "MINIMAX", "TTS" }
            .Any(prefix => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToArray())
            start.Environment.Remove(key);
        start.Environment["BOTAGENT_FILE_LOG"] = "0";
        start.Environment["QQCHAT_FILE_LOG"] = "0";
        using var process = Process.Start(start) ?? throw new InvalidOperationException("synthetic_child_not_started");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr; // Diagnostics remain private; only the closed test marker is inspected.
            report(process.ExitCode == 0 && output.Contains(successMarker, StringComparison.Ordinal), label);
        }
        finally
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
        }
    }

    private static async Task<int> ValidationChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-managed-validation-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("BOTAGENT_FILE_LOG", "0");
        Environment.SetEnvironmentVariable("QQCHAT_FILE_LOG", "0");
        var passed = 0;
        void Check(bool condition)
        {
            if (!condition) throw new InvalidOperationException("synthetic_validation_assertion_" + passed);
            passed++;
        }
        try
        {
            ClockBindings.InitializeStorage();
            AppDatabase.Initialize();
            var baseline = new AppSettings { AiDesire = 10, ModelBaseUrl = "https://model.example.com/v1" };
            new SettingsStore().Save(baseline);
            new ModelProviderStore().EnsurePrimary(baseline);
            var runtime = new SettingsBox(baseline);
            var authorized = true;
            Action<ConfigurationAccess>? onAuthorize = null;
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, access) =>
            { onAuthorize?.Invoke(access); return authorized; };
            var participant = ActualParticipant(runtime, authorization);
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-validator", "bot",
                ConfigurationAccess.ValidateConfiguration | ConfigurationAccess.EditAllowedSecrets
                | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            var header = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            Check(header.Code == ConfigurationResultCode.Ok);
            ConfigurationPatch Patch(int value = 21) => new(new BehaviorChanges
                { AiDesire = FieldEdit<int>.Set(value) }, null, null, null, null, null, null, null);
            ConfigurationValidationRequest Request(ConfigurationPatch? patch = null) => new("cfg-0.3.3",
                header.Version, patch ?? Patch(), ImmutableArray<SecretEdit>.Empty);
            var before = DatabaseFingerprint();
            async Task<ManagedValidationResult> Validate(ConfigurationValidationRequest request,
                ConfigurationCaller? identity = null, CancellationToken ct = default)
            {
                var result = await StorageConfiguration.ConfigurationStorageBridge.ValidateAsync(store,
                    identity ?? caller, request, ct);
                Check(DatabaseFingerprint() == before && runtime.Current.AiDesire == 10);
                return result;
            }
            Check((await Validate(Request())).Code == ManagedResultCode.Ok);
            Check((await Validate(Request(), caller with { Access = ConfigurationAccess.SaveConfiguration })).Code
                == ManagedResultCode.Forbidden);
            Check((await Validate(Request(), caller with { Access = ConfigurationAccess.ValidateConfiguration })).Code
                == ManagedResultCode.Ok);
            Check((await Validate(Request() with { ContractRevision = "cfg-future" })).Code
                == ManagedResultCode.UnsupportedContractRevision);
            Check((await Validate(Request() with { ExpectedVersion = new(header.Version.Value + 1) })).Code
                == ManagedResultCode.VersionConflict);
            Check((await Validate(Request() with { ExpectedVersion = new(-1) })).Code == ManagedResultCode.ValidationFailed);
            Check((await Validate(Request() with { Secrets = default })).Code == ManagedResultCode.ValidationFailed);
            authorized = false;
            Check((await Validate(Request())).Code == ManagedResultCode.Forbidden);
            authorized = true;
            using (var input = SensitiveInput.FromUtf8(Encoding.UTF8.GetBytes("synthetic-validation-key")))
            {
                var secret = Request() with { Secrets = ImmutableArray.Create(SecretEdit.Replace(WritableSecretSlot.TtsApiKey, input)) };
                Check((await Validate(secret, caller with { Access = ConfigurationAccess.ValidateConfiguration })).Code
                    == ManagedResultCode.Forbidden);
                var authorizations = 0;
                onAuthorize = _ => { if (++authorizations >= 3) input.Dispose(); };
                Check((await Validate(secret)).Code == ManagedResultCode.Ok);
                onAuthorize = null;
                Check((await Validate(secret)).Code == ManagedResultCode.ValidationFailed);
            }
            foreach (var bytes in new[] { Array.Empty<byte>(), new byte[] { 0xff }, Encoding.UTF8.GetBytes(" \t "), new byte[] { 65, 0, 66 } })
            {
                using var input = SensitiveInput.FromUtf8(bytes);
                Check((await Validate(Request() with { Secrets = ImmutableArray.Create(
                    SecretEdit.Replace(WritableSecretSlot.TtsApiKey, input)) })).Code == ManagedResultCode.ValidationFailed);
            }
            Check((await Validate(Request() with { Secrets = ImmutableArray.Create(
                SecretEdit.Delete(WritableSecretSlot.TtsApiKey)) })).Code == ManagedResultCode.Ok);
            var alias = new RegisteredConfigurationChanges(ImmutableArray<PluginEnabledEdit>.Empty,
                ImmutableArray.Create(new ProviderEdit(new("primary"), new(default, default, default,
                    FieldEdit<string>.Set("synthetic-alias-model"), default, CredentialBindingEdit.KeepExisting(), default, default))),
                ImmutableArray<PolicyEdit>.Empty);
            var conflictingAlias = new ConfigurationPatch(null,
                new ModelChanges { Model = FieldEdit<string>.Set("synthetic-direct-model") },
                null, null, null, null, null, alias);
            Check((await Validate(Request(conflictingAlias))).Errors.Any(error => error.Code == "alias_conflict"));
            var unavailableEndpoint = new ConfigurationPatch(null,
                new ModelChanges { ModelBaseUrl = FieldEdit<string>.Set("https://unregistered.example.com/v1") },
                null, null, null, null, null, null);
            var validation = await Validate(Request(unavailableEndpoint));
            var save = await store.SaveAsync(caller, new("cfg-0.3.3", header.Version,
                new(header.Generation, header.ServerUnixSeconds, Guid.NewGuid()), unavailableEndpoint, ImmutableArray<SecretEdit>.Empty));
            Check(validation.Code == ManagedResultCode.ValidationFailed && save.Code == ConfigurationResultCode.ValidationFailed
                && validation.Errors.SequenceEqual(save.Errors));
            // Save may advance the governance floor; all further Validate calls must leave this new fact unchanged.
            before = DatabaseFingerprint();
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                var cancelled = false;
                try { await Validate(Request(), ct: cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                Check(cancelled && DatabaseFingerprint() == before);
            }
            using (var cancellation = new CancellationTokenSource())
            {
                var checks = 0;
                onAuthorize = _ => { if (++checks >= 2) cancellation.Cancel(); };
                var cancelled = false;
                try { await Validate(Request(), ct: cancellation.Token); }
                catch (OperationCanceledException) { cancelled = true; }
                onAuthorize = null;
                Check(cancelled && DatabaseFingerprint() == before);
            }
            Check((await Validate(Request(), caller with { ResourceId = "synthetic-other-resource" })).Code
                == ManagedResultCode.Forbidden);
            Console.WriteLine($"MANAGED_VALIDATE failed=0 passed={passed}");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"MANAGED_VALIDATE failed=1 passed={passed} type={error.GetType().Name}");
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (full.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("botagent-managed-validation-", StringComparison.Ordinal)
                && Directory.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReparsePoint) == 0)
                Directory.Delete(full, recursive: true);
        }
    }

    private static StorageConfiguration.ConfigurationStorageParticipant ActualParticipant(SettingsBox runtime,
        Func<ConfigurationCaller, ConfigurationAccess, bool> authorize)
    {
        Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> sources
            = (connection, edits) => Enum.GetValues<WritableSecretSlot>().Select(slot =>
            {
                var kind = edits.FirstOrDefault(edit => edit.Slot == slot).Kind;
                var configured = kind == SecretEditKind.Replace || kind == SecretEditKind.Keep
                    && StorageSecrets.LoadInTransaction(connection, slot) is not null;
                return new SecretMetadata(slot, configured, configured ? CredentialSource.Database : CredentialSource.None);
            }).ToImmutableArray();
        Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
            new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty, ImmutableArray<ModelCredentialView>.Empty);
        Func<ConfigurationFieldId, ConfigurationValueSource> provenance = _ => ConfigurationValueSource.UserConfiguration;
        Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Restart;
        var type = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("actual_host_participant_missing");
        return (StorageConfiguration.ConfigurationStorageParticipant)type.GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single().Invoke(new object[] { runtime, authorize, sources, registered, provenance, effect, false });
    }

    private static string DatabaseFingerprint()
    {
        using var connection = StorageDatabase.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var names = connection.CreateCommand();
        names.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name";
        var tables = new List<string>();
        using (var reader = names.ExecuteReader()) while (reader.Read()) tables.Add(reader.GetString(0));
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var table in tables)
        {
            digest.AppendData(Encoding.UTF8.GetBytes(table));
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"") + "\"";
            using var reader = command.ExecuteReader();
            var rows = new List<string>();
            while (reader.Read())
            {
                var values = new object[reader.FieldCount]; reader.GetValues(values);
                rows.Add(JsonSerializer.Serialize(values.Select(value => value is DBNull ? null : value)));
            }
            foreach (var row in rows.OrderBy(row => row, StringComparer.Ordinal)) digest.AppendData(Encoding.UTF8.GetBytes(row));
        }
        return Convert.ToHexString(digest.GetHashAndReset());
    }
}
