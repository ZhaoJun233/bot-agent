extern alias StorageModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Ops;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;

namespace BotAgent.ReviewRemediationProbe;

internal static class WhitelistMigrationTests
{
    private const string ChildSwitch = "--whitelist-migration";
    private static readonly PolicyTarget.AccountDefault LegacyQq = new(PlatformId.QqPrivate, AccountScope.Legacy);

    internal static Task<int?> TryRunChildAsync(string[] args)
        => args.Length == 4 && args[0] == ChildSwitch
            ? Task.FromResult<int?>(RunChild(args[1], args[2], args[3] == "explicit"))
            : Task.FromResult<int?>(null);

    internal static async Task RunAsync(Action<bool, string> check)
    {
        CheckHelper(check);
        foreach (var explicitPrivate in new[] { false, true })
        {
            var root = Path.Combine(Path.GetTempPath(), "botagent-whitelist-migration-" + Guid.NewGuid().ToString("N"));
            try
            {
                foreach (var phase in new[] { "seed", "migrate", "restart" })
                {
                    var passed = await RunProcessAsync(root, phase, explicitPrivate);
                    check(passed, $"Whitelist init {phase}: explicit private={explicitPrivate}");
                    if (!passed) break;
                }
            }
            finally
            {
                try { Directory.Delete(root, recursive: true); } catch { }
            }
        }
    }

    private static void CheckHelper(Action<bool, string> check)
    {
        var method = typeof(SettingsStore).Assembly.GetType("BotAgent.Adapters.Persistence.PlatformScalarSync")!
            .GetMethod("ReconcileEmptyQqLists", BindingFlags.Static | BindingFlags.NonPublic)!;
        bool Reconcile(Dictionary<PolicyTarget, PlatformPolicyEntry> entries, ImmutableDictionary<PolicyTarget, int> masks, AppSettings settings)
            => (bool)method.Invoke(null, new object[] { entries, masks, settings })!;

        foreach (var rule in new[] { ListRule.Explicit(""), ListRule.Explicit("10001"), ListRule.AllowAll() })
        foreach (var mask in new[] { 0, 4, 8, 12 })
        foreach (var groupScalar in new[] { "", "10001" })
        foreach (var privateScalar in new[] { " 	", "10002" })
        {
            var original = Entry(LegacyQq, rule, rule);
            var entries = new Dictionary<PolicyTarget, PlatformPolicyEntry> { [LegacyQq] = original };
            var masks = ImmutableDictionary<PolicyTarget, int>.Empty.Add(LegacyQq, mask);
            var settings = new AppSettings { WhitelistGroups = groupScalar, WhitelistPrivates = privateScalar };
            var migrateGroup = (mask & 4) == 0 && string.IsNullOrWhiteSpace(groupScalar) && rule.Kind != ListRuleKind.AllowAll;
            var migratePrivate = (mask & 8) == 0 && string.IsNullOrWhiteSpace(privateScalar) && rule.Kind != ListRuleKind.AllowAll;
            var changed = Reconcile(entries, masks, settings);
            var result = entries[LegacyQq].Configuration;
            check(changed == (migrateGroup || migratePrivate)
                && (migrateGroup ? result.GroupWhitelist.Kind == ListRuleKind.AllowAll : ReferenceEquals(result.GroupWhitelist, rule))
                && (migratePrivate ? result.PrivateWhitelist.Kind == ListRuleKind.AllowAll : ReferenceEquals(result.PrivateWhitelist, rule))
                && result.Enabled == original.Configuration.Enabled && result.ChatEnabled == original.Configuration.ChatEnabled
                && result.Features.Equals(original.Configuration.Features)
                && ReferenceEquals(result.AllowedActions, original.Configuration.AllowedActions) && masks[LegacyQq] == mask,
                $"Whitelist migration is field-local: mode={rule.Kind}, mask={mask}, groupBlank={groupScalar.Length == 0}, privateBlank={string.IsNullOrWhiteSpace(privateScalar)}");
            var migrated = entries[LegacyQq];
            check(!Reconcile(entries, masks, settings) && ReferenceEquals(entries[LegacyQq], migrated),
                "Whitelist reconciliation second pass is a no-op, including existing AllowAll");
        }

        var untouched = OtherEntries().ToDictionary(entry => entry.Target);
        var originalEntries = untouched.ToArray();
        check(!Reconcile(untouched, ImmutableDictionary<PolicyTarget, int>.Empty, new AppSettings { WhitelistGroups = "", WhitelistPrivates = "" })
            && untouched.SequenceEqual(originalEntries), "Whitelist migration leaves other platforms/accounts/instances untouched");
        var implicitEntry = new Dictionary<PolicyTarget, PlatformPolicyEntry>
        { [LegacyQq] = Entry(LegacyQq, ListRule.Explicit(""), ListRule.Explicit("")) };
        var absentMask = ImmutableDictionary<PolicyTarget, int>.Empty;
        check(Reconcile(implicitEntry, absentMask, new AppSettings { WhitelistGroups = "", WhitelistPrivates = "" })
            && implicitEntry[LegacyQq].Configuration.GroupWhitelist.Kind == ListRuleKind.AllowAll
            && implicitEntry[LegacyQq].Configuration.PrivateWhitelist.Kind == ListRuleKind.AllowAll && absentMask.Count == 0,
            "Whitelist missing explicit mask remains implicit and is not invented");
    }

