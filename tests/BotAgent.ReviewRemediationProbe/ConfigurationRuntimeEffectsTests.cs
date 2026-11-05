extern alias StorageModule;

using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Adapters.Time;
using BotAgent.Domain.Configuration;
using BotAgent.Services;
using Microsoft.Data.Sqlite;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.ReviewRemediationProbe;

internal static class ConfigurationRuntimeEffectsTests
{
    internal const string ChildArgument = "--configuration-runtime-effects";

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var process = new Process { StartInfo = new ProcessStartInfo("dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false } };
        process.StartInfo.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        process.StartInfo.ArgumentList.Add(ChildArgument);
        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorsTask = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(entireProcessTree: true); throw; }
        var output = await outputTask;
        await errorsTask;
        check(process.ExitCode == 0 && output.Contains("CFG_RUNTIME_EFFECTS_OK", StringComparison.Ordinal),
            "runtime effects reflect persisted desired and active snapshots without receipt drift");
    }

    internal static async Task<int> RunChildAsync()
    {
        var root = Path.Combine(Path.GetTempPath(), "botagent-runtime-effects-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("BOTAGENT_DATA_DIR", root);
        Environment.SetEnvironmentVariable("QQCHAT_DATA_DIR", root);
        try
        {
            ClockBindings.InitializePlatforms();
            ClockBindings.InitializeStorage();
            AppDatabase.Initialize();
            var initial = new AppSettings { AiDesire = 10, OfficialAppId = "10001",
                Model = "synthetic-model", ModelOverride = "synthetic-model",
                ModelBaseUrl = "https://example.com/v1", ModelBaseUrlOverride = "https://example.com/v1" };
            new SettingsStore().Save(initial);
            new ModelProviderStore().EnsurePrimary(initial);
            var runtime = new SettingsBox(initial);
            var authorized = true;
            Func<ConfigurationCaller, ConfigurationAccess, bool> authorization = (_, _) => authorized;
            Func<SqliteConnection, ImmutableArray<StorageConfiguration.SecretMutation>, ImmutableArray<SecretMetadata>> secrets
                = (_, _) => Enum.GetValues<WritableSecretSlot>()
                    .Select(slot => new SecretMetadata(slot, false, CredentialSource.None)).ToImmutableArray();
            Func<SqliteConnection, ConfigurationReadKind, RegisteredConfigurationView> registered = (_, _) =>
                new(ImmutableArray<PluginConfigurationView>.Empty, ImmutableArray<ProviderConfigurationView>.Empty,
                    ImmutableArray<ModelCredentialView>.Empty);
            Func<ConfigurationFieldId, ConfigurationValueSource> source = _ => ConfigurationValueSource.UserConfiguration;
            Func<ConfigurationFieldId, FieldEffect> effect = field => field is ConfigurationFieldId.OfficialAppId
                or ConfigurationFieldId.PlatformPolicies ? FieldEffect.Restart : FieldEffect.Hot;
            var adapterType = typeof(SettingsStore).GetNestedType("ConfigurationParticipant", BindingFlags.NonPublic)!;
            var participant = (StorageConfiguration.ConfigurationStorageParticipant)adapterType
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke(
                    new object[] { runtime, authorization, secrets, registered, source, effect, false });
            var store = StorageConfiguration.ConfigurationStorageBridge.Activate(ClockBindings.Source, participant);
            var caller = new ConfigurationCaller("synthetic-host", "synthetic-owner", "bot",
                ConfigurationAccess.ReadOverview | ConfigurationAccess.ReadEditable | ConfigurationAccess.SaveConfiguration);
            async Task<ConfigurationReadResult> Read() => await store.ReadAsync(caller, ConfigurationReadKind.Editable);
            async Task<ConfigurationSaveResult> Save(ConfigurationPatch patch)
            {
                var read = await Read();
                return await store.SaveAsync(caller, new(ConfigurationContract.Revision, read.Version,
                    new(read.Generation, read.ServerUnixSeconds, Guid.NewGuid()), patch, ImmutableArray<SecretEdit>.Empty));
            }
            static ConfigurationRuntimeEffect State(ConfigurationReadResult read, ConfigurationFieldId field)
                => read.RuntimeEffects!.Value.Single(item => item.Field.Field == field && item.Field.Object is null);
            var before = await Read();
            Require(State(before, ConfigurationFieldId.OfficialAppId).State == ConfigurationRuntimeState.Current, "initial_current");
            var changed = await Save(new(null, null, null,
                new PlatformChanges { OfficialAppId = FieldEdit<string>.Set("synthetic-next") }, null, null, null, null));
            Require(changed.Code == ConfigurationResultCode.Ok, "restart_save:" + changed.Code + ":" + string.Join(",", changed.Errors.Select(error => error.Address.Field + "=" + error.Code)));
            var pending = await Read();
            var restart = State(pending, ConfigurationFieldId.OfficialAppId);
            Require(restart.State == ConfigurationRuntimeState.RestartRequired
                && restart.RestartTarget == ConfigurationRestartTarget.BotAgent, "pending_restart");
            var hot = await Save(new(new BehaviorChanges { AiDesire = FieldEdit<int>.Set(11) }, null, null, null, null, null, null, null));
            Require(hot.Code == ConfigurationResultCode.Ok, "hot_save");
            foreach (var step in Enum.GetValues<ApplyStep>())
                store.RecordApply(caller, hot.Receipt!.Key, ImmutableArray.Create(new ApplyStepReport(step, ApplyState.Applied, null)));
            runtime.Apply(settings => settings.AiDesire = 11);
            var afterHot = await Read();
            Require(State(afterHot, ConfigurationFieldId.OfficialAppId).State == ConfigurationRuntimeState.RestartRequired,
                "unrelated_hot_save_preserves_pending");
            Require(State(afterHot, ConfigurationFieldId.AiDesire).State == ConfigurationRuntimeState.Current, "hot_current");
            var reverted = await Save(new(null, null, null,
                new PlatformChanges { OfficialAppId = FieldEdit<string>.Set("10001") }, null, null, null, null));
            Require(reverted.Code == ConfigurationResultCode.Ok
                && State(await Read(), ConfigurationFieldId.OfficialAppId).State == ConfigurationRuntimeState.Current, "revert_clears_pending");
            await Save(new(null, null, null,
                new PlatformChanges { OfficialAppId = FieldEdit<string>.Set("synthetic-next") }, null, null, null, null));
            runtime.Apply(settings => settings.OfficialAppId = "synthetic-next");
            Require(State(await Read(), ConfigurationFieldId.OfficialAppId).State == ConfigurationRuntimeState.Current,
                "restart_snapshot_clears_pending");
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            var metadata = JsonSerializer.Serialize(pending.RuntimeEffects, options);
            Require(metadata.Contains("restartRequired", StringComparison.Ordinal) && metadata.Contains("botAgent", StringComparison.Ordinal)
                && !metadata.Contains("synthetic-next", StringComparison.Ordinal) && !metadata.Contains("10001", StringComparison.Ordinal)
                && !metadata.Contains("editable", StringComparison.Ordinal), "metadata_contains_no_values");
            var overview = await store.ReadAsync(caller, ConfigurationReadKind.Overview);
            Require(overview.RuntimeEffects!.Value.Length == before.RuntimeEffects!.Value.Length, "overview_visible_fields");
            var runtimeEvidence = (ConfigurationRuntimeTracker)typeof(SettingsBox)
                .GetProperty("RuntimeEffects", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(runtime)!;
            runtimeEvidence.BeginApply(projectTts: false);
            runtimeEvidence.Record(new(ApplyStep.ModelProjection, ApplyState.Failed, "synthetic_failure"));
            var latestApplyState = (await Read()).LatestApplyState;
            foreach (var kind in new[] { ConfigurationReadKind.Overview, ConfigurationReadKind.Editable })
            {
                var failed = await store.ReadAsync(caller, kind);
                Require(State(failed, ConfigurationFieldId.Model).State == ConfigurationRuntimeState.ApplyFailed,
                    "read_projection_failure_honest_" + kind);
                Require(State(failed, ConfigurationFieldId.MaxContextMessages).State == ConfigurationRuntimeState.ApplyFailed,
                    "brain_projection_failure_honest_" + kind);
                Require(State(failed, ConfigurationFieldId.MaxConcurrentReplies).State == ConfigurationRuntimeState.Unknown,
                    "inflight_gate_not_confirmed_" + kind);
                Require(failed.LatestApplyState == latestApplyState, "runtime_evidence_does_not_rewrite_receipt");
                var wire = JsonSerializer.Serialize(failed, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                using var document = JsonDocument.Parse(wire);
                var runtimeEffects = document.RootElement.GetProperty("runtimeEffects");
                Require(runtimeEffects.EnumerateArray().Any(item => item.GetProperty("state").GetString() == "applyFailed"),
                    "runtime_state_string_wire");
                Require(runtimeEffects.EnumerateArray().All(item => !item.TryGetProperty("desired", out _)
                    && !item.TryGetProperty("active", out _) && !item.TryGetProperty("value", out _)),
                    "runtime_effects_are_value_free");
            }
            authorized = false;
            Require((await Read()).RuntimeEffects is null, "denied_read_omits_metadata");
            CheckTracker();
            CheckPolicies();
            Console.WriteLine("CFG_RUNTIME_EFFECTS_OK");
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine("CFG_RUNTIME_EFFECTS_FAILED " + error.GetType().Name + " " + error.Message);
            return 1;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            var full = Path.GetFullPath(root);
            if (Path.GetDirectoryName(full) == Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar)
                && Path.GetFileName(full).StartsWith("botagent-runtime-effects-", StringComparison.Ordinal))
                Directory.Delete(full, recursive: true);
        }
    }

    private static void CheckTracker()
    {
        var tracker = new ConfigurationRuntimeTracker();
        ConfigurationRuntimeState State(ConfigurationFieldId field, FieldEffect effect, bool equal = true)
            => ConfigurationRuntimeTracker.Resolve(field, effect, equal, tracker.Snapshot());
        tracker.BeginApply(projectTts: true);
        Require(State(ConfigurationFieldId.Model, FieldEffect.Hot) == ConfigurationRuntimeState.Unknown, "inflight_unknown");
        tracker.Record(new(ApplyStep.ModelProjection, ApplyState.Failed, "synthetic_failure"));
        Require(State(ConfigurationFieldId.Model, FieldEffect.Hot) == ConfigurationRuntimeState.ApplyFailed, "failed_model_honest");
        tracker.Record(new(ApplyStep.TtsFileProjection, ApplyState.Failed, "synthetic_failure"));
        tracker.BeginApply(projectTts: false);
        Require(State(ConfigurationFieldId.TtsModel, FieldEffect.ExternalProjection) == ConfigurationRuntimeState.ApplyFailed,
            "unrelated_save_preserves_external_failure");
        tracker.Record(new(ApplyStep.ModelProjection, ApplyState.Unknown, "synthetic_unknown"));
        Require(State(ConfigurationFieldId.Model, FieldEffect.Hot) == ConfigurationRuntimeState.Unknown, "unknown_model_honest");
        tracker.Record(new(ApplyStep.ModelProjection, ApplyState.Applied, null));
        Require(State(ConfigurationFieldId.Model, FieldEffect.Hot) == ConfigurationRuntimeState.Current, "confirmed_model_current");
        Require(State(ConfigurationFieldId.OfficialAppId, FieldEffect.Restart, false) == ConfigurationRuntimeState.RestartRequired,
            "restart_difference_survives_apply_ledger");
        Require(State(ConfigurationFieldId.Model, FieldEffect.Hot, false) == ConfigurationRuntimeState.Unknown,
            "different_snapshot_not_confirmed_by_old_success");
        Require(State(ConfigurationFieldId.AiDesire, FieldEffect.Hot, false) == ConfigurationRuntimeState.Unknown,
            "unconfirmed_hot_difference_unknown");
        var dependencies = new (ConfigurationFieldId Field, ApplyStep Step)[]
        {
            (ConfigurationFieldId.FastModel, ApplyStep.PublishSnapshot),
            (ConfigurationFieldId.WhitelistGroups, ApplyStep.RuntimePolicy),
            (ConfigurationFieldId.AgentAllowedUsers, ApplyStep.RuntimePolicy),
            (ConfigurationFieldId.FeishuWhitelistPrivates, ApplyStep.RuntimePolicy),
            (ConfigurationFieldId.EnableApprovals, ApplyStep.RuntimePolicy),
            (ConfigurationFieldId.EnableWebSearch, ApplyStep.RuntimePolicy),
            (ConfigurationFieldId.ParticipationCooldownSeconds, ApplyStep.RuntimePolicy),
            (ConfigurationFieldId.BotPersona, ApplyStep.ModelProjection),
            (ConfigurationFieldId.MaxContextMessages, ApplyStep.ModelProjection),
            (ConfigurationFieldId.MaxConcurrentReplies, ApplyStep.ConcurrencyGate),
            (ConfigurationFieldId.MaxMessagesPerConversation, ApplyStep.ContextWindow),
            (ConfigurationFieldId.ProfileSummaryIntervalSeconds, ApplyStep.Schedulers),
            (ConfigurationFieldId.HealthReportTime, ApplyStep.Schedulers),
            (ConfigurationFieldId.IdleFallbackSeconds, ApplyStep.Schedulers),
            (ConfigurationFieldId.EnableProfileSummary, ApplyStep.Schedulers),
            (ConfigurationFieldId.StickerLibraryMax, ApplyStep.Schedulers),
        };
        foreach (var (field, step) in dependencies)
        {
            var evidence = new ConfigurationRuntimeTracker();
            evidence.BeginApply(projectTts: false);
            ConfigurationRuntimeState Resolve() => ConfigurationRuntimeTracker.Resolve(field, FieldEffect.Hot, true, evidence.Snapshot());
            Require(Resolve() == ConfigurationRuntimeState.Unknown, "dependency_inflight_" + field);
            evidence.Record(new(step, ApplyState.Failed, "synthetic_failure"));
            Require(Resolve() == ConfigurationRuntimeState.ApplyFailed, "dependency_failed_" + field);
            evidence.Record(new(step, ApplyState.Applied, null));
            Require(Resolve() == ConfigurationRuntimeState.Current, "dependency_confirmed_" + field);
            evidence.BeginApply(projectTts: false);
            Require(Resolve() == ConfigurationRuntimeState.Unknown, "new_apply_invalidates_previous_confirmation_" + field);
        }
        var combined = new ConfigurationRuntimeTracker();
        combined.BeginApply(projectTts: false);
        ConfigurationRuntimeState StickerState() => ConfigurationRuntimeTracker.Resolve(
            ConfigurationFieldId.EnableStickers, FieldEffect.Hot, true, combined.Snapshot());
        combined.Record(new(ApplyStep.RuntimePolicy, ApplyState.Applied, null));
        Require(StickerState() == ConfigurationRuntimeState.Unknown, "scheduler_also_requires_confirmation");
        combined.Record(new(ApplyStep.Schedulers, ApplyState.Failed, "synthetic_failure"));
        Require(StickerState() == ConfigurationRuntimeState.ApplyFailed, "scheduler_failure_not_hidden_by_policy_success");
        combined.Record(new(ApplyStep.Schedulers, ApplyState.Applied, null));
        Require(StickerState() == ConfigurationRuntimeState.Current, "both_sticker_dependencies_confirmed");
    }

    private static void CheckPolicies()
    {
        var key = new DriverKey("local", "legacy", "synthetic-main");
        var account = new PolicyTarget.AccountDefault(key.PlatformId, key.AccountScope);
        var instance = new PolicyTarget.Instance(key);
        PlatformPolicyConfiguration Rules(ListRule group) => new(ToggleRule.Enabled, ToggleRule.Enabled,
            group, ListRule.AllowAll(), new(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty),
            ImmutableArray<FeatureOverride>.Empty);
        var initialRule = Rules(ListRule.Explicit("10001"));
        var runtime = new AppSettings { DeclaredPlatformDrivers = ImmutableArray.Create(key),
            RuntimePlatformPolicies = ImmutableArray.Create(new PlatformPolicyEntry(account, initialRule),
                new PlatformPolicyEntry(instance, initialRule)) };
        var desired = runtime.Snapshot();
        desired.RuntimePlatformPolicies = ImmutableArray.Create(
            new PlatformPolicyEntry(account, Rules(ListRule.Explicit(string.Empty))), new PlatformPolicyEntry(instance, initialRule));
        var collectorType = typeof(SettingsStore).Assembly.GetType("BotAgent.Adapters.Persistence.ConfigurationRuntimeEffectCollector")!;
        var method = typeof(SettingsStore).GetMethod("PolicyViews", BindingFlags.NonPublic | BindingFlags.Static)!;
        ImmutableArray<ConfigurationRuntimeEffect> Effects(AppSettings wanted, AppSettings actual,
            ImmutableDictionary<ApplyStep, ApplyState>? steps = null)
        {
            var collector = Activator.CreateInstance(collectorType, BindingFlags.Instance | BindingFlags.NonPublic,
                null, new object[] { steps ?? ImmutableDictionary<ApplyStep, ApplyState>.Empty }, null)!;
            method.Invoke(null, new object[] { wanted, actual, ConfigurationReadKind.Editable, true, collector });
            collectorType.GetMethod("CompletePolicies", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collector, null);
            return (ImmutableArray<ConfigurationRuntimeEffect>)collectorType
                .GetMethod("Build", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(collector, null)!;
        }
        var effects = Effects(desired, runtime);
        var globalState = effects.Single(item => item.Field.Object is ConfigurationObjectAddress.Policy policy && policy.Target == account);
        var instanceState = effects.Single(item => item.Field.Object is ConfigurationObjectAddress.Policy policy && policy.Target == instance);
        Require(globalState.State == ConfigurationRuntimeState.RestartRequired, "explicit_empty_differs_from_nonempty");
        Require(instanceState.State == ConfigurationRuntimeState.Current, "instance_override_preserves_effective_policy");
        Require(Effects(runtime, runtime).All(item => item.State == ConfigurationRuntimeState.Current), "policy_revert_current");
        Require(Effects(desired, desired).All(item => item.State == ConfigurationRuntimeState.Current), "policy_restart_current");
        var failed = ImmutableDictionary<ApplyStep, ApplyState>.Empty.Add(ApplyStep.RuntimePolicy, ApplyState.Failed);
        var unknown = failed.SetItem(ApplyStep.RuntimePolicy, ApplyState.Unknown);
        var applied = failed.SetItem(ApplyStep.RuntimePolicy, ApplyState.Applied);
        Require(Effects(desired, desired, failed).All(item => item.State == ConfigurationRuntimeState.ApplyFailed),
            "published_policy_not_current_when_gate_failed");
        Require(Effects(desired, desired, unknown).All(item => item.State == ConfigurationRuntimeState.Unknown),
            "published_policy_not_current_while_gate_inflight");
        Require(Effects(desired, desired, applied).All(item => item.State == ConfigurationRuntimeState.Current),
            "published_policy_current_after_gate_confirmation");
        Require(Effects(desired, runtime, failed).Any(item => item.State == ConfigurationRuntimeState.RestartRequired),
            "cold_policy_difference_still_requires_restart");
        desired.RuntimePlatformPolicies = ImmutableArray.Create(new PlatformPolicyEntry(account, Rules(ListRule.AllowAll())),
            new PlatformPolicyEntry(instance, initialRule));
        Require(Effects(desired, runtime).Any(item => item.State == ConfigurationRuntimeState.RestartRequired),
            "allow_all_is_distinct_from_explicit_list");
    }

    private static void Require(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
    }
}
