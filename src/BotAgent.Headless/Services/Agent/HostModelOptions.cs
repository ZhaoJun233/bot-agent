using BotAgent.Services;

namespace BotAgent.Services.Agent;

/// <summary>Projects one published settings snapshot per access; never owns configuration or secrets.</summary>
internal static class HostModelOptions
{
    internal static Func<BotAgent.Model.ModelOptions> CreateAccessor(SettingsBox box)
    {
        ArgumentNullException.ThrowIfNull(box);
        return () => Project(box.Current);
    }

    private static BotAgent.Model.ModelOptions Project(AppSettings settings) => new()
    {
        BaseUrl = settings.ModelBaseUrl,
        Model = settings.Model,
        ApiKey = settings.ApiKey,
        BaseUrlOverride = settings.ModelBaseUrlOverride,
        ModelOverride = settings.ModelOverride,
        ApiKeyOverride = settings.ApiKeyOverride,
        FastReply = settings.FastReply,
        FastModel = settings.FastModel,
        MaxTokens = settings.MaxTokens,
        ThinkingBudget = settings.ThinkingBudget,
        ThinkingCustomBudget = settings.ThinkingCustomBudget,
        FastThinkingBudget = settings.FastThinkingBudget,
        FastThinkingCustomBudget = settings.FastThinkingCustomBudget,
        DefaultTemperature = settings.DefaultTemperature,
        DefaultTopP = settings.DefaultTopP,
        EnableAtmosphereDamping = settings.EnableAtmosphereDamping,
        VoiceMaxChars = settings.VoiceMaxChars,
        VoiceEagerness = settings.VoiceEagerness,
        FilterActionNarration = settings.FilterActionNarration,
        MusicUnderstandModel = settings.MusicUnderstandModel,
        MusicSendAudioToModel = settings.MusicSendAudioToModel,
        MusicAudioToModelMaxKb = settings.MusicAudioToModelMaxKb
    };
}
