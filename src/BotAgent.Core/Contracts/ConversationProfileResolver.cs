using System.Collections.Immutable;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Model;
using BotAgent.Domain.Platforms;
using BotAgent.Services;

namespace BotAgent.Services.Platforms;

/// <summary>合并后的 4 项回复节奏有效值（Core 层纯值对象，由 Engine 转为 ReplyPacingOptions）。</summary>
public sealed record EffectiveConversationPacing(
    int GroupInterjectionProbability,
    int GroupInterjectionCooldownSeconds,
    int GroupContinuationWaitSeconds,
    double PrivateContinuationWaitSeconds)
{
    public override string ToString() => "EffectiveConversationPacing";
}

/// <summary>
/// 一轮会话合并后的不可变有效配置（全局默认 → 平台实例策略 → 会话档案三层继承产物）。
/// </summary>
public sealed record EffectiveConversationSettings(
    EffectivePlatformPolicy EffectivePolicy,
    EffectiveConversationPacing Pacing,
    ConversationPromptOverrides PromptOverrides,
    int SuitabilityThreshold,
    int MaxContextMessages,
    bool FilterActionNarration,
    int VoiceMaxChars,
    int VoiceEagerness,
    bool ProactiveEnabled,
    int ProactiveQuietSeconds,
    ConversationToolTier EffectiveToolTier,
    ImmutableDictionary<string, bool> EffectiveFeatureDefaults)
{
    public PlatformFeatureDecision Feature(string name)
    {
        var normalized = (name ?? string.Empty).Trim().ToLowerInvariant();
        var defaultEnabled = EffectiveFeatureDefaults.TryGetValue(normalized, out var enabled) ? enabled : true;
        return EffectivePolicy.Feature(normalized, defaultEnabled);
    }

    public bool FeatureEnabled(string name) => Feature(name).Enabled;

    public bool AllowsNaturalAgent => EffectiveToolTier is ConversationToolTier.Inherit or ConversationToolTier.ManagedTools;
    public bool AllowsToolRequest => EffectiveToolTier is ConversationToolTier.Inherit or ConversationToolTier.ManagedTools;
    public bool AllowsAsk => EffectiveToolTier != ConversationToolTier.ChatOnly;
    public bool AllowsReadOnlyTools => EffectiveToolTier != ConversationToolTier.ChatOnly;

    public override string ToString() => "EffectiveConversationSettings";
}

/// <summary>
/// 三层继承纯函数解析器（零 IO / 零副作用）：
/// 平台硬能力、平台显式禁用、白名单、AI 总开关、插件门禁为不可逾越硬顶棚；
/// 在硬顶棚放行前提下，会话档案可收紧或局部放开全局默认开关。
/// </summary>
public static class ConversationProfileResolver
{
    public static ConversationProfile? FindProfile(
        AppSettings settings, PlatformContext context, string conversationKey)
    {
        if (settings is null || context is null || string.IsNullOrWhiteSpace(conversationKey)) return null;
        var profiles = settings.RuntimeConversationProfiles;
        if (profiles.IsDefaultOrEmpty) return null;
        var platform = PlatformId.Normalize(context.PlatformId);
        var account = context.AccountScope ?? string.Empty;
        var instance = context.InstanceId ?? string.Empty;
        if (instance.Length == 0 && !settings.DeclaredPlatformDrivers.IsDefaultOrEmpty)
        {
            DriverKey? unique = null;
            var count = 0;
            foreach (var declared in settings.DeclaredPlatformDrivers)
            {
                if (string.Equals(PlatformId.Normalize(declared.PlatformId), platform, StringComparison.Ordinal)
                    && string.Equals(declared.AccountScope, account, StringComparison.Ordinal))
                {
                    unique = declared;
                    if (++count > 1) break;
                }
            }
            if (count == 1 && unique is { } matched) instance = matched.InstanceId;
        }
        for (var i = 0; i < profiles.Length; i++)
        {
            var item = profiles[i];
            if (item is null) continue;
            var driver = item.Key.Driver;
            if (string.Equals(driver.PlatformId, platform, StringComparison.Ordinal)
                && string.Equals(driver.AccountScope, account, StringComparison.Ordinal)
                && string.Equals(driver.InstanceId, instance, StringComparison.Ordinal)
                && string.Equals(item.Key.ConversationKey, conversationKey, StringComparison.Ordinal))
                return item;
        }
        if (BotAgent.Domain.Messaging.ConversationIdCodec.TryParse(conversationKey, out var targetConv))
        {
            var targetIsStructured = conversationKey.StartsWith("v=1;", StringComparison.OrdinalIgnoreCase);
            for (var i = 0; i < profiles.Length; i++)
            {
                var item = profiles[i];
                if (item is null) continue;
                var driver = item.Key.Driver;
                if (!string.Equals(driver.PlatformId, platform, StringComparison.Ordinal)
                    || !string.Equals(driver.AccountScope, account, StringComparison.Ordinal)
                    || !string.Equals(driver.InstanceId, instance, StringComparison.Ordinal))
                    continue;
                var itemIsStructured = item.Key.ConversationKey.StartsWith("v=1;", StringComparison.OrdinalIgnoreCase);
                if (targetIsStructured && itemIsStructured) continue;
                if (BotAgent.Domain.Messaging.ConversationIdCodec.TryParse(item.Key.ConversationKey, out var itemConv)
                    && PlatformId.Normalize(itemConv.PlatformId) == PlatformId.Normalize(targetConv.PlatformId)
                    && itemConv.Kind == targetConv.Kind
                    && string.Equals(itemConv.NativeTargetId, targetConv.NativeTargetId, StringComparison.Ordinal)
                    && string.Equals(itemConv.ThreadId ?? string.Empty, targetConv.ThreadId ?? string.Empty, StringComparison.Ordinal))
                    return item;
            }
        }
        return null;
    }

