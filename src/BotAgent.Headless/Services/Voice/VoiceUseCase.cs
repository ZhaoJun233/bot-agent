using BotAgent.Services.Agent;
using BotAgent.Services.Plugins;

namespace BotAgent.Services.Voice;

/// <summary>
/// 语音（TTS + STT）宿主薄门面：连发熔断、建议节奏、分段 URL 拼装、面板试听、样本拼接与云端语音转写均归
/// <c>preset.feature.voice</c> 独立包经 <see cref="IVoiceRuntimeContribution" /> 拥有；缺包或禁用时 fail-closed。
/// </summary>
public sealed class VoiceUseCase : IVoiceUseCase
{
    public const string ProviderId = "preset.feature.voice";
    public const int BreakerSeconds = 8;

    private readonly VoiceService? _voice;
    private readonly SettingsBox _box;
    private readonly Action<string> _log;
    private readonly PluginManager? _plugins;
    private readonly CloudSttClient? _stt;
    private IVoiceRuntimeContribution? _direct;

    public VoiceUseCase(
        VoiceService? voice,
        SettingsBox box,
        Action<string> log,
        PluginManager? plugins = null,
        IVoiceRuntimeContribution? direct = null,
        CloudSttClient? stt = null)
    {
        _voice = voice;
        _box = box;
        _log = log;
        _plugins = plugins;
        _direct = direct;
        _stt = stt;
    }

    private AppSettings _settings => _box.Current;

    /// <summary>仅供无插件宿主的独立单测注入包内真实运行时实现。</summary>
    public void BindDirect(IVoiceRuntimeContribution runtime) => _direct = runtime;

    /// <summary>供宿主装配层注册到插件服务容器，向 <c>preset.feature.voice</c> 提供窄宿主端口。</summary>
    public IVoiceHostPorts CreateHostPorts() => new HostPorts(this);

    private static bool IsUnavailable(InvalidOperationException ex)
        => ex.Message is "plugin_contribution_unavailable" or "plugin_dependency_unavailable" or "plugin_contribution_missing";

    private IVoiceRuntimeContribution? CurrentRuntime
        => _direct ?? (_plugins is not null && _plugins.TryGetContribution<IVoiceRuntimeContribution>(ProviderId, out var runtime)
            ? runtime : null);

    private IVoicePolicyContribution? CurrentPolicy
        => _direct ?? (_plugins is not null && _plugins.TryGetContribution<IVoicePolicyContribution>(ProviderId, out var policy)
            ? policy : null);

    /// <summary>语音插件是否可用（无插件宿主的独立单测放行；真实宿主按包就绪态 fail-closed）。</summary>
    public bool IsPluginAvailable => _direct is not null || _plugins is null || _plugins.IsReady(ProviderId);

    /// <summary>语音客户端（面板健康探测 / 音色克隆管理传输层；TTS 地址没配好时为 null）。</summary>
    public VoiceService? Client => _voice;

    public bool IsSttEnabled
    {
        get
        {
            if (!IsPluginAvailable) return false;
            try
            {
                return CurrentRuntime?.IsSttEnabled ?? (_stt?.IsEnabled == true);
            }
            catch (InvalidOperationException ex) when (IsUnavailable(ex))
            {
                return false;
            }
        }
    }

    public int MaxSttDurationSeconds => _stt?.MaxDurationSeconds ?? 60;

    /// <summary>面板上「语音积极性」对应给模型的建议节奏（秒）。</summary>
    public int SuggestIntervalSeconds
    {
        get
        {
            try
            {
                return CurrentPolicy?.SuggestIntervalSeconds(_settings.VoiceEagerness)
                    ?? VoiceCadence.IntervalSeconds(_settings.VoiceEagerness);
            }
            catch (InvalidOperationException ex) when (IsUnavailable(ex))
            {
                return VoiceCadence.IntervalSeconds(_settings.VoiceEagerness);
            }
        }
    }

    public string? ClientVoiceName => _voice?.VoiceName;

    public bool Allow(string sourceKey, out string reason)
    {
        var policy = CurrentPolicy;
        if (policy is null)
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }

        try { return policy.Allow(sourceKey, BreakerSeconds, Clock.Now, out reason); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            reason = "plugin_contribution_unavailable";
            return false;
        }
    }

    public void MarkSent(string sourceKey)
    {
        try { CurrentPolicy?.MarkSent(sourceKey, Clock.Now); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { }
    }

    public List<string> BuildSpeakUrls(IReadOnlyList<string> parts, string? emotion, double? speed, int? pitch)
    {
        var runtime = CurrentRuntime;
        if (runtime is null || parts.Count == 0)
        {
            return new List<string>();
        }

        var array = parts as string[] ?? parts.ToArray();
        try { return new List<string>(runtime.BuildSpeakUrls(array, emotion, speed, pitch)); }
        catch (InvalidOperationException ex) when (IsUnavailable(ex)) { return new List<string>(); }
    }

    public async Task<(string? Transcript, string ReasonCode)> TranscribeAsync(
        byte[] audioData,
        string fileName,
        CancellationToken ct = default)
    {
        if (!IsSttEnabled)
            return (null, "stt_disabled");

        var runtime = CurrentRuntime;
        if (runtime is null)
        {
            if (_stt is null) return (null, "plugin_contribution_unavailable");
            var direct = await _stt.TranscribeAsync(audioData, fileName, ct).ConfigureAwait(false);
            return (direct.Transcript, direct.ReasonCode);
        }

        try
        {
            var outcome = await runtime.TranscribeAsync(audioData, fileName, ct).ConfigureAwait(false);
            return (outcome.Transcript, outcome.ReasonCode);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            return (null, "plugin_contribution_unavailable");
        }
    }

    public async Task<(string? Transcript, string ReasonCode)> TranscribeFromUrlAsync(
        string audioUrl,
        string fileName,
        CancellationToken ct = default)
    {
        if (!IsSttEnabled || _stt is null)
            return (null, "stt_disabled");

        var (data, resolvedName, reason) = await _stt.DownloadAudioAsync(audioUrl, fileName, ct).ConfigureAwait(false);
        if (data is null)
            return (null, reason);
        return await TranscribeAsync(data, resolvedName, ct).ConfigureAwait(false);
    }

    public async Task<(byte[]? Data, string? Error)> TestAsync(
        string text,
        string? voiceOverride,
        int? speedOverride,
        CancellationToken ct)
    {
        var runtime = CurrentRuntime;
        if (runtime is null)
        {
            return (null, "语音插件未启用或不可用");
        }

        try
        {
            var outcome = await runtime.TestAsync(text, voiceOverride, speedOverride, ct).ConfigureAwait(false);
            return (outcome.Data, outcome.Error);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            return (null, "语音插件未启用或不可用");
        }
    }

    public (byte[]? Data, string? Error, string Note) ConcatSamples(IReadOnlyList<(byte[] Data, string Name)> samples)
    {
        var runtime = CurrentRuntime;
        if (runtime is null)
        {
            return (null, "语音插件未启用或不可用", string.Empty);
        }

        var input = samples.Select(s => new VoiceSampleInput(s.Data, s.Name)).ToArray();
        try
        {
            var outcome = runtime.ConcatSamples(input);
            return (outcome.Data, outcome.Error, outcome.Note);
        }
        catch (InvalidOperationException ex) when (IsUnavailable(ex))
        {
            return (null, "语音插件未启用或不可用", string.Empty);
        }
    }

    private sealed class HostPorts(VoiceUseCase owner) : IVoiceHostPorts
    {
        public bool IsClientReady => owner._voice is not null;

        public string? ClientVoiceName => owner._voice?.VoiceName;

        public bool IsSttEnabled => owner._stt?.IsEnabled == true;

        public string? BuildSpeakUrl(string text, string? emotionOverride, int? speedOverride, int? pitchOverride)
            => owner._voice?.BuildSpeakUrl(text, emotionOverride: emotionOverride, speedOverride: speedOverride, pitchOverride: pitchOverride);

        public async Task<VoiceSynthesisOutcome> SynthesizeAsync(string text, string? voiceOverride, int? speedOverride, CancellationToken ct)
        {
            if (owner._voice is null)
            {
                return new(null, "语音服务还没初始化");
            }

            var (data, error) = await owner._voice.SynthesizeAsync(text, voiceOverride, speedOverride, ct).ConfigureAwait(false);
            return new(data, error);
        }

        public Task<VoiceTranscriptionOutcome> TranscribeAsync(byte[] audioData, string fileName, CancellationToken ct)
            => owner._stt is null
                ? Task.FromResult(new VoiceTranscriptionOutcome(false, null, "stt_unavailable"))
                : owner._stt.TranscribeAsync(audioData, fileName, ct);

        public void Log(string message) => owner._log(message);
    }
}
