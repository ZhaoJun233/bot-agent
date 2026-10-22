using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：TTS 语音合成与下发插件。</summary>
public sealed class VoicePresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.voice";
    public string Name => "TTS 拟真语音合成";
    public string Version => "1.0.0";
    public string Description => "支持对接外部 TTS 服务，模型自主决策语音表达，并具有会话级防连发频控。";
    public PluginCategory Category => PluginCategory.Media;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