    private static PlatformPolicyEntry Entry(PolicyTarget target, ListRule groups, ListRule privates)
        => new(target, new(ToggleRule.Disabled, ToggleRule.Enabled, groups, privates,
            new ActionRule(ActionRuleKind.Explicit, ImmutableArray<ActionId>.Empty), ImmutableArray<FeatureOverride>.Empty));

    private static List<PlatformPolicyEntry> OtherEntries()
        => new()
        {
            Entry(new PolicyTarget.AccountDefault(PlatformId.QqPrivate, AccountScope.Default), ListRule.Explicit(""), ListRule.Explicit("")),
            Entry(new PolicyTarget.AccountDefault(PlatformId.QqOfficial, AccountScope.Legacy), ListRule.Explicit(""), ListRule.Explicit("")),
            Entry(new PolicyTarget.AccountDefault(PlatformId.Feishu, AccountScope.Default), ListRule.Explicit(""), ListRule.Explicit("")),
            Entry(new PolicyTarget.Instance(new(PlatformId.QqPrivate, AccountScope.Legacy, "synthetic-instance")), ListRule.Explicit(""), ListRule.Explicit(""))
        };

    private static async Task<bool> RunProcessAsync(string root, string phase, bool explicitPrivate)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true
        };
        // These are synthetic storage probes: never inherit live bot credentials, paths or model fallbacks.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("BOTAGENT_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("QQCHAT_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("OPENAI_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.ArgumentList.Add(typeof(WhitelistMigrationTests).Assembly.Location);
        start.ArgumentList.Add(ChildSwitch);
        start.ArgumentList.Add(root);
        start.ArgumentList.Add(phase);
        start.ArgumentList.Add(explicitPrivate ? "explicit" : "implicit");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("whitelist_child_not_started");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            await Task.WhenAll(output, error);
            return false;
        }
        var text = await output;
        await error;
        if (process.ExitCode != 0)
            foreach (var line in text.Split('\n').Where(line => line.StartsWith("WHITELIST_CHILD ", StringComparison.Ordinal)))
                Console.WriteLine(line.Trim());
        return process.ExitCode == 0 && text.Contains($"WHITELIST_CHILD {phase} passed", StringComparison.Ordinal);
    }

    private static int RunChild(string root, string phase, bool explicitPrivate)
    {
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        FileLog.WriteToFile = false;
        FileLog.Verbose = false;
        try
        {
            ClockBindings.InitializePlatforms();
            Clock.Use(new SyntheticClock());
            AppDatabase.Initialize();
            if (phase == "seed")
            {
                AppDatabase.SetMeta("user_settings", JsonSerializer.Serialize(new AppSettings
                { WhitelistGroups = "", WhitelistPrivates = "" }));
                Activate(); // Finish unrelated first-run migrations before seeding the old policy snapshot.
                StorageDatabase.Write(connection =>
                {
                    var entries = OtherEntries();
                    entries.Insert(0, Entry(LegacyQq, ListRule.Explicit("10001"), ListRule.Explicit("")));
                    StorageConfiguration.ConfigurationStorageBridge.WritePlatformPolicies(connection,
                        entries.ToImmutableArray(), entries.ToImmutableDictionary(entry => entry.Target,
                            entry => entry.Target == LegacyQq && explicitPrivate ? 8 : 0));
                    StorageDatabase.SetMetaInTransaction(connection, "synthetic:whitelist-version", Number(connection, "SELECT version FROM configuration_state WHERE id=1").ToString());
                    StorageDatabase.SetMetaInTransaction(connection, "synthetic:whitelist-audits", Number(connection, "SELECT count(*) FROM security_audit_log WHERE event_type='configuration_initialization'").ToString());
                    StorageDatabase.SetMetaInTransaction(connection, "synthetic:whitelist-user", StorageDatabase.GetMetaInTransaction(connection, "user_settings")!);
                });
            }
            else
            {
                Require(phase is "migrate" or "restart", "known_phase");
                Activate(); // Real ConfigurationStore BEGIN/Initialize/version/audit/COMMIT path, in a fresh process.
                StorageDatabase.Write(connection =>
                {
                    var snapshot = StorageConfiguration.ConfigurationStorageBridge.LoadPlatformPolicies(connection);
                    Require(snapshot.Stored, "readable_persisted_snapshot");
                    var policy = snapshot.Entries.Single(entry => entry.Target == LegacyQq).Configuration;
                    Require(policy.GroupWhitelist.Kind == ListRuleKind.AllowAll, "implicit_inherited_group_allowlist_migrated");
                    Require(explicitPrivate ? IsDenyAll(policy.PrivateWhitelist) : policy.PrivateWhitelist.Kind == ListRuleKind.AllowAll,
                        "private_denyall_respects_explicit_bit8");
                    Require(policy.Enabled == ToggleRule.Disabled && policy.ChatEnabled == ToggleRule.Enabled
                        && policy.AllowedActions.Kind == ActionRuleKind.Explicit && policy.AllowedActions.Actions.IsEmpty
                        && policy.Features.IsEmpty, "nonlist_fields_preserved");
                    Require(snapshot.Entries.Length == OtherEntries().Count + 1
                        && OtherEntries().All(expected => snapshot.Entries.Any(actual => actual.Target == expected.Target
                            && IsDenyAll(actual.Configuration.GroupWhitelist) && IsDenyAll(actual.Configuration.PrivateWhitelist)
                            && actual.Configuration.Enabled == ToggleRule.Disabled && actual.Configuration.ChatEnabled == ToggleRule.Enabled)), "other_targets_preserved");
                    Require(snapshot.ExplicitFields.Count == snapshot.Entries.Length
                        && snapshot.ExplicitFields.All(pair => pair.Value == (pair.Key == LegacyQq && explicitPrivate ? 8 : 0)), "explicit_masks_preserved");
                    Require(Number(connection, "SELECT version FROM configuration_state WHERE id=1")
                        == long.Parse(StorageDatabase.GetMetaInTransaction(connection, "synthetic:whitelist-version")!) + 1, "one_version_bump_only");
                    Require(Number(connection, "SELECT count(*) FROM security_audit_log WHERE event_type='configuration_initialization'")
                        == long.Parse(StorageDatabase.GetMetaInTransaction(connection, "synthetic:whitelist-audits")!) + 1, "one_initialization_audit_only");
                    Require(StorageDatabase.GetMetaInTransaction(connection, "user_settings")
                        == StorageDatabase.GetMetaInTransaction(connection, "synthetic:whitelist-user"), "scalar_settings_unchanged");
                    var raw = StorageDatabase.GetMetaInTransaction(connection, "configuration_platform_policies")!;
                    if (phase == "migrate") StorageDatabase.SetMetaInTransaction(connection, "synthetic:whitelist-migrated", raw);
                    else Require(raw == StorageDatabase.GetMetaInTransaction(connection, "synthetic:whitelist-migrated"), "restart_policy_bytes_unchanged");
                });
            }
            Console.WriteLine($"WHITELIST_CHILD {phase} passed");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WHITELIST_CHILD {phase} failed type={ex.GetType().Name}");
            if (ex is SqliteException sqlite)
                Console.WriteLine($"WHITELIST_CHILD sqlite={sqlite.SqliteErrorCode}/{sqlite.SqliteExtendedErrorCode} message={sqlite.Message.Replace(root, "<synthetic-root>")}");
            foreach (var frame in new StackTrace(ex).GetFrames())
                if (frame.GetMethod() is { } method)
                    Console.WriteLine($"WHITELIST_CHILD at {method.DeclaringType?.FullName}.{method.Name}");
            return 1;
        }
        finally { SqliteConnection.ClearAllPools(); }
    }

    private static void Activate()
    {
        // Same real-host participant seam as ConfigurationContractTests; no fake Initialize implementation.
        Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, _) => true;
        Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> sources
            = (_, _) => Enum.GetValues<WritableSecretSlot>().Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
        Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
            new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty, ImmutableArray<ModelCredentialView>.Empty);
        Func<ConfigurationFieldId, ConfigurationValueSource> provenance = _ => ConfigurationValueSource.UserConfiguration;
        Func<ConfigurationFieldId, FieldEffect> effect = _ => FieldEffect.Restart;
        var type = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
        var participant = (StorageConfiguration.ConfigurationStorageParticipant)Activator.CreateInstance(type,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            new object[] { new SettingsBox(new AppSettings()), authorization, sources, registered, provenance, effect, false }, null)!;
        StorageConfiguration.ConfigurationStorageBridge.Activate(Clock.Current, participant);
    }

    private static bool IsDenyAll(ListRule rule) => rule.TryGetExplicitValue(out var value) && value.Length == 0;

    private static long Number(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static void Require(bool condition, string label)
    {
        if (condition) return;
        Console.WriteLine("WHITELIST_CHILD assertion=" + label);
        throw new InvalidOperationException(label);
    }

    private sealed class SyntheticClock : IClock
    {
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1_900_000_000);
        public DateTimeOffset UtcNow => Now;
        public DateTime LocalDateTime => Now.LocalDateTime;
        public long TickCount => 5_000;
        public Task Delay(TimeSpan delay, CancellationToken ct = default) => Task.CompletedTask;
        public Task Delay(int millisecondsDelay, CancellationToken ct = default) => Task.CompletedTask;
    }
}
