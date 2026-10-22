namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件初始化与运行期上下文：提供只读服务发现与只读环境参数。
/// </summary>
public sealed class PluginContext
{
    public IServiceProvider Services { get; }
    public IReadOnlyDictionary<string, string> EnvironmentInfo { get; }

    public PluginContext(IServiceProvider services, IReadOnlyDictionary<string, string>? env = null)
    {
        Services = services ?? throw new ArgumentNullException(nameof(services));
        EnvironmentInfo = env ?? new Dictionary<string, string>();
    }
}
