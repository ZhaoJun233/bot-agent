using BotAgent.Domain.Platforms;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Qq;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;

namespace BotAgent.Services.Platforms;

/// <summary>Versioned compatibility bridge for platform and chat switches.</summary>
public static class PlatformSwitchSettings
{
    public static (bool Enabled, bool ChatEnabled) Read(PlatformOptions settings, string platform, string account)
    {
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(account)) return (false, false);
        platform = PlatformId.Normalize(platform);
        if (settings.RuntimePlatformPolicies is { } typed)
        {
            // A compatibility account view is not an authenticated instance or a loaded-state decision.
            if (typed.IsDefault || !IsCanonical(platform, account) || settings.DeclaredPlatformDrivers.IsDefault
                || settings.DeclaredPlatformDrivers.Count(key => ValidDriver(key)
                    && PlatformId.Normalize(key.PlatformId) == platform && key.AccountScope == account) != 1
                || typed.Any(entry => entry.Target is PolicyTarget.Instance instance
                    && PlatformId.Normalize(instance.Key.PlatformId) == platform && instance.Key.AccountScope == account))
                return (false, false);
            var rules = ResolvePolicyRules(settings, new PolicyTarget.AccountDefault(platform, account), ImmutableArray<ActionId>.Empty);
            return (rules.Enabled == ToggleRule.Enabled, rules.ChatEnabled == ToggleRule.Enabled);
        }
        if (platform is not (PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Feishu or PlatformId.Local))
            return (false, false);

