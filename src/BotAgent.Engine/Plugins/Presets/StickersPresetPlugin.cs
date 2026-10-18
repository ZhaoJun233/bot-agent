using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：表情包感知、审核与协同插件。</summary>
public sealed class StickersPresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.stickers";
    public string Name => "表情包协同与安全库";
    public string Version => "1.0.0";
    public string Description => "自动学习收录群聊表情包，提取标签并根据对话语境协同发送，内置安全魔数校验。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
