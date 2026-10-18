using global::BotAgent.Platforms;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Domain.Qq;
using BotAgent.Domain.Configuration;
using BotAgent.Services;
using System.Collections.Immutable;

namespace BotAgent.Services.Platforms;

/// <summary>
/// Combines adapter capabilities, account-scoped policy overrides, and legacy settings.
/// </summary>
public sealed class PlatformPolicyResolver : IPlatformPolicyResolver
{
    private readonly IPlatformSettingsBox _box;
    private readonly IPlatformRegistry? _registry;
    private readonly Func<PlatformOptions, ImmutableArray<ActionId>> _qqActionBaseline;

    public PlatformPolicyResolver(IPlatformSettingsBox box, IPlatformRegistry? registry = null)
        : this(box, registry, _ => ImmutableArray<ActionId>.Empty) { }

    public static PlatformPolicyResolver ForSettings(ISettingsBox box, IPlatformRegistry? registry = null)
        => new(new SettingsBoxView(box), registry);

    public PlatformPolicyResolver(IPlatformSettingsBox box, IPlatformRegistry? registry,
        Func<PlatformOptions, ImmutableArray<ActionId>> qqActionBaseline)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _registry = registry;
        _qqActionBaseline = qqActionBaseline ?? throw new ArgumentNullException(nameof(qqActionBaseline));
    }

    private sealed class SettingsBoxView(ISettingsBox box) : IPlatformSettingsBox
    {
        public PlatformOptions Current => box.Current;
    }

    public EffectivePlatformPolicy ResolveForChannel(string? channel)
    {
        var declared = Channels.Declared(channel);
        var platform = declared switch
        {
            Channels.Private => PlatformId.QqPrivate,
            Channels.Official => PlatformId.QqOfficial,
            Channels.Local => PlatformId.Local,
            Channels.Feishu => PlatformId.Feishu,
            _ => string.IsNullOrWhiteSpace(channel) ? PlatformId.QqPrivate : PlatformId.Normalize(channel),
        };
        var account = platform is PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Local
            ? AccountScope.Legacy
            : AccountScope.Default;
        var context = new PlatformContext(platform, account);
        var settings = _box.Current;
        if (!settings.RuntimePlatformPolicies.HasValue) return ResolveLegacy(settings, context, channelOnly: true);
        if (settings.RuntimePlatformPolicies.Value.IsDefault) return Rejected(context, "source_context_required");

        // A channel has no authenticated source instance. Keep only the uniform canonical compatibility view.
        var drivers = settings.DeclaredPlatformDrivers;
        var candidates = _registry?.GetSnapshots().Where(snapshot => SameAccount(snapshot, context)).Take(2).ToArray();
        var canonical = platform == PlatformId.Feishu ? account == AccountScope.Default
            : platform is PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Local && account == AccountScope.Legacy;
        var instanceOverride = settings.RuntimePlatformPolicies.Value.Any(entry => entry is not null && entry.Target is PolicyTarget.Instance instance
            && !string.IsNullOrWhiteSpace(instance.Key.PlatformId) && PlatformId.Normalize(instance.Key.PlatformId) == platform
            && instance.Key.AccountScope == account);
        if (!canonical || drivers.IsDefault || drivers.Count(driver => !string.IsNullOrWhiteSpace(driver.PlatformId)
                && PlatformId.Normalize(driver.PlatformId) == platform && driver.AccountScope == account) != 1
            || candidates is not { Length: 1 } || instanceOverride)
            return Rejected(context, "source_context_required");
        if (!drivers.Any(driver => !string.IsNullOrWhiteSpace(driver.PlatformId)
                && PlatformId.Normalize(driver.PlatformId) == platform && driver.AccountScope == account
                && string.Equals(driver.InstanceId, candidates[0].InstanceId, StringComparison.Ordinal)))
            return Rejected(context, "source_context_required");
        return ResolveTyped(settings, context, candidates[0], new PolicyTarget.AccountDefault(platform, account));
    }

    public EffectivePlatformPolicy Resolve(PlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = _box.Current;
        if (!ValidContext(context)) return Rejected(context, "invalid_platform_context");
        var actual = context with { PlatformId = PlatformId.Normalize(context.PlatformId) };
        if (!settings.RuntimePlatformPolicies.HasValue) return ResolveLegacy(settings, actual, channelOnly: false);
        var snapshots = _registry?.GetSnapshots().Where(snapshot => SameAccount(snapshot, actual)
            && string.Equals(snapshot.InstanceId, actual.InstanceId, StringComparison.Ordinal)).Take(2).ToArray();
        if (snapshots is { Length: > 1 }) return Rejected(actual, "ambiguous_platform_instance");
        return ResolveTyped(settings, actual, snapshots?.SingleOrDefault(), new PolicyTarget.Instance(
            new DriverKey(actual.PlatformId, actual.AccountScope, actual.InstanceId)));
    }

    private EffectivePlatformPolicy ResolveTyped(PlatformOptions settings, PlatformContext context,
        PlatformStatusSnapshot? snapshot, PolicyTarget target)
    {
        var rules = PlatformSwitchSettings.ResolvePolicyRules(settings, target, _qqActionBaseline(settings));
        var enabled = rules.Enabled == ToggleRule.Enabled;
        var chatEnabled = enabled && rules.ChatEnabled == ToggleRule.Enabled;
        var reasons = ImmutableArray.CreateBuilder<string>();
        if (snapshot is null) reasons.Add("platform_unregistered");
        else if (!snapshot.Connected) reasons.Add("platform_disconnected");
        if (!enabled) reasons.Add("platform_disabled");
        if (!chatEnabled) reasons.Add("chat_disabled");
        // QQ management actions are implemented only by the private OneBot action host, not by text capability.
        var actions = context.PlatformId == PlatformId.QqPrivate && enabled && chatEnabled && snapshot?.Connected == true
            ? rules.AllowedActions.Actions.Select(action => action.Value).ToImmutableHashSet(StringComparer.Ordinal)
            : ImmutableHashSet<string>.Empty;
        return new EffectivePlatformPolicy(context, snapshot is not null, snapshot?.Connected ?? false, enabled, chatEnabled,
            snapshot?.Capabilities ?? DefaultCapabilities(context.PlatformId), actions,
            rules.Features.ToImmutableDictionary(feature => feature.Feature.ToString().ToLowerInvariant(),
                feature => feature.Enabled, StringComparer.OrdinalIgnoreCase), reasons.ToImmutable())
        {
            ActionAllowlistConfigured = true,
        };
    }

    private EffectivePlatformPolicy ResolveLegacy(PlatformOptions settings, PlatformContext context, bool channelOnly)
    {
        var platform = PlatformId.Normalize(context.PlatformId);
        var account = string.IsNullOrWhiteSpace(context.AccountScope) ? AccountScope.Default : context.AccountScope;
        var actualContext = context with { PlatformId = platform, AccountScope = account };
        var snapshots = _registry?.GetSnapshots().Where(s =>
            string.Equals(PlatformId.Normalize(s.PlatformId), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.AccountScope, account, channelOnly ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            && (channelOnly || string.Equals(s.InstanceId, context.InstanceId, StringComparison.Ordinal))).Take(2).ToArray();
        if (snapshots is { Length: > 1 }) return Rejected(actualContext, "ambiguous_platform_instance");
        if ((settings.PlatformPolicies ?? new()).Count(policy => policy is not null
                && !string.IsNullOrWhiteSpace(policy.PlatformId) && PlatformId.Normalize(policy.PlatformId) == platform
                && string.Equals(string.IsNullOrWhiteSpace(policy.AccountScope) ? AccountScope.Default : policy.AccountScope,
                    account, StringComparison.OrdinalIgnoreCase)) > 1)
            return Rejected(actualContext, "ambiguous_platform_policy");
        var snapshot = snapshots?.SingleOrDefault();
        var overrideSettings = FindSettings(settings, platform, account);
        var switches = PlatformSwitchSettings.Read(settings, platform, account);
        var enabled = switches.Enabled;
        var chatEnabled = enabled && switches.ChatEnabled;
        var capabilities = snapshot?.Capabilities ?? DefaultCapabilities(platform);
        var reasons = new List<string>();

        if (snapshot is null) reasons.Add("platform_unregistered");
        else if (!snapshot.Connected) reasons.Add("platform_disconnected");
        if (!enabled) reasons.Add("platform_disabled");
        if (!chatEnabled) reasons.Add("chat_disabled");

        var allowedActions = (overrideSettings?.AllowedActions ?? new List<string>())
            .Where(a => !string.IsNullOrWhiteSpace(a))
            .Select(a => a.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var featureOverrides = overrideSettings?.FeatureOverrides
            ?? new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        return new EffectivePlatformPolicy(
            actualContext,
            Registered: snapshot is not null,
            Connected: snapshot?.Connected ?? false,
            Enabled: enabled,
            ChatEnabled: chatEnabled,
            Capabilities: capabilities,
            AllowedActions: allowedActions,
            FeatureOverrides: featureOverrides,
            Reasons: reasons)
        {
            ActionAllowlistConfigured = overrideSettings is not null
                && (!overrideSettings.InheritActionAllowlist || overrideSettings.AllowedActions.Count > 0),
        };
    }

    public bool IsChatEnabled(string? channel)
    {
        var policy = ResolveForChannel(channel);
        return policy.Enabled && policy.ChatEnabled;
    }

    public bool FeatureEnabled(string? channel, string feature, bool globalEnabled)
        => ResolveForChannel(channel).Feature(feature, globalEnabled).Enabled;

    private static bool ValidContext(PlatformContext context) => !string.IsNullOrWhiteSpace(context.PlatformId)
        && !string.IsNullOrWhiteSpace(context.AccountScope) && context.InstanceId is not null
        && (context.InstanceId.Length == 0 || !string.IsNullOrWhiteSpace(context.InstanceId));

    private static bool SameAccount(PlatformStatusSnapshot snapshot, PlatformContext context)
        => !string.IsNullOrWhiteSpace(snapshot.PlatformId) && PlatformId.Normalize(snapshot.PlatformId) == context.PlatformId
            && string.Equals(snapshot.AccountScope, context.AccountScope, StringComparison.Ordinal);

    private static EffectivePlatformPolicy Rejected(PlatformContext context, string reason)
        => new(context, false, false, false, false,
            new PlatformCapabilities(SupportsText: false, SupportsDirect: false, SupportsGroup: false),
            ImmutableHashSet<string>.Empty, ImmutableDictionary<string, bool>.Empty, ImmutableArray.Create(reason))
        { ActionAllowlistConfigured = true };

    private static PlatformPolicySettings? FindSettings(PlatformOptions settings, string platform, string account)
        => (settings.PlatformPolicies ?? new List<PlatformPolicySettings>()).FirstOrDefault(p =>
            p is not null
            && string.Equals(PlatformId.Normalize(p.PlatformId), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(string.IsNullOrWhiteSpace(p.AccountScope) ? AccountScope.Default : p.AccountScope,
                account, StringComparison.OrdinalIgnoreCase));

    private static PlatformCapabilities DefaultCapabilities(string platform)
        => platform switch
        {
            PlatformId.QqPrivate => PlatformCapabilities.QqOneBot,
            PlatformId.QqOfficial => PlatformCapabilities.QqOfficial,
            PlatformId.Local => PlatformCapabilities.Local,
            PlatformId.Feishu => PlatformCapabilities.FeishuTextOnly,
            _ => new PlatformCapabilities(SupportsText: false, SupportsDirect: false, SupportsGroup: false),
        };
}
