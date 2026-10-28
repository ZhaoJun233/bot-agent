using BotAgent.Domain.Plugins;
using BotAgent.Services;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>
/// 预设插件的启停绑定基类：把插件启停映射到 <see cref="AppSettings"/> 上的一个布尔开关。
/// 没有它，面板点"禁用"只改内存标记，业务判定读的却是配置字段 —— 面板就成了摆设。
/// </summary>
public class PresetToggleBinding : IPluginToggleBinding
{
    private readonly Func<AppSettings, bool> _read;
    private readonly Action<AppSettings, bool> _write;

    public PresetToggleBinding(
        string pluginId,
        Func<AppSettings, bool> read,
        Action<AppSettings, bool> write)
    {
        PluginId = pluginId;
        _read = read;
        _write = write;
    }

    public string PluginId { get; }

    public virtual bool AllowDisable => true;

    public bool Read(AppSettings settings) => _read(settings);

    public void Write(AppSettings target, bool enabled) => _write(target, enabled);
}