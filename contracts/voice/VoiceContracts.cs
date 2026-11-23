using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Voice;

/// <summary>Outcome of synthesizing one voice clip across the package boundary.</summary>
public sealed record VoiceSynthesisOutcome(byte[]? Data, string? Error);

/// <summary>Outcome of transcribing an inbound voice clip via Cloud STT.</summary>
public sealed record VoiceTranscriptionOutcome(bool Ok, string? Transcript, string ReasonCode = "ok")
{
    public override string ToString() => $"VoiceTranscriptionOutcome(ok={Ok}, chars={Transcript?.Length ?? 0}, reason={ReasonCode})";
}

/// <summary>One sample clip submitted for voice-clone concatenation.</summary>
public sealed record VoiceSampleInput(byte[] Data, string Name);

/// <summary>Outcome of concatenating multiple WAV or MP3 samples inside <c>preset.feature.voice</c>.</summary>
public sealed record VoiceConcatOutcome(byte[]? Data, string? Error, string Note);

/// <summary>
/// Narrow, credential-free host ports provided to <c>preset.feature.voice</c> for TTS/STT transport and logging.
/// </summary>
public interface IVoiceHostPorts
{
    bool IsClientReady { get; }

    string? ClientVoiceName { get; }

    bool IsSttEnabled => false;

    string? BuildSpeakUrl(string text, string? emotionOverride, int? speedOverride, int? pitchOverride);

    Task<VoiceSynthesisOutcome> SynthesizeAsync(string text, string? voiceOverride, int? speedOverride, CancellationToken ct);

    Task<VoiceTranscriptionOutcome> TranscribeAsync(byte[] audioData, string fileName, CancellationToken ct)
        => Task.FromResult(new VoiceTranscriptionOutcome(false, null, "stt_unavailable"));

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
/// multi-part speak URL orchestration, panel synthesis self-test, clone sample concatenation, and Cloud STT.
/// </summary>
public interface IVoiceRuntimeContribution : IVoicePolicyContribution
{
    bool IsSttEnabled => false;

    string[] BuildSpeakUrls(string[] parts, string? emotion, double? speed, int? pitch);

    Task<VoiceSynthesisOutcome> TestAsync(string text, string? voiceOverride, int? speedOverride, CancellationToken ct);

    Task<VoiceTranscriptionOutcome> TranscribeAsync(byte[] audioData, string fileName, CancellationToken ct)
        => Task.FromResult(new VoiceTranscriptionOutcome(false, null, "stt_unavailable"));

    VoiceConcatOutcome ConcatSamples(VoiceSampleInput[] samples);
}
