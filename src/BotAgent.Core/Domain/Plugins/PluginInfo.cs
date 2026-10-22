namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件只读信息（供面板/CLI 探测展示）。
/// </summary>
public sealed record PluginInfo(
    string Id,
    string Name,
    string Version,
    string Description,
    PluginCategory Category,
    bool IsPreset,
    bool IsEnabled);
