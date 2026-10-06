namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件注册表只读端口：供宿主与面板查看已加载插件及状态。
/// </summary>
public interface IPluginRegistry
{
    /// <summary>获取所有已登记插件的概览信息。</summary>
    IReadOnlyList<PluginInfo> GetAll();

    /// <summary>根据 ID 获取插件实例。</summary>
    IBotPlugin? Find(string pluginId);

    /// <summary>插件是否已启用。</summary>
    bool IsEnabled(string pluginId);
}