    public static EffectiveConversationSettings Resolve(
        AppSettings global,
        EffectivePlatformPolicy platformPolicy,
        string conversationKey)
        => Resolve(global, platformPolicy, FindProfile(global, platformPolicy.Context, conversationKey), IsGroupConversationKey(conversationKey));

    public static EffectiveConversationSettings Resolve(
        AppSettings global,
        EffectivePlatformPolicy platformPolicy,
        ConversationProfile? profile)
        => Resolve(global, platformPolicy, profile, isGroupConversation: false);

    private static EffectiveConversationSettings Resolve(
        AppSettings global,
        EffectivePlatformPolicy platformPolicy,
        ConversationProfile? profile,
        bool isGroupConversation)
    {
        ArgumentNullException.ThrowIfNull(global);
        ArgumentNullException.ThrowIfNull(platformPolicy);

        var style = profile?.Style;
        var pacingOverride = profile?.Pacing;
        var capabilityOverride = profile?.Capabilities;

        var suitability = Math.Clamp(pacingOverride?.SuitabilityThreshold ?? global.SuitabilityThreshold, 0, 100);
        var maxContext = Math.Clamp(pacingOverride?.MaxContextMessages ?? global.MaxContextMessages, 1, 200);
        var filterNarration = style?.FilterActionNarration ?? global.FilterActionNarration;
        var voiceMaxChars = Math.Clamp(style?.VoiceMaxChars ?? global.VoiceMaxChars, 10, 300);
        var voiceEagerness = Math.Clamp(style?.VoiceEagerness ?? global.VoiceEagerness, 0, 100);
        var proactiveQuiet = Math.Clamp(pacingOverride?.ProactiveQuietSeconds ?? global.ProactiveQuietSeconds, 1, 3600);
        var proactiveEnabled = global.AiModeEnabled && platformPolicy.Enabled && platformPolicy.ChatEnabled
            && (pacingOverride?.ProactiveEnabled switch
            {
                ToggleRule.Enabled => true,
                ToggleRule.Disabled => false,
                _ => global.EnableProactive,
            });

        var pacing = new EffectiveConversationPacing(
            Math.Clamp(pacingOverride?.GroupInterjectionProbability ?? global.GroupInterjectionProbability, 0, 100),
            Math.Clamp(pacingOverride?.GroupInterjectionCooldownSeconds ?? global.GroupInterjectionCooldownSeconds, 0, 3600),
            Math.Clamp(pacingOverride?.GroupContinuationWaitSeconds ?? global.GroupContinuationWaitSeconds, 0, 30),
            Math.Clamp(pacingOverride?.PrivateContinuationWaitSeconds ?? global.PrivateContinuationWaitSeconds, 0, 30));

        var defaults = BuildFeatureDefaults(global, platformPolicy, capabilityOverride, isGroupConversation);
        var mergedPolicy = MergeFeatureOverrides(platformPolicy, defaults);
        var toolTier = capabilityOverride?.ToolTier ?? ConversationToolTier.Inherit;

        var platformId = PlatformId.Normalize(platformPolicy.Context.PlatformId);
        var platformNeutral = platformId is not (PlatformId.QqPrivate or PlatformId.QqOfficial);
        var platformDisplayName = PlatformDisplayName(platformId);

        var promptOverrides = new ConversationPromptOverrides(
            PlatformDisplayName: platformDisplayName,
            PlatformNeutral: platformNeutral,
            PersonaOverride: style?.PersonaOverride,
            PersonaAppend: style?.PersonaAppend,
            SuitabilityThreshold: suitability,
            MaxContextMessages: maxContext,
            VoiceMaxChars: voiceMaxChars,
            VoiceEagerness: voiceEagerness,
            FilterActionNarration: filterNarration);

        return new EffectiveConversationSettings(
            mergedPolicy,
            pacing,
            promptOverrides,
            suitability,
            maxContext,
            filterNarration,
            voiceMaxChars,
            voiceEagerness,
            proactiveEnabled,
            proactiveQuiet,
            toolTier,
            defaults);
    }

