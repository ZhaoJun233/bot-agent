using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>预设插件：联网深度研究与网页链接摘要插件。</summary>
public sealed class WebSearchPresetPlugin : IBotPlugin
{
    public string Id => "preset.feature.websearch";
    public string Name => "联网研究与链接摘要";
    public string Version => "1.0.0";
    public string Description => "支持受控联网搜索抓取、群聊外部网页链接实时抓取与内容智能提要。";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => true;

    public Task InitializeAsync(CancellationToken ct) => Task.CompletedTask;
    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