        var policy = Find(settings, platform, account);
        var legacy = Legacy(settings, platform);
        return settings.PlatformSwitchSchemaVersion < 1
            ? ((policy?.Enabled ?? true) && legacy.Enabled, (policy?.ChatEnabled ?? true) && legacy.ChatEnabled)
            : (policy?.Enabled ?? legacy.Enabled, policy?.ChatEnabled ?? legacy.ChatEnabled);
    }

    public static void Normalize(PlatformOptions settings)
    {
        if (settings.RuntimePlatformPolicies.HasValue) return;
        settings.PlatformPolicies ??= new();
        if (settings.PlatformSwitchSchemaVersion < 1)
        {
            // Calculate all intersections before mirroring any legacy fields.
            foreach (var policy in settings.PlatformPolicies.Where(p => p is not null))
            {
                var switches = Read(settings, policy.PlatformId, policy.AccountScope);
                policy.Enabled = switches.Enabled;
                policy.ChatEnabled = switches.ChatEnabled;
            }
            settings.PlatformSwitchSchemaVersion = 1;
        }
        SynchronizeLegacy(settings);
    }

    public static void SynchronizeLegacy(PlatformOptions settings)
    {
        if (settings.RuntimePlatformPolicies.HasValue) return;
        var official = Find(settings, PlatformId.QqOfficial, AccountScope.Legacy);
        if (official?.Enabled is bool officialEnabled) settings.OfficialEnabled = officialEnabled;
        if (official?.ChatEnabled is bool officialChat) settings.OfficialChatEnabled = officialChat;
        var qq = Find(settings, PlatformId.QqPrivate, AccountScope.Legacy);
        if (qq?.ChatEnabled is bool privateChat) settings.PrivateChatEnabled = privateChat;
        var feishu = Find(settings, PlatformId.Feishu, AccountScope.Default);
        if (feishu?.Enabled is bool feishuEnabled) settings.FeishuEnabled = feishuEnabled;
    }

    public static void ApplyLegacy(PlatformOptions settings, string platform, string account, bool? enabled, bool? chatEnabled)
    {
        var policy = Find(settings, platform, account);
        if (policy is null) return;
        if (enabled is bool e) policy.Enabled = e;
        if (chatEnabled is bool c) policy.ChatEnabled = c;
    }

    public static List<PlatformPolicySettings> EditablePolicies(PlatformOptions settings)
    {
        if (settings.RuntimePlatformPolicies.HasValue)
        {
            // The old panel gets an isolated canonical projection, never a second policy source.
            return new[] { (PlatformId.QqPrivate, AccountScope.Legacy), (PlatformId.QqOfficial, AccountScope.Legacy),
                (PlatformId.Feishu, AccountScope.Default), (PlatformId.Local, AccountScope.Legacy) }
                .Select(key =>
                {
                    var switches = Read(settings, key.Item1, key.Item2);
                    var rules = ResolvePolicyRules(settings, new PolicyTarget.AccountDefault(key.Item1, key.Item2), ImmutableArray<ActionId>.Empty);
                    return new PlatformPolicySettings { PlatformId = key.Item1, AccountScope = key.Item2,
                        Enabled = switches.Enabled, ChatEnabled = switches.ChatEnabled, InheritActionAllowlist = true,
                        GroupWhitelist = rules.GroupWhitelist.TryGetExplicitValue(out var group) ? group : string.Empty,
                        PrivateWhitelist = rules.PrivateWhitelist.TryGetExplicitValue(out var direct) ? direct : string.Empty };
                }).ToList();
        }
        var policies = (settings.PlatformPolicies ?? new()).Where(p => p is not null).Select(p => p.Clone()).ToList();
        foreach (var policy in policies)
        {
            var switches = Read(settings, policy.PlatformId, policy.AccountScope);
            policy.Enabled = switches.Enabled;
            policy.ChatEnabled = switches.ChatEnabled;
        }
        foreach (var (platform, account) in new[]
        {
            (PlatformId.QqPrivate, AccountScope.Legacy), (PlatformId.QqOfficial, AccountScope.Legacy),
            (PlatformId.Feishu, AccountScope.Default), (PlatformId.Local, AccountScope.Legacy),
        })
        {
            var policy = policies.FirstOrDefault(p => PlatformId.Normalize(p.PlatformId) == platform
                && string.Equals(string.IsNullOrWhiteSpace(p.AccountScope) ? AccountScope.Default : p.AccountScope,
                    account, StringComparison.OrdinalIgnoreCase));
            if (policy is null)
            {
                policy = new PlatformPolicySettings
                {
                    PlatformId = platform,
                    AccountScope = account,
                    InheritActionAllowlist = true,
                    GroupWhitelist = platform switch
                    {
                        PlatformId.QqPrivate => !string.IsNullOrWhiteSpace(settings.WhitelistGroups) ? settings.WhitelistGroups : settings.MessageWhitelist,
                        PlatformId.QqOfficial => settings.OfficialWhitelistGroups,
                        PlatformId.Feishu => settings.FeishuWhitelist,
                        PlatformId.Local => settings.LocalChannelIds,
                        _ => string.Empty,
                    },
                    PrivateWhitelist = platform switch
                    {
                        PlatformId.QqPrivate => !string.IsNullOrWhiteSpace(settings.WhitelistPrivates) ? settings.WhitelistPrivates : settings.MessageWhitelist,
                        PlatformId.QqOfficial => settings.OfficialWhitelistPrivates,
                        _ => string.Empty,
                    },
                };
                policies.Add(policy);
            }
            policy.PlatformId = platform;
            policy.AccountScope = account;
            var switches = Read(settings, platform, account);
            policy.Enabled = switches.Enabled;
            policy.ChatEnabled = switches.ChatEnabled;
        }
        return policies;
    }

    /// <summary>One-time, pure import of pre-normalization compatibility facts. No writes or loaded-registry lookup.</summary>
    public static (ConfigurationResultCode Code, ImmutableArray<PlatformPolicyEntry> Entries, ImmutableArray<FieldError> Errors)
        ImportLegacyPolicies(PlatformOptions legacy, ImmutableArray<DriverKey> declaredDrivers,
            ImmutableArray<ActionId> qqActionBaseline)
    {
        ArgumentNullException.ThrowIfNull(legacy);
        var entries = ImmutableArray.CreateBuilder<PlatformPolicyEntry>();
        var seen = new HashSet<PolicyTarget>();
        foreach (var policy in legacy.PlatformPolicies ?? new())
        {
            if (policy is null || string.IsNullOrWhiteSpace(policy.PlatformId) || declaredDrivers.IsDefault)
                return ImportFailure("legacy_policy_needs_review");
            var platform = PlatformId.Normalize(policy.PlatformId);
            var account = string.IsNullOrWhiteSpace(policy.AccountScope) ? AccountScope.Default : policy.AccountScope;
            var candidates = declaredDrivers.Where(key => ValidDriver(key)
                    && PlatformId.Normalize(key.PlatformId) == platform
                    && string.Equals(key.AccountScope, account, StringComparison.OrdinalIgnoreCase))
                .Select(key => new PolicyTarget.AccountDefault(platform, key.AccountScope)).Distinct().Take(2).ToArray();
            if (candidates.Length != 1 || !IsCanonical(platform, candidates[0].AccountScope) || !seen.Add(candidates[0]))
                return ImportFailure("legacy_policy_needs_review");
            var target = candidates[0];
            var scalar = Legacy(legacy, platform);
            ToggleRule ImportToggle(bool? value, bool baseline) => value is null ? ToggleRule.Inherit
                : ToToggle(legacy.PlatformSwitchSchemaVersion < 1 ? value.Value && baseline : value.Value);
            var group = string.IsNullOrWhiteSpace(policy.GroupWhitelist) ? ListRule.Inherit() : ListRule.Explicit(policy.GroupWhitelist);
            var direct = string.IsNullOrWhiteSpace(policy.PrivateWhitelist) ? ListRule.Inherit() : ListRule.Explicit(policy.PrivateWhitelist);
            if (platform == PlatformId.Local
                && (!string.IsNullOrWhiteSpace(policy.GroupWhitelist) || !string.IsNullOrWhiteSpace(policy.PrivateWhitelist)))
            {
                // The old local gate consumed the numeric union, never the wildcard/all flag.
                group = ListRule.Explicit(LocalNumbers($"{policy.GroupWhitelist},{policy.PrivateWhitelist}"));
                direct = group;
            }
            var features = ImmutableArray.CreateBuilder<FeatureOverride>();
            foreach (var feature in policy.FeatureOverrides ?? new())
            {
                if (!Enum.TryParse<PlatformFeatureId>(feature.Key, true, out var id) || !Enum.IsDefined(id)
                    || !string.Equals(id.ToString(), feature.Key, StringComparison.OrdinalIgnoreCase))
                    return ImportFailure("legacy_feature_needs_review");
                features.Add(new FeatureOverride(id, feature.Value));
            }
            var actions = (policy.AllowedActions ?? new()).Select(action => action?.Trim() ?? string.Empty)
                .Select(action => new ActionId(action)).ToImmutableArray();
            var actionRule = policy.InheritActionAllowlist && actions.Length == 0
                ? new ActionRule(ActionRuleKind.Inherit, ImmutableArray<ActionId>.Empty)
                : new ActionRule(ActionRuleKind.Explicit, actions);
            entries.Add(new PlatformPolicyEntry(target, new PlatformPolicyConfiguration(
                ImportToggle(policy.Enabled, scalar.Enabled), ImportToggle(policy.ChatEnabled, scalar.ChatEnabled),
                group, direct, actionRule, features.OrderBy(feature => feature.Feature).ToImmutableArray())));
        }
        var imported = entries.ToImmutable();
        var errors = ValidatePolicyEntries(imported, declaredDrivers, legacy.DeclaredPlatformActions);
        return errors.Length == 0 ? (ConfigurationResultCode.Ok, imported, errors)
            : (ConfigurationResultCode.LegacyNeedsReview, ImmutableArray<PlatformPolicyEntry>.Empty, errors);
    }

    public static ImmutableArray<FieldError> ValidatePolicyEntries(ImmutableArray<PlatformPolicyEntry> entries,
        ImmutableArray<DriverKey> declaredDrivers, ImmutableArray<ActionId> registeredActions)
    {
        var errors = ImmutableArray.CreateBuilder<FieldError>();
        if (entries.IsDefault || entries.Length > 32 || declaredDrivers.IsDefault || registeredActions.IsDefault
            || declaredDrivers.Any(key => !ValidDriver(key))
            || declaredDrivers.Select(NormalizedDriver).Distinct().Count() != declaredDrivers.Length
            || registeredActions.Any(action => !ValidText(action.Value, 80, false))
            || registeredActions.Distinct().Count() != registeredActions.Length)
            return ImmutableArray.Create(PolicyError("invalid_policy_catalog"));
        var seen = new HashSet<PolicyTarget>();
        var actions = registeredActions.ToHashSet();
        foreach (var entry in entries)
        {
            if (entry is null || !TryNormalizeTarget(entry.Target, out var target)
                || !Declared(target, declaredDrivers) || !seen.Add(target))
            {
                errors.Add(PolicyError("invalid_policy_target", entry?.Target));
                continue;
            }
            var policy = entry.Configuration;
            if (policy is null || !Enum.IsDefined(policy.Enabled) || !Enum.IsDefined(policy.ChatEnabled)
                || !ValidList(policy.GroupWhitelist) || !ValidList(policy.PrivateWhitelist)
                || policy.AllowedActions is null || !Enum.IsDefined(policy.AllowedActions.Kind)
                || policy.AllowedActions.Actions.IsDefault || policy.AllowedActions.Actions.Length > 64
                || policy.AllowedActions.Actions.Any(action => !actions.Contains(action))
                || policy.AllowedActions.Actions.Distinct().Count() != policy.AllowedActions.Actions.Length
                || (policy.AllowedActions.Kind == ActionRuleKind.Inherit && policy.AllowedActions.Actions.Length != 0)
                || policy.Features.IsDefault || policy.Features.Length > 32
                || policy.Features.Any(feature => feature is null || !Enum.IsDefined(feature.Feature))
                || policy.Features.Select(feature => feature.Feature).Distinct().Count() != policy.Features.Length)
                errors.Add(PolicyError("invalid_policy_configuration", target));
        }
        return errors.Take(32).ToImmutableArray();
    }

    public static PlatformPolicyConfiguration ResolvePolicyRules(PlatformOptions settings, PlatformContext context,
        ImmutableArray<ActionId> qqActionBaseline)
        => ResolvePolicyRules(settings, context is null ? null!
            : new PolicyTarget.Instance(new DriverKey(context.PlatformId, context.AccountScope, context.InstanceId)), qqActionBaseline);

    public static PlatformPolicyConfiguration ResolvePolicyRules(PlatformOptions settings, PolicyTarget target,
        ImmutableArray<ActionId> qqActionBaseline)
    {
        var resolved = ResolvePolicy(settings, target, qqActionBaseline);
        var allowed = qqActionBaseline.IsDefault ? ImmutableArray<ActionId>.Empty : qqActionBaseline;
        return resolved.Rules with
        {
            AllowedActions = new ActionRule(ActionRuleKind.Explicit, resolved.Rules.AllowedActions.Actions
                .Where(action => allowed.Contains(action)).OrderBy(action => action.Value, StringComparer.Ordinal).ToImmutableArray()),
        };
    }

    public static (ConfigurationValueSource Enabled, ConfigurationValueSource ChatEnabled,
        ConfigurationValueSource GroupWhitelist, ConfigurationValueSource PrivateWhitelist,
        ConfigurationValueSource AllowedActions, ConfigurationValueSource Features)
        ResolvePolicySources(PlatformOptions settings, PolicyTarget target)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var resolved = ResolvePolicy(settings, target, settings.DeclaredPlatformActions);
        var featureSources = resolved.FeatureSources.Values.Distinct().Take(2).ToArray();
        var features = featureSources.Length == 0 ? ConfigurationValueSource.Default
            : featureSources.Length == 1 ? featureSources[0] : ConfigurationValueSource.Unclassified;
        return (resolved.Enabled, resolved.ChatEnabled, resolved.GroupWhitelist, resolved.PrivateWhitelist,
            resolved.AllowedActions, features);
    }

    private sealed record ResolvedPolicy(PlatformPolicyConfiguration Rules,
        ConfigurationValueSource Enabled, ConfigurationValueSource ChatEnabled,
        ConfigurationValueSource GroupWhitelist, ConfigurationValueSource PrivateWhitelist,
        ConfigurationValueSource AllowedActions, ImmutableDictionary<PlatformFeatureId, ConfigurationValueSource> FeatureSources)
    {
        public override string ToString() => "ResolvedPolicy";
    }

    private static ResolvedPolicy ResolvePolicy(PlatformOptions settings, PolicyTarget requested,
        ImmutableArray<ActionId> qqActionBaseline)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!TryNormalizeTarget(requested, out var target) || !Declared(target, settings.DeclaredPlatformDrivers))
            return Denied();
        ImmutableArray<PlatformPolicyEntry> entries;
        if (settings.RuntimePlatformPolicies is { } runtime) entries = runtime;
        else
        {
            var imported = ImportLegacyPolicies(settings, settings.DeclaredPlatformDrivers, qqActionBaseline);
            if (imported.Code != ConfigurationResultCode.Ok) return Denied();
            entries = imported.Entries;
        }
        if (ValidatePolicyEntries(entries, settings.DeclaredPlatformDrivers, settings.DeclaredPlatformActions).Length != 0)
            return Denied();
        var account = target is PolicyTarget.AccountDefault accountTarget ? accountTarget
            : new PolicyTarget.AccountDefault(((PolicyTarget.Instance)target).Key.PlatformId, ((PolicyTarget.Instance)target).Key.AccountScope);
        var resolved = Baseline(settings, account, qqActionBaseline);
        var accountEntry = entries.FirstOrDefault(entry => NormalizedTarget(entry.Target) == account);
        if (accountEntry is not null) resolved = Overlay(settings, resolved, accountEntry);
        if (target is PolicyTarget.Instance)
        {
            var instanceEntry = entries.FirstOrDefault(entry => NormalizedTarget(entry.Target) == target);
            if (instanceEntry is not null) resolved = Overlay(settings, resolved, instanceEntry);
        }
        return resolved;
    }

    private static ResolvedPolicy Overlay(PlatformOptions settings, ResolvedPolicy parent, PlatformPolicyEntry entry)
    {
        var policy = entry.Configuration;
        var hasOrigin = settings.RuntimePlatformPolicyExplicitFields.TryGetValue(NormalizedTarget(entry.Target), out var bits);
        ConfigurationValueSource Source(int bit) => !settings.RuntimePlatformPolicies.HasValue
            ? ConfigurationValueSource.LegacyCompatibility
            : !hasOrigin || bits is < 0 or > 63 ? ConfigurationValueSource.Unclassified
            : (bits & bit) != 0 ? ConfigurationValueSource.UserConfiguration : ConfigurationValueSource.LegacyCompatibility;
        var features = parent.Rules.Features.ToDictionary(feature => feature.Feature);
        var featureSources = parent.FeatureSources.ToBuilder();
        foreach (var feature in policy.Features)
        {
            features[feature.Feature] = feature;
            featureSources[feature.Feature] = Source(32);
        }
        return new ResolvedPolicy(new PlatformPolicyConfiguration(
            policy.Enabled == ToggleRule.Inherit ? parent.Rules.Enabled : policy.Enabled,
            policy.ChatEnabled == ToggleRule.Inherit ? parent.Rules.ChatEnabled : policy.ChatEnabled,
            policy.GroupWhitelist.Kind == ListRuleKind.Inherit ? parent.Rules.GroupWhitelist : policy.GroupWhitelist,
            policy.PrivateWhitelist.Kind == ListRuleKind.Inherit ? parent.Rules.PrivateWhitelist : policy.PrivateWhitelist,
            policy.AllowedActions.Kind == ActionRuleKind.Inherit ? parent.Rules.AllowedActions : policy.AllowedActions,
            features.Values.OrderBy(feature => feature.Feature).ToImmutableArray()),
            policy.Enabled == ToggleRule.Inherit ? parent.Enabled : Source(1),
            policy.ChatEnabled == ToggleRule.Inherit ? parent.ChatEnabled : Source(2),
            policy.GroupWhitelist.Kind == ListRuleKind.Inherit ? parent.GroupWhitelist : Source(4),
            policy.PrivateWhitelist.Kind == ListRuleKind.Inherit ? parent.PrivateWhitelist : Source(8),
            policy.AllowedActions.Kind == ActionRuleKind.Inherit ? parent.AllowedActions : Source(16), featureSources.ToImmutable());
    }

    private static ResolvedPolicy Baseline(PlatformOptions settings, PolicyTarget.AccountDefault target,
        ImmutableArray<ActionId> qqActionBaseline)
    {
        var platform = target.PlatformId;
        if (!IsCanonical(platform, target.AccountScope)) return Denied();
        var toggles = Legacy(settings, platform);
        ConfigurationValueSource Source(ConfigurationFieldId field) => settings.RuntimePlatformPolicyBaselineSources.TryGetValue(field, out var source)
            && Enum.IsDefined(source) ? source : ConfigurationValueSource.Unclassified;
        var enabledSource = platform switch
        {
            PlatformId.QqPrivate => ConfigurationValueSource.Default,
            PlatformId.QqOfficial => Source(ConfigurationFieldId.OfficialEnabled),
            PlatformId.Feishu => Source(ConfigurationFieldId.FeishuEnabled),
            _ => Source(ConfigurationFieldId.LocalChannelIds),
        };
        var chatSource = platform switch
        {
            PlatformId.QqPrivate => Source(ConfigurationFieldId.PrivateChatEnabled),
            PlatformId.QqOfficial => Source(ConfigurationFieldId.OfficialChatEnabled),
            _ => enabledSource,
        };
        var groupField = platform switch
        {
            PlatformId.QqPrivate => string.IsNullOrWhiteSpace(settings.WhitelistGroups) ? ConfigurationFieldId.MessageWhitelist : ConfigurationFieldId.WhitelistGroups,
            PlatformId.QqOfficial => ConfigurationFieldId.OfficialWhitelistGroups,
            PlatformId.Feishu => ConfigurationFieldId.FeishuWhitelist,
            _ => ConfigurationFieldId.LocalChannelIds,
        };
        var privateField = platform switch
        {
            PlatformId.QqPrivate => string.IsNullOrWhiteSpace(settings.WhitelistPrivates) ? ConfigurationFieldId.MessageWhitelist : ConfigurationFieldId.WhitelistPrivates,
            PlatformId.QqOfficial => ConfigurationFieldId.OfficialWhitelistPrivates,
            _ => groupField,
        };
        var group = platform switch
        {
            PlatformId.QqPrivate => ListRule.Explicit((groupField == ConfigurationFieldId.MessageWhitelist ? settings.MessageWhitelist : settings.WhitelistGroups) ?? string.Empty),
            PlatformId.QqOfficial => string.IsNullOrWhiteSpace(settings.OfficialWhitelistGroups) ? ListRule.AllowAll() : ListRule.Explicit(settings.OfficialWhitelistGroups),
            PlatformId.Feishu => ListRule.Explicit(settings.FeishuWhitelist ?? string.Empty),
            _ => ListRule.Explicit(LocalNumbers(settings.LocalChannelIds)),
        };
        var direct = platform switch
        {
            PlatformId.QqPrivate => ListRule.Explicit((privateField == ConfigurationFieldId.MessageWhitelist ? settings.MessageWhitelist : settings.WhitelistPrivates) ?? string.Empty),
            PlatformId.QqOfficial => string.IsNullOrWhiteSpace(settings.OfficialWhitelistPrivates) ? ListRule.AllowAll() : ListRule.Explicit(settings.OfficialWhitelistPrivates),
            _ => group,
        };
        var qq = platform is PlatformId.QqPrivate or PlatformId.QqOfficial;
        var actions = qq && !qqActionBaseline.IsDefault && !settings.DeclaredPlatformActions.IsDefault
            ? qqActionBaseline.Where(action => settings.DeclaredPlatformActions.Contains(action)).Distinct()
                .OrderBy(action => action.Value, StringComparer.Ordinal).ToImmutableArray() : ImmutableArray<ActionId>.Empty;
        return new ResolvedPolicy(new PlatformPolicyConfiguration(ToToggle(toggles.Enabled), ToToggle(toggles.ChatEnabled),
            group, direct, new ActionRule(ActionRuleKind.Explicit, actions), ImmutableArray<FeatureOverride>.Empty),
            enabledSource, chatSource, Source(groupField), Source(privateField),
            qq ? Source(ConfigurationFieldId.AgentServerQqActions) : ConfigurationValueSource.Default,
            ImmutableDictionary<PlatformFeatureId, ConfigurationValueSource>.Empty);
    }

    private static ResolvedPolicy Denied() => new(new PlatformPolicyConfiguration(ToggleRule.Disabled, ToggleRule.Disabled,
        ListRule.Explicit(string.Empty), ListRule.Explicit(string.Empty), new ActionRule(ActionRuleKind.Explicit, ImmutableArray<ActionId>.Empty),
        ImmutableArray<FeatureOverride>.Empty), ConfigurationValueSource.Default, ConfigurationValueSource.Default,
        ConfigurationValueSource.Default, ConfigurationValueSource.Default, ConfigurationValueSource.Default,
        ImmutableDictionary<PlatformFeatureId, ConfigurationValueSource>.Empty);

    private static ToggleRule ToToggle(bool enabled) => enabled ? ToggleRule.Enabled : ToggleRule.Disabled;
    private static bool IsCanonical(string platform, string account) => platform switch
    {
        PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Local => account == AccountScope.Legacy,
        PlatformId.Feishu => account == AccountScope.Default,
        _ => false,
    };
    private static string LocalNumbers(string? raw) => string.Join(",", WhitelistPolicy.ParseWhitelist(raw).Ids
        .Where(id => Channels.LocalTarget(id) > 0).OrderBy(id => id).Select(id => id.ToString(CultureInfo.InvariantCulture)));
    private static bool ValidText(string? text, int maximum, bool empty)
    {
        if (text is null || text.Length > maximum || (!empty && string.IsNullOrWhiteSpace(text)) || text.Contains('\0')) return false;
        try { _ = new UTF8Encoding(false, true).GetByteCount(text); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    private static bool ValidDriver(DriverKey key) => ValidText(key.PlatformId, 128, false)
        && ValidText(key.AccountScope, 128, false) && ValidText(key.InstanceId, 128, true)
        && (key.InstanceId.Length == 0 || !string.IsNullOrWhiteSpace(key.InstanceId));
    private static DriverKey NormalizedDriver(DriverKey key) => key with { PlatformId = PlatformId.Normalize(key.PlatformId) };
    private static bool ValidList(ListRule? rule) => rule is not null && Enum.IsDefined(rule.Kind)
        && (!rule.TryGetExplicitValue(out var text) || ValidText(text, 4096, true));
    private static bool TryNormalizeTarget(PolicyTarget? target, out PolicyTarget normalized)
    {
        normalized = null!;
        switch (target)
        {
            case PolicyTarget.AccountDefault account when ValidText(account.PlatformId, 128, false) && ValidText(account.AccountScope, 128, false):
                normalized = account with { PlatformId = PlatformId.Normalize(account.PlatformId) };
                return true;
            case PolicyTarget.Instance instance when ValidDriver(instance.Key):
                normalized = new PolicyTarget.Instance(NormalizedDriver(instance.Key));
                return true;
            default: return false;
        }
    }
    private static PolicyTarget NormalizedTarget(PolicyTarget target) => TryNormalizeTarget(target, out var normalized) ? normalized : target;
    private static bool Declared(PolicyTarget target, ImmutableArray<DriverKey> drivers) => !drivers.IsDefault && drivers.Any(key => ValidDriver(key)
        && (target is PolicyTarget.Instance instance ? NormalizedDriver(key) == instance.Key
            : target is PolicyTarget.AccountDefault account && PlatformId.Normalize(key.PlatformId) == account.PlatformId && key.AccountScope == account.AccountScope));
    private static FieldError PolicyError(string code, PolicyTarget? target = null) => new(
        new FieldAddress(ConfigurationFieldId.PlatformPolicies, target is null ? null : new ConfigurationObjectAddress.Policy(target)), code);
    private static (ConfigurationResultCode Code, ImmutableArray<PlatformPolicyEntry> Entries, ImmutableArray<FieldError> Errors)
        ImportFailure(string code) => (ConfigurationResultCode.LegacyNeedsReview, ImmutableArray<PlatformPolicyEntry>.Empty,
            ImmutableArray.Create(PolicyError(code)));

    private static PlatformPolicySettings? Find(PlatformOptions settings, string platform, string account)
        => (settings.PlatformPolicies ?? new()).FirstOrDefault(p => p is not null
            && string.Equals(PlatformId.Normalize(p.PlatformId), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(string.IsNullOrWhiteSpace(p.AccountScope) ? AccountScope.Default : p.AccountScope,
                string.IsNullOrWhiteSpace(account) ? AccountScope.Default : account, StringComparison.OrdinalIgnoreCase));

    private static (bool Enabled, bool ChatEnabled) Legacy(PlatformOptions settings, string platform)
        => platform switch
        {
            PlatformId.QqPrivate => (true, settings.PrivateChatEnabled),
            PlatformId.QqOfficial => (settings.OfficialEnabled, settings.OfficialChatEnabled),
            PlatformId.Feishu => (settings.FeishuEnabled, settings.FeishuEnabled),
            PlatformId.Local => (!string.IsNullOrWhiteSpace(settings.LocalChannelIds), !string.IsNullOrWhiteSpace(settings.LocalChannelIds)),
            _ => (false, false),
        };
}
