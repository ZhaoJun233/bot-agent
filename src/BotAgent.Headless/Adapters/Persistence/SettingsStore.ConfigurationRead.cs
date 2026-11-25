extern alias StorageModule;

using System.Collections.Immutable;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Services.Platforms;
using ConfigurationBridge = StorageModule::BotAgent.Storage.ConfigurationStorageBridge;
using StorageConfiguration = StorageModule::BotAgent.Storage;

namespace BotAgent.Adapters.Persistence;

public sealed partial class SettingsStore
{
    internal sealed partial class ConfigurationParticipant
    {
        internal override ConfigurationReadResult Read(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationCaller caller, ConfigurationReadKind kind, ConfigurationVersion version,
            ConfigurationGeneration generation, long serverUnixSeconds, ApplyState latestApplyState)
        {
            var stored = ReadUserFacts(connection);
            var secrets = SecretSources(connection, ImmutableArray<StorageConfiguration.SecretMutation>.Empty);
            var effective = _runtime.Current.Snapshot();
            if (ContainsEndpointUserInfo(stored.User.ModelBaseUrl) || ContainsEndpointUserInfo(stored.User.ModelBaseUrlOverride)
                || ContainsEndpointUserInfo(effective.ModelBaseUrl) || ContainsEndpointUserInfo(effective.ModelBaseUrlOverride))
                return new(ConfigurationResultCode.LegacyNeedsReview, version, generation, ConfigurationContract.Revision,
                    serverUnixSeconds, null, secrets, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
            RegisteredConfigurationView registered;
            try
            {
                ProjectPlatformPoliciesInTransaction(connection, stored.User);
                if (!stored.User.RuntimePlatformPolicies.HasValue && (stored.User.PlatformPolicies?.Count ?? 0) != 0)
                    throw new JsonException("platform_policy_legacy_needs_review");
                stored.User.DeclaredPlatformDrivers = effective.DeclaredPlatformDrivers;
                stored.User.DeclaredPlatformActions = effective.DeclaredPlatformActions;
                if (PlatformSwitchSettings.ValidatePolicyEntries(stored.User.RuntimePlatformPolicies ?? ImmutableArray<PlatformPolicyEntry>.Empty,
                    effective.DeclaredPlatformDrivers, effective.DeclaredPlatformActions).Length != 0)
                    throw new JsonException("platform_policy_legacy_needs_review");
                _ = ModelProviderStore.LoadFieldModesInTransaction(connection);
                stored.User.RuntimeConversationProfiles = StorageConfiguration.ConversationProfileStorageBridge.Load(connection);
                registered = _registeredRead(connection, kind);
                registered = registered with
                {
                    ConversationProfiles = ConversationProfileProjection.ProjectViews(
                        stored.User.RuntimeConversationProfiles, kind, effective.AgentMaskSensitive),
                };
            }
            catch (JsonException)
            {
                return new(ConfigurationResultCode.LegacyNeedsReview, version, generation, ConfigurationContract.Revision,
                    serverUnixSeconds, null, secrets, ImmutableArray<FieldDescriptor>.Empty, latestApplyState);
            }
            var runtimeEffects = new ConfigurationRuntimeEffectCollector(_runtime.RuntimeEffects.Snapshot());
            var requestedPolicies = PolicyViews(stored.User, effective, kind, true, runtimeEffects);
            var view = ProjectConfigurationView(connection, stored.User, effective, kind, latestApplyState,
                stored.Properties, _effectiveSource, _effect,
                requestedPolicies, PolicyViews(stored.User, effective, kind, false), registered, runtimeEffects);
            runtimeEffects.CompletePolicies();
            ConversationProfileProjection.ObserveRuntimeEffects(
                stored.User.RuntimeConversationProfiles, effective.RuntimeConversationProfiles, kind, runtimeEffects);
            view = view with
            {
                Registered = registered with
                {
                    Providers = registered.Providers.Select(provider => provider.Key.Value == "primary"
                        ? provider with { ModelName = view.Model.Model, BaseUrl = view.Model.ModelBaseUrl } : provider).ToImmutableArray(),
                },
            };
            return new(ConfigurationResultCode.Ok, version, generation, ConfigurationContract.Revision, serverUnixSeconds,
                view, secrets, CollectFieldDescriptors(view), latestApplyState) { RuntimeEffects = runtimeEffects.Build() };
        }
    }
}
