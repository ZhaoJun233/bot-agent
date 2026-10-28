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
    bool IsEnabled)
{
    /// <summary>启停是否已落到真实配置字段（true = 重启后仍有效）。</summary>
    public bool Persisted { get; init; }

    /// <summary>是否允许被禁用。管理类插件（Web 控制台）为 false。</summary>
    public bool AllowDisable { get; init; } = true;
}
