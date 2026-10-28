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

    /// <summary>动态切换插件启用/禁用状态（具体结果由实现返回；接口侧保留 void 兼容）。</summary>
    void SetEnabled(string pluginId, bool enabled);

    /// <summary>
    /// 带结果的启停：区分「已落配置」「仅内存」「被拒绝」。
    /// WebUI 用它决定按钮回执文案。
    /// </summary>
    PluginToggleResult TrySetEnabled(string pluginId, bool enabled) => PluginToggleResult.Fail("not supported");
}
