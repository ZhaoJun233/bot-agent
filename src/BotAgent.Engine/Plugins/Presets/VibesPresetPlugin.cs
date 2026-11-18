using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：群聊氛围感知与情绪底色。</summary>
public sealed class VibesPresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.vibes";
    public string Name => "群聊氛围感知";
    public string Version => "1.0.0";
    public string Description => "保留会话氛围与情绪底色，为后续聊天和主动关心提供上下文。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
