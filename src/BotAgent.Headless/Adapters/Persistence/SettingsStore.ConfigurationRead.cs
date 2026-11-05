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
    private sealed class ConfigurationReadFields
    {
        private readonly Microsoft.Data.Sqlite.SqliteConnection _connection;
        private readonly ConfigurationReadKind _kind;
        private readonly ApplyState _latestApply;
        private readonly IReadOnlySet<string> _legacyProperties;
        private readonly Func<ConfigurationFieldId, ConfigurationValueSource> _actualSource;
        private readonly Func<ConfigurationFieldId, FieldEffect> _effect;
        private readonly ConfigurationRuntimeEffectCollector? _runtimeEffects;

        internal ConfigurationReadFields(Microsoft.Data.Sqlite.SqliteConnection connection,
            ConfigurationReadKind kind, ApplyState latestApply, IReadOnlySet<string> legacyProperties,
            Func<ConfigurationFieldId, ConfigurationValueSource> actualSource, Func<ConfigurationFieldId, FieldEffect> effect,
            ConfigurationRuntimeEffectCollector? runtimeEffects)
        {
            _connection = connection; _kind = kind; _latestApply = latestApply;
            _legacyProperties = legacyProperties; _actualSource = actualSource; _effect = effect; _runtimeEffects = runtimeEffects;
        }
        internal ReadField<T> Field<T>(ConfigurationFieldId id, T requested, T actual, string oldProperty,
            string? displayOverride = null)
        {
            var present = _legacyProperties.Contains(oldProperty);
            var state = ConfigurationBridge.ReadFieldState(_connection, id, present);
            var source = _actualSource(id);
            if (source == ConfigurationValueSource.UserConfiguration && !state.Tracked)
                source = present ? ConfigurationValueSource.LegacyCompatibility : ConfigurationValueSource.Default;
            var visibility = _kind == ConfigurationReadKind.Editable ? FieldVisibility.Editable : FieldVisibility.Hidden;
            var editable = visibility == FieldVisibility.Hidden ? default! : state.UserMode == UserValueKind.Explicit ? requested : actual;
            var display = displayOverride ?? (actual switch
            {
                string text => BotAgent.Services.Agent.AgentMask.Shorten(text),
                bool value => value ? "true" : "false",
                IFormattable number => number.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
                _ => "[configured]",
            });
            var rule = new FieldDescriptor(id, _effect(id), typeof(T) == typeof(string)
                || id is ConfigurationFieldId.PlatformPolicies,
                _kind == ConfigurationReadKind.Editable, present && !state.Tracked);
            _runtimeEffects?.Observe(id, rule.Effect,
                state.Tracked || state.UserMode == UserValueKind.Explicit ? requested : actual, actual);
            return ConfigurationBridge.ProjectField(visibility, display, rule, state.UserMode,
                source, _latestApply, editable);
        }
    }

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
                registered = _registeredRead(connection, kind);
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
