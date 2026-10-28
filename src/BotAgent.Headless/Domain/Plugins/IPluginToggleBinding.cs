using BotAgent.Services;

namespace BotAgent.Domain.Plugins;

/// <summary>
/// 插件启停与真实配置字段的绑定契约。
///
/// 为什么需要它：<c>PluginManager</c> 里的 Enabled 只是一个内存标记，
/// 业务判定读的是 <see cref="AppSettings"/> 上的独立开关（EnableMusic / EnableVoice …）。
/// 两套真相并存时，"面板点禁用"只会改内存标记，功能照常运行 —— 面板就成了摆设。
/// 绑定之后，插件的启停直接落到真实配置字段上，并经热重载扇出生效。
/// </summary>
public interface IPluginToggleBinding
{
    /// <summary>绑定的插件 ID（与 <see cref="IBotPlugin.Id"/> 一致）。</summary>
    string PluginId { get; }

    /// <summary>是否允许被禁用。管理类插件（如 Web 控制台）覆写为 false —— 禁用会让面板在重启后彻底消失。</summary>
    bool AllowDisable => true;

    /// <summary>从配置读当前开关值。</summary>
    bool Read(AppSettings settings);

    /// <summary>把开关值写进配置副本（由 SettingsBox 在副本上执行后整体发布）。</summary>
    void Write(AppSettings target, bool enabled);
}