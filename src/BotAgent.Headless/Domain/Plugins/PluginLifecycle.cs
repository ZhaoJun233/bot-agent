namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件生命周期状态机状态。
/// </summary>
public enum PluginLifecycleState
{
    Discovered,
    Loading,
    Loaded,
    Active,
    Quarantined,
    Disabled,
    Unloading,
    Unloaded
}