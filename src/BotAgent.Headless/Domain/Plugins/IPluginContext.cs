namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件宿主上下文接口：插件通过此接口间接与 BotAgent 核心通信与获取受限服务。
/// 核心不直接持有插件，插件亦不穿透核心私有实现。
/// </summary>
public interface IPluginContext
{
    /// <summary>获取全局受控只读配置。</summary>
    T? GetConfig<T>(string key);

    /// <summary>获取宿主暴露的共享服务或能力端口。</summary>
    T? GetService<T>() where T : class;

    /// <summary>记录插件业务日志。</summary>
    void Log(string message);

    /// <summary>安全登记插件暴露的能力探针（带版本协商）。</summary>
    bool TryRegisterCapability(CapabilityDescriptor descriptor, out string? error);

    /// <summary>获取当前系统已登记的全部能力探针。</summary>
    IReadOnlyList<CapabilityDescriptor> GetCapabilities();

    /// <summary>
    /// 登记插件启停与真实配置字段的绑定。
    /// 没有绑定 = 该插件的启停只改内存标记（面板会标注"仅本次会话生效"）。
    /// </summary>
    bool TryBindToggle(IPluginToggleBinding binding, out string? error);

    /// <summary>按插件 ID 取绑定（没有 = null）。</summary>
    IPluginToggleBinding? GetToggleBinding(string pluginId);

    /// <summary>
    /// 登记本插件自述的一组配置字段（面板按分组渲染）。
    /// 插件未启用时该组不会出现在面板上 —— 面板只挂真实存在的功能。
    /// </summary>
    bool TryContributeSettings(SettingsSection section, out string? error);

    /// <summary>取当前已登记的全部配置分组（供面板 schema 端点用）。</summary>
    IReadOnlyList<SettingsSection> GetSettingsSections();

    /// <summary>发布/广播插件事件。</summary>
    void PublishEvent(string eventName, object? payload);
}