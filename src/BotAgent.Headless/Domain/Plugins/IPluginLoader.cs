namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件加载器契约：负责间接发现、实例化并装载插件。
/// </summary>
public interface IPluginLoader
{
    /// <summary>加载并初始化所有可用插件。</summary>
    Task<IReadOnlyList<IBotPlugin>> LoadPluginsAsync(IPluginContext context, CancellationToken ct);
}