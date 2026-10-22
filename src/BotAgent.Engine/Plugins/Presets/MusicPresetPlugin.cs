using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：群聊音乐解析与分享插件。</summary>
public sealed class MusicPresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.music";
    public string Name => "群聊音乐卡片与波形解析";
    public string Version => "1.0.0";
    public string Description => "支持群聊音乐卡片解析、低码率音频波形特征提取与网易云音乐卡片分享。";
    public PluginCategory Category => PluginCategory.Media;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
