namespace BotAgent.Domain.Platforms;

/// <summary>
/// 面板可持久化的平台策略覆盖。凭据不属于这里；空覆盖表示沿用兼容字段或默认值。
/// </summary>
public sealed class PlatformPolicySettings
{
    public string PlatformId { get; set; } = string.Empty;
    public string AccountScope { get; set; } = BotAgent.Domain.Platforms.AccountScope.Default;
    public bool? Enabled { get; set; }
    public bool? ChatEnabled { get; set; }
    public bool InheritActionAllowlist { get; set; }
    public string GroupWhitelist { get; set; } = string.Empty;
    public string PrivateWhitelist { get; set; } = string.Empty;
    public Dictionary<string, bool> FeatureOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> AllowedActions { get; set; } = new();

    public PlatformPolicySettings Clone()
        => new()
        {
            PlatformId = PlatformId ?? string.Empty,
            AccountScope = AccountScope ?? BotAgent.Domain.Platforms.AccountScope.Default,
            Enabled = Enabled,
            ChatEnabled = ChatEnabled,
            InheritActionAllowlist = InheritActionAllowlist,
            GroupWhitelist = GroupWhitelist ?? string.Empty,
            PrivateWhitelist = PrivateWhitelist ?? string.Empty,
            FeatureOverrides = new Dictionary<string, bool>(FeatureOverrides ?? new(), StringComparer.OrdinalIgnoreCase),
            AllowedActions = [.. (AllowedActions ?? new())],
        };
}

public sealed record PlatformFeatureDecision(
    string Name,
    bool Supported,
    bool Enabled,
    string ReasonCode);

/// <summary>
/// 一个入站/出站回合使用的不可变平台有效策略快照。
/// Supported 是适配器硬能力，Enabled 是管理员策略与全局开关的交集。
/// </summary>
public sealed record EffectivePlatformPolicy(
    PlatformContext Context,
    bool Registered,
    bool Connected,
    bool Enabled,
    bool ChatEnabled,
    PlatformCapabilities Capabilities,
    IReadOnlySet<string> AllowedActions,
    IReadOnlyDictionary<string, bool> FeatureOverrides,
    IReadOnlyList<string> Reasons)
{
    public bool ActionAllowlistConfigured { get; init; }


    public PlatformFeatureDecision Feature(string name, bool globallyEnabled = true)
    {
        var normalized = (name ?? string.Empty).Trim().ToLowerInvariant();
        var supported = normalized switch
        {
            "text" => Capabilities.SupportsText,
            "image" => Capabilities.SupportsImage,
            "voice" => Capabilities.SupportsVoice,
            "quote" => Capabilities.SupportsQuote,
            "recall" => Capabilities.SupportsRecall,
            "thread" => Capabilities.SupportsThread,
            "stickers" => Capabilities.SupportsStickers,
            "music" => Capabilities.SupportsMusic,
            "poke" => Capabilities.SupportsPoke,
            "linkpreview" or "websearch" => Capabilities.SupportsText,
            _ => false,
        };

        if (!supported)
        {
            return new PlatformFeatureDecision(normalized, false, false, "platform_capability_unsupported");
        }

        if (!Registered || !Connected)
        {
            return new PlatformFeatureDecision(normalized, true, false,
                Registered ? "platform_disconnected" : "platform_unregistered");
        }

        if (!Enabled || !ChatEnabled)
        {
            return new PlatformFeatureDecision(normalized, true, false, "platform_chat_disabled");
        }

        var allowedByPolicy = FeatureOverrides is null || !FeatureOverrides.TryGetValue(normalized, out var featureOverride) || featureOverride;
        var featureEnabled = globallyEnabled && allowedByPolicy;
        return featureEnabled
            ? new PlatformFeatureDecision(normalized, true, true, "enabled")
            : new PlatformFeatureDecision(normalized, true, false, "feature_disabled");
    }

    public bool CanUseAction(string action)
        => !string.IsNullOrWhiteSpace(action) && AllowedActions.Contains(action.Trim());
}