    private static ImmutableDictionary<string, bool> BuildFeatureDefaults(
        AppSettings global, EffectivePlatformPolicy platformPolicy, ConversationCapabilityOverride? capabilityOverride, bool isGroupConversation)
    {
        var map = ImmutableDictionary.CreateBuilder<string, bool>(StringComparer.OrdinalIgnoreCase);
        map["text"] = true;
        map["image"] = true;
        map["quote"] = true;
        map["recall"] = true;
        map["thread"] = true;
        map["voice"] = global.EnableVoice;
        map["stickers"] = global.EnableStickers;
        map["music"] = global.EnableMusic;
        map["poke"] = global.EnablePoke;
        map["linkpreview"] = global.EnableLinkPreview;
        map["websearch"] = global.EnableWebSearch;
        map["documents"] = true;
        map["digest"] = true;
        map["knowledge"] = true;
        map["scheduler"] = true;
        map["personal_todo"] = !isGroupConversation;
        map["episodes"] = platformPolicy.FeatureOverrides is not null
            && platformPolicy.FeatureOverrides.TryGetValue("episodes", out var pEp) && pEp;
        map["jargon"] = platformPolicy.FeatureOverrides is not null
            && platformPolicy.FeatureOverrides.TryGetValue("jargon", out var pJar) && pJar;

        if (capabilityOverride is not null && !capabilityOverride.Features.IsDefaultOrEmpty)
        {
            foreach (var item in capabilityOverride.Features)
            {
                if (item is null) continue;
                var key = EffectivePlatformPolicy.FeatureKey(item.Feature);
                if (isGroupConversation && key == "personal_todo") continue;
                map[key] = item.Enabled;
            }
        }
        return map.ToImmutable();
    }

    public static bool IsGroupConversationKey(string? conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey)) return false;
        var trimmed = conversationKey.Trim();
        if (BotAgent.Domain.Messaging.ConversationIdCodec.TryParse(trimmed, out var conv))
            return conv.Kind == BotAgent.Domain.Conversation.ConversationKind.GroupChat;
        return trimmed.StartsWith("group:", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains(":group:", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 将会话级显式/默认关闭态叠加到平台 FeatureOverrides 中，同时严禁覆盖平台层原有显式 false：
    /// 这样即使下游只调用 EffectivePolicy.Feature(name, globallyEnabled: true) 也不会旁路会话禁令。
    /// </summary>
    private static EffectivePlatformPolicy MergeFeatureOverrides(
        EffectivePlatformPolicy platformPolicy,
        ImmutableDictionary<string, bool> effectiveDefaults)
    {
        var merged = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        if (platformPolicy.FeatureOverrides is not null)
        {
            foreach (var pair in platformPolicy.FeatureOverrides)
                merged[pair.Key] = pair.Value;
        }
        foreach (var pair in effectiveDefaults)
        {
            if (merged.TryGetValue(pair.Key, out var platformAllowed) && !platformAllowed)
                continue; // 平台显式禁用不可被会话档案复活
            if (!pair.Value)
                merged[pair.Key] = false;
        }
        return platformPolicy with { FeatureOverrides = merged.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase) };
    }

    private static string PlatformDisplayName(string normalizedPlatformId) => normalizedPlatformId switch
    {
        PlatformId.QqPrivate => "QQ",
        PlatformId.QqOfficial => "QQ",
        PlatformId.Feishu => "飞书",
        PlatformId.Local => "本地通道",
        _ => normalizedPlatformId,
    };
}
