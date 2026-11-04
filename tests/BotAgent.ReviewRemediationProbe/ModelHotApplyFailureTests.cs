extern alias StorageModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using BotAgent.Domain.Configuration;
using BotAgent.Services;
using BotAgent.Adapters.Time;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;
using StorageDatabase = StorageModule::BotAgent.Adapters.Persistence.AppDatabase;
using StorageSecrets = StorageModule::BotAgent.Adapters.Persistence.SecretsStore;

namespace BotAgent.ReviewRemediationProbe;

internal static class ModelHotApplyFailureTests
{
    internal const string ChildArgument = "--model-hot-apply-failure";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory
        };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(ChildArgument);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("synthetic_child_start_failed");
        var stdout = child.StandardOutput.ReadToEndAsync();
        var stderr = child.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try { await child.WaitForExitAsync(timeout.Token); }
        catch
        {
            try { child.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        await stderr;
        check(child.ExitCode == 0 && (await stdout).Contains("MODEL_HOT_APPLY_FAILURE_PASS", StringComparison.Ordinal),
            "real unattached registry rebuild failure is persisted as Failed, not RestartRequired");
    }

    internal static async Task<int> RunChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-model-hot-apply-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        var stage = "initialize";
        try
        {
            ClockBindings.InitializeStorage();
            StorageDatabase.Initialize();
            FileLog.WriteToFile = false;
            FileLog.Verbose = false;
            var participant = new SyntheticParticipant();
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-authority", "synthetic-writer", "bot",
                ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration | ConfigurationAccess.EditAllowedSecrets);
            var read = await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            var request = new ConfigurationSaveRequest(ConfigurationContract.Revision, read.Version,
                new(read.Generation, DateTimeOffset.UtcNow.ToUnixTimeSeconds(), Guid.NewGuid()),
                new(null, new ModelChanges { DefaultTemperature = FieldEdit<double>.Set(0.8) }, null, null, null, null, null, null),
                ImmutableArray<SecretEdit>.Empty);
            stage = "commit";
            var saved = await store.SaveAsync(caller, request);
            Require(saved.Code == ConfigurationResultCode.Ok && saved.Receipt?.CommitState == CommitState.Committed,
                $"synthetic_commit_failed_{saved.Code}_{saved.Receipt?.CommitState}");
            Require(store.TryStartApplyStep(caller, request.Key, ApplyStep.ModelProjection).CanRun,
                "synthetic_model_step_not_planned");

            stage = "registry-reflection";
            var host = typeof(SettingsBox).Assembly;
            var registryType = host.GetType("BotAgent.Host.ModelProviderRegistry", throwOnError: true)!;
            var providerType = host.GetType("BotAgent.Adapters.Persistence.PersistedModelProvider", throwOnError: true)!;
            var registry = Activator.CreateInstance(registryType, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null, args: new object[] { Array.CreateInstance(providerType, 0) }, culture: null)!;
            var captured = registryType.GetProperty("Captured", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var before = captured.GetValue(registry);
            var inspect = host.GetType("BotAgent.Host.CompositionRoot", throwOnError: true)!
                .GetMethod("InspectModelProjection", BindingFlags.Static | BindingFlags.NonPublic)!;
            // A real registry without Attach naturally rejects Replace before publishing.
            // No failure switch or production-only test injection is introduced.
            stage = "projection";
            var report = (ApplyStepReport)inspect.Invoke(null, new object[]
                { saved.Receipt!.CommittedVersion!, registry, new SettingsBox(new AppSettings()), new StorageSecrets() })!;
            Require(report.Step == ApplyStep.ModelProjection && report.State == ApplyState.Failed
                && report.ErrorCode == "model_projection_rebuild_failed", "model_projection_wrong_failure_state");
            Require(ReferenceEquals(before, captured.GetValue(registry)), "old_registry_snapshot_changed");

            stage = "production-report-guard";
            var guard = host.GetType("BotAgent.Services.Settings.SettingsHotReload", throwOnError: true)!
                .GetMethod("IsValidModelProjectionReport", BindingFlags.Static | BindingFlags.NonPublic)!;
            bool Accepted(ApplyStepReport candidate) => (bool)guard.Invoke(null, new object[] { candidate })!;
            Require(Accepted(report), "production_guard_rejected_rebuild_failure");
            Require(Accepted(new(ApplyStep.ModelProjection, ApplyState.Applied, null))
                && Accepted(new(ApplyStep.ModelProjection, ApplyState.RestartRequired, "restart_required")),
                "production_guard_rejected_existing_states");
            Require(!Accepted(new(ApplyStep.ModelProjection, ApplyState.Failed, "synthetic_unknown_code"))
                && !Accepted(new(ApplyStep.RuntimePolicy, ApplyState.Failed, "model_projection_rebuild_failed"))
                && !Accepted(new(ApplyStep.ModelProjection, ApplyState.Unknown, "projection_unavailable")),
                "production_guard_admitted_unclassified_report");

            stage = "receipt";
            var recorded = store.RecordApply(caller, request.Key, ImmutableArray.Create(report));
            Require(recorded.Code == ConfigurationResultCode.Ok, "failure_receipt_record_failed");
            var found = await store.FindAsync(caller, request.Key);
            var persisted = found.Receipt?.Steps.Single(step => step.Step == ApplyStep.ModelProjection);
            Require(found.Code == ConfigurationResultCode.Ok && persisted?.State == ApplyState.Failed
                && persisted.ErrorCode == "model_projection_rebuild_failed"
                && found.Receipt!.CommitState == CommitState.Committed
                && found.Receipt.Steps.All(step => step.State != ApplyState.RestartRequired),
                "durable_failure_receipt_not_preserved");
            Console.WriteLine("MODEL_HOT_APPLY_FAILURE_PASS");
            return 0;
        }
        catch (Exception error)
        {
            var code = error is SyntheticFailure failure ? failure.Code : error.GetType().Name;
            Console.Error.WriteLine($"MODEL_HOT_APPLY_FAILURE_FAIL stage={stage} code={code}");
            return 1;
        }
        finally { try { Directory.Delete(root, recursive: true); } catch { } }
    }

    private static void Require(bool condition, string code)
    {
        if (!condition) throw new SyntheticFailure(code);
    }

    private sealed class SyntheticFailure(string code) : Exception
    {
        internal string Code { get; } = code;
    }

    private sealed class SyntheticParticipant : StorageConfiguration.ConfigurationStorageParticipant
    {
        internal override bool Reauthorize(ConfigurationCaller caller, ConfigurationAccess required) => caller.Access.HasFlag(required);
        internal override ImmutableArray<SecretMetadata> SecretSources(SqliteConnection connection,
            ImmutableArray<StorageConfiguration.SecretMutation> proposed)
            => Enum.GetValues<WritableSecretSlot>()
                .Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
        internal override StorageConfiguration.PreparedConfigurationChange Prepare(SqliteConnection connection,
            ConfigurationPatch ownedIntent, ImmutableArray<SecretMetadata> proposedSecrets,
            ImmutableArray<StorageConfiguration.SecretMutation> proposedMutations, StorageConfiguration.ConfigurationStorageProfile profile)
            => new(true, ImmutableArray<FieldError>.Empty, ImmutableArray<DeviceKeyAssignment>.Empty, _ => { });
        internal override ConfigurationReadResult Read(SqliteConnection connection, ConfigurationCaller caller,
            ConfigurationReadKind kind, ConfigurationVersion version, ConfigurationGeneration generation,
            long serverUnixSeconds, ApplyState latestApplyState)
            => new(ConfigurationResultCode.Ok, version, generation, ConfigurationContract.Revision, serverUnixSeconds,
                null, ImmutableArray<SecretMetadata>.Empty, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
        internal override void AppendAudit(SqliteConnection connection, ConfigurationReceipt receipt,
            ImmutableArray<ConfigurationFieldId> fields) { }
    }
}
