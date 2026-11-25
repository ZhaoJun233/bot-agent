using System.Collections.Immutable;
using System.Text;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Configuration;

/// <summary>会话档案纯领域校验与规范化（无 IO、零副作用）。</summary>
public static class ConversationProfileValidator
{
    public const int MaxProfiles = 256;
    public const int MaxJsonBytes = 256 * 1024;
    public const int MaxKeyLength = 128;
    public const int MaxDisplayLabelLength = 120;
    public const int MaxPersonaOverrideLength = 1200;
    public const int MaxPersonaAppendLength = 600;
    public const int MaxCombinedPersonaLength = 1200;

    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, true);

    public static string KeyOrder(ConversationProfileKey key)
        => string.Concat(key.Driver.PlatformId, "\u001F", key.Driver.AccountScope, "\u001F",
            key.Driver.InstanceId, "\u001F", key.ConversationKey);

    public static ConversationProfileKey FreezeKey(ConversationProfileKey key)
    {
        if (key is null) throw new ArgumentException("invalid_conversation_profile_key");
        var platform = CheckToken(key.Driver.PlatformId, MaxKeyLength);
        platform = PlatformId.Normalize(platform);
        var accountScope = CheckToken(key.Driver.AccountScope, MaxKeyLength);
        var instanceId = key.Driver.InstanceId ?? throw new ArgumentException("invalid_conversation_profile_key");
        if (instanceId.Length > MaxKeyLength || instanceId.Trim() != instanceId || HasControlChar(instanceId))
            throw new ArgumentException("invalid_conversation_profile_key");
        StrictUtf8.GetByteCount(instanceId);

        var conversationKey = CheckToken(key.ConversationKey, MaxKeyLength);
        if (!MatchesPlatformPrefix(platform, conversationKey))
            throw new ArgumentException("conversation_key_platform_mismatch");
        return new ConversationProfileKey(new DriverKey(platform, accountScope, instanceId), conversationKey);
    }

    public static bool MatchesPlatformPrefix(string normalizedPlatformId, string conversationKey)
    {
        if (string.IsNullOrEmpty(conversationKey)) return false;
        foreach (var ch in conversationKey)
            if (char.IsControl(ch) || char.IsWhiteSpace(ch)) return false;

        return normalizedPlatformId switch
        {
            PlatformId.QqPrivate => HasDigitSuffix(conversationKey, "group:") || HasDigitSuffix(conversationKey, "private:"),
            PlatformId.QqOfficial => HasNonEmptySuffix(conversationKey, "official:group:") || HasNonEmptySuffix(conversationKey, "official:private:"),
            PlatformId.Feishu => HasNonEmptySuffix(conversationKey, "feishu:group:") || HasNonEmptySuffix(conversationKey, "feishu:private:"),
            PlatformId.Local => HasNonEmptySuffix(conversationKey, "local:"),
            _ => false,
        };
    }

    public static ConversationProfile FreezeProfile(ConversationProfile profile)
    {
        if (profile is null) throw new ArgumentException("invalid_conversation_profile");
        var key = FreezeKey(profile.Key);
        var label = NormalizeText(profile.DisplayLabel, MaxDisplayLabelLength, allowEmpty: true, disallowControls: true) ?? string.Empty;
        var style = FreezeStyle(profile.Style);
        var pacing = FreezePacing(profile.Pacing);
        var capabilities = FreezeCapabilities(profile.Capabilities);
        return new ConversationProfile(key, label, style, pacing, capabilities);
    }

    public static ConversationProfileEdit FreezeEdit(ConversationProfileEdit edit)
    {
        if (edit is null || !Enum.IsDefined(edit.Operation))
            throw new ArgumentException("invalid_conversation_profile_edit");
        var key = FreezeKey(edit.Key);
        if (edit.Operation == ConversationProfileEditOperation.Delete)
            return new ConversationProfileEdit(key, ConversationProfileEditOperation.Delete, null);

        if (edit.Profile is null) throw new ArgumentException("invalid_conversation_profile");
        var frozen = FreezeProfile(edit.Profile);
        if (frozen.Key != key) throw new ArgumentException("conversation_profile_key_mismatch");
        return new ConversationProfileEdit(key, ConversationProfileEditOperation.Upsert, frozen);
    }

    public static ImmutableArray<FieldError> ValidateCollection(
        ImmutableArray<ConversationProfile> profiles,
        ImmutableArray<DriverKey> declaredDrivers)
    {
        var errors = ImmutableArray.CreateBuilder<FieldError>();
        if (profiles.IsDefault || profiles.Length > MaxProfiles)
        {
            errors.Add(new(new(ConfigurationFieldId.ConversationProfiles, null), "conversation_profile_limit_exceeded"));
            return errors.ToImmutable();
        }
        var declared = declaredDrivers.IsDefaultOrEmpty ? null : declaredDrivers.ToHashSet();
        var seen = new HashSet<ConversationProfileKey>();
        foreach (var profile in profiles)
        {
            if (profile is null)
            {
                errors.Add(new(new(ConfigurationFieldId.ConversationProfiles, null), "invalid_conversation_profile"));
                continue;
            }
            var address = new ConfigurationObjectAddress.ConversationProfile(profile.Key);
            if (!seen.Add(profile.Key))
                errors.Add(new(new(ConfigurationFieldId.ConversationProfiles, address), "duplicate_conversation_profile"));
            if (declared is not null && !declared.Contains(profile.Key.Driver))
                errors.Add(new(new(ConfigurationFieldId.ConversationProfiles, address), "undeclared_platform_driver"));
        }
        return errors.ToImmutable();
    }

    private static ConversationStyleOverride? FreezeStyle(ConversationStyleOverride? style)
    {
        if (style is null) return null;
        var personaOverride = NormalizeText(style.PersonaOverride, MaxPersonaOverrideLength, allowEmpty: false);
        var personaAppend = NormalizeText(style.PersonaAppend, MaxPersonaAppendLength, allowEmpty: false);
        if (personaOverride is not null && personaAppend is not null
            && personaOverride.Length + personaAppend.Length > MaxCombinedPersonaLength)
            throw new ArgumentException("persona_length_exceeded");
        if (style.VoiceMaxChars is { } maxChars && maxChars is < 10 or > 300)
            throw new ArgumentException("out_of_range");
        if (style.VoiceEagerness is { } eagerness && eagerness is < 0 or > 100)
            throw new ArgumentException("out_of_range");
        if (personaOverride is null && personaAppend is null && style.FilterActionNarration is null
            && style.VoiceMaxChars is null && style.VoiceEagerness is null)
            return null;
        return new ConversationStyleOverride(personaOverride, personaAppend, style.FilterActionNarration,
            style.VoiceMaxChars, style.VoiceEagerness);
    }

    private static ConversationPacingOverride? FreezePacing(ConversationPacingOverride? pacing)
    {
        if (pacing is null) return null;
        if (!Enum.IsDefined(pacing.ProactiveEnabled)) throw new ArgumentException("invalid_toggle_rule");
        if (pacing.GroupInterjectionProbability is { } prob && prob is < 0 or > 100)
            throw new ArgumentException("out_of_range");
        if (pacing.GroupInterjectionCooldownSeconds is { } cd && cd is < 0 or > 3600)
            throw new ArgumentException("out_of_range");
        if (pacing.GroupContinuationWaitSeconds is { } gWait && gWait is < 0 or > 30)
            throw new ArgumentException("out_of_range");
        if (pacing.PrivateContinuationWaitSeconds is { } pWait && (!double.IsFinite(pWait) || pWait < 0 || pWait > 30))
            throw new ArgumentException("out_of_range");
        if (pacing.SuitabilityThreshold is { } suit && suit is < 0 or > 100)
            throw new ArgumentException("out_of_range");
        if (pacing.MaxContextMessages is { } ctx && ctx is < 4 or > 60)
            throw new ArgumentException("out_of_range");
        if (pacing.ProactiveQuietSeconds is { } quiet && quiet is < 30 or > 3600)
            throw new ArgumentException("out_of_range");
        if (pacing.GroupInterjectionProbability is null && pacing.GroupInterjectionCooldownSeconds is null
            && pacing.GroupContinuationWaitSeconds is null && pacing.PrivateContinuationWaitSeconds is null
            && pacing.SuitabilityThreshold is null && pacing.MaxContextMessages is null
            && pacing.ProactiveEnabled == ToggleRule.Inherit && pacing.ProactiveQuietSeconds is null)
            return null;
        return pacing with { };
    }

    private static ConversationCapabilityOverride? FreezeCapabilities(ConversationCapabilityOverride? capabilities)
    {
        if (capabilities is null) return null;
        if (!Enum.IsDefined(capabilities.ToolTier)) throw new ArgumentException("invalid_tool_tier");
        var raw = capabilities.Features.IsDefault ? ImmutableArray<FeatureOverride>.Empty : capabilities.Features;
        if (raw.Length > 32) throw new ArgumentException("invalid_features");
        var features = raw.Select(f => f is null || !Enum.IsDefined(f.Feature)
            ? throw new ArgumentException("invalid_feature") : f with { })
            .OrderBy(f => f.Feature).ToImmutableArray();
        if (features.Select(f => f.Feature).Distinct().Count() != features.Length)
            throw new ArgumentException("duplicate_feature");
        if (features.IsEmpty && capabilities.ToolTier == ConversationToolTier.Inherit) return null;
        return new ConversationCapabilityOverride(features, capabilities.ToolTier);
    }

    private static string CheckToken(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > maxLength || text.Trim() != text || HasControlChar(text))
            throw new ArgumentException("invalid_conversation_profile_key");
        StrictUtf8.GetByteCount(text);
        return text;
    }

    private static string? NormalizeText(string? value, int maxLength, bool allowEmpty, bool disallowControls = false)
    {
        if (value is null) return null;
        StrictUtf8.GetByteCount(value);
        var trimmed = value.Trim();
        if (trimmed.Length == 0) return allowEmpty ? string.Empty : null;
        if (disallowControls && HasControlChar(trimmed))
            throw new ArgumentException("invalid_control_character");
        if (trimmed.Length > maxLength) throw new ArgumentException("text_too_long");
        return trimmed;
    }

    private static bool HasControlChar(string text)
    {
        foreach (var ch in text)
            if (char.IsControl(ch)) return true;
        return false;
    }

    private static bool HasNonEmptySuffix(string value, string prefix)
        => value.StartsWith(prefix, StringComparison.Ordinal) && value.Length > prefix.Length;

    private static bool HasDigitSuffix(string value, string prefix)
    {
        if (!HasNonEmptySuffix(value, prefix)) return false;
        for (var i = prefix.Length; i < value.Length; i++)
            if (value[i] < '0' || value[i] > '9') return false;
        return true;
    }
}
