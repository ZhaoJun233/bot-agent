using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Voice;

/// <summary>Outcome of synthesizing one voice clip across the package boundary.</summary>
public sealed record VoiceSynthesisOutcome(byte[]? Data, string? Error);

/// <summary>One sample clip submitted for voice-clone concatenation.</summary>
public sealed record VoiceSampleInput(byte[] Data, string Name);

/// <summary>Outcome of concatenating multiple WAV or MP3 samples inside <c>preset.feature.voice</c>.</summary>
public sealed record VoiceConcatOutcome(byte[]? Data, string? Error, string Note);

/// <summary>
/// Narrow, credential-free host ports provided to <c>preset.feature.voice</c> for TTS transport and logging.
/// </summary>
public interface IVoiceHostPorts
{
    bool IsClientReady { get; }

    string? ClientVoiceName { get; }

    string? BuildSpeakUrl(string text, string? emotionOverride, int? speedOverride, int? pitchOverride);

    Task<VoiceSynthesisOutcome> SynthesizeAsync(string text, string? voiceOverride, int? speedOverride, CancellationToken ct);

    void Log(string message);
}

/// <summary>
/// Generation-bound voice send cadence and burst breaker policy owned by <c>preset.feature.voice</c>.
/// </summary>
public interface IVoicePolicyContribution : IPluginContribution
{
    bool Allow(string sourceKey, int breakerSeconds, DateTimeOffset now, out string reason);

    void MarkSent(string sourceKey, DateTimeOffset now);

    int SuggestIntervalSeconds(int eagerness);

    void ClearConversation(string sourceKey);
}

/// <summary>
/// Full voice runtime coordinator owned by <c>preset.feature.voice</c>: burst breaker, cadence,
/// multi-part speak URL orchestration, panel synthesis self-test, and clone sample concatenation.
/// </summary>
public interface IVoiceRuntimeContribution : IVoicePolicyContribution
{
    string[] BuildSpeakUrls(string[] parts, string? emotion, double? speed, int? pitch);

    Task<VoiceSynthesisOutcome> TestAsync(string text, string? voiceOverride, int? speedOverride, CancellationToken ct);

    VoiceConcatOutcome ConcatSamples(VoiceSampleInput[] samples);
}
