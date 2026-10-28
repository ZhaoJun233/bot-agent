namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件类别：通道、业务特性、多媒体处理、管理面扩展。
/// </summary>
public enum PluginCategory
{
    Channel,
    Feature,
    Media,
    Management
}

/// <summary>
/// BotAgent 插件核心契约：所有可插拔能力（官方预设或社区扩展）均实现此接口。
/// </summary>
public interface IBotPlugin
{
    /// <summary>唯一标识符，例如 "preset.feature.music"。</summary>
    string Id { get; }

    /// <summary>插件易读名称。</summary>
    string Name { get; }

    /// <summary>版本号。</summary>
    string Version { get; }

    /// <summary>插件功能描述。</summary>
    string Description { get; }

    /// <summary>插件所属类别。</summary>
    PluginCategory Category { get; }

    /// <summary>是否为官方预设插件。</summary>
    bool IsPreset => true;

    /// <summary>初始化插件资源并注入间接宿主上下文。</summary>
    Task InitializeAsync(IPluginContext context, CancellationToken ct);

    /// <summary>停止与清理插件资源。</summary>
    Task ShutdownAsync(CancellationToken ct);
}
