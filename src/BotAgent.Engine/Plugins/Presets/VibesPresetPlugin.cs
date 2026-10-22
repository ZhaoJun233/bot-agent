using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：群聊氛围感知与疲劳阻尼插件。</summary>
public sealed class VibesPresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.vibes";
    public string Name => "群聊氛围感知与发言欲望阻尼";
    public string Version => "1.0.0";
    public string Description => "动态监测群消息密度与发言热度，连续发言后自适应非线性衰减回复欲望并进入强制降温冷却。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
