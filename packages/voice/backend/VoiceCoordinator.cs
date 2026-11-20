using BotAgent.Services.Voice;

namespace BotAgent.Packages.Voice;

/// <summary>
/// Package-owned voice runtime coordinator: owns multi-part speak URL orchestration,
/// panel synthesis self-test, clone sample concatenation, burst breaker, and cadence policy.
/// </summary>
public sealed class VoiceCoordinator : IVoiceRuntimeContribution
{
    private readonly VoicePolicyEngine _policy;
    private readonly IVoiceHostPorts? _ports;

    public VoiceCoordinator(VoicePolicyEngine? policy = null, IVoiceHostPorts? ports = null)
    {
        _policy = policy ?? new VoicePolicyEngine();
        _ports = ports;
    }

    public bool Allow(string sourceKey, int breakerSeconds, DateTimeOffset now, out string reason)
        => _policy.Allow(sourceKey, breakerSeconds, now, out reason);

    public void MarkSent(string sourceKey, DateTimeOffset now)
        => _policy.MarkSent(sourceKey, now);

    public int SuggestIntervalSeconds(int eagerness)
        => _policy.SuggestIntervalSeconds(eagerness);

    public void ClearConversation(string sourceKey)
        => _policy.ClearConversation(sourceKey);

    public string[] BuildSpeakUrls(string[] parts, string? emotion, double? speed, int? pitch)
    {
        if (_ports is null || !_ports.IsClientReady || parts is null || parts.Length == 0)
        {
            return [];
        }

        var urls = new List<string>(parts.Length);
        var speedPercent = speed is double s ? (int)Math.Round(s * 100) : (int?)null;
        var first = _ports.BuildSpeakUrl(parts[0], emotionOverride: emotion, speedOverride: speedPercent, pitchOverride: pitch);
        if (first is null)
        {
            return [];
        }

        urls.Add(first);
        for (var i = 1; i < parts.Length; i++)
        {
            var extra = _ports.BuildSpeakUrl(parts[i], emotionOverride: emotion, speedOverride: speedPercent, pitchOverride: pitch);
            if (extra is null)
            {
                break;
            }

            urls.Add(extra);
        }

        return urls.ToArray();
    }

    public async Task<VoiceSynthesisOutcome> TestAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        if (_ports is null || !_ports.IsClientReady)
        {
            return new(null, "语音服务还没初始化");
        }

        var outcome = await _ports.SynthesizeAsync(text, voiceOverride, speedOverride, ct).ConfigureAwait(false);
        _ports.Log(outcome.Data is null
            ? $"[Voice] 面板试听失败：{outcome.Error}"
            : $"[Voice] 面板试听成功（{text.Length} 字 → {outcome.Data.Length / 1024} KB wav）");
        return outcome;
    }

    public VoiceConcatOutcome ConcatSamples(VoiceSampleInput[] samples)
        => VoiceSampleConcatenator.Concat(samples);
}
