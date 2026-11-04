using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：双向戳一戳互动与自主回戳插件。</summary>
public sealed class PokePresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.poke";
    public string Name => "双向戳一戳交互";
    public string Version => "1.0.0";
    public string Description => "识别群聊与私聊双向戳一戳动作，具备冷却门限判定与自主拟人化回戳逻辑。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
