using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;
using BotAgent.Services;

namespace BotAgent.Services.Platforms;

/// <summary>
/// Combines adapter capabilities, account-scoped policy overrides, and legacy settings.
/// </summary>
public sealed class PlatformPolicyResolver
{
    private readonly SettingsBox _box;
    private readonly IPlatformRegistry? _registry;

    public PlatformPolicyResolver(SettingsBox box, IPlatformRegistry? registry = null)
    {
        _box = box ?? throw new ArgumentNullException(nameof(box));
        _registry = registry;
    }

    public EffectivePlatformPolicy ResolveForChannel(string? channel)
    {
        var normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        var platform = normalized switch
        {
            "private" => PlatformId.QqPrivate,
            "official" => PlatformId.QqOfficial,
            "local" => PlatformId.Local,
            "feishu" => PlatformId.Feishu,
            _ => PlatformId.Normalize(normalized),
        };
        var account = platform is PlatformId.QqPrivate or PlatformId.QqOfficial or PlatformId.Local
            ? AccountScope.Legacy
            : AccountScope.Default;
        return Resolve(new PlatformContext(platform, account));
    }

    public EffectivePlatformPolicy Resolve(PlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var settings = _box.Current;
        var platform = PlatformId.Normalize(context.PlatformId);
        var account = string.IsNullOrWhiteSpace(context.AccountScope) ? AccountScope.Default : context.AccountScope;
        var actualContext = context with { PlatformId = platform, AccountScope = account };
        var snapshot = _registry?.GetSnapshots().FirstOrDefault(s =>
            string.Equals(PlatformId.Normalize(s.PlatformId), platform, StringComparison.OrdinalIgnoreCase)
            && string.Equals(s.AccountScope, account, StringComparison.OrdinalIgnoreCase));
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

    private static PlatformPolicySettings? FindSettings(AppSettings settings, string platform, string account)
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
