using System.Collections.Concurrent;
using BotAgent.Domain.Plugins;
using BotAgent.Services;

namespace BotAgent.Services.Plugins;

/// <summary>
/// 插件管理宿主：负责插件生命周期统筹与查询。
/// </summary>
public sealed class PluginManager : IPluginRegistry
{
    private readonly ConcurrentDictionary<string, (IBotPlugin Plugin, bool Enabled)> _plugins = new();
    private readonly Action<string> _log;
    private readonly Func<string, IPluginToggleBinding?>? _bindingLookup;
    private readonly Func<Action<AppSettings>, bool>? _applySettings;

    public PluginManager(
        Action<string> log,
        Func<string, IPluginToggleBinding?>? bindingLookup = null,
        Func<Action<AppSettings>, bool>? applySettings = null)
    {
        _log = log;
        _bindingLookup = bindingLookup;
        _applySettings = applySettings;
    }

    public void Register(IBotPlugin plugin, bool defaultEnabled = true)
    {
        _plugins[plugin.Id] = (plugin, defaultEnabled);
        _log($"[Plugin] 注册插件: {plugin.Name} ({plugin.Id}) v{plugin.Version} [{(defaultEnabled ? "已启用" : "已禁用")}]");
    }

    public IReadOnlyList<PluginInfo> GetAll()
    {
        var list = new List<PluginInfo>(_plugins.Count);
        foreach (var (_, (p, enabled)) in _plugins)
        {
            // 有绑定的插件以**真实配置值**为准：内存字典只是镜像，配置才是真相
            var binding = _bindingLookup?.Invoke(p.Id);
            var effective = enabled;
            var persisted = false;
            if (binding != null)
            {
                persisted = true;
                try
                {
                    effective = binding.Read(CurrentSettingsSnapshot());
                }
                catch
                {
                    effective = enabled;
                }
            }

            list.Add(new PluginInfo(p.Id, p.Name, p.Version, p.Description, p.Category, p.IsPreset, effective)
            {
                Persisted = persisted,
                // 管理类插件（Web 控制台）没有配置字段绑定，但同样不可禁用：
                // 否则重启后面板自身消失，只剩命令行可用。
                AllowDisable = binding?.AllowDisable ?? (p.Category != PluginCategory.Management)
            });
        }
        return list;
    }

    /// <summary>当前配置快照（由 CompositionRoot 注入，供绑定读值用）。</summary>
    private Func<AppSettings>? _settingsAccessor;

    public void AttachSettingsAccessor(Func<AppSettings> accessor) => _settingsAccessor = accessor;

    private AppSettings CurrentSettingsSnapshot() => _settingsAccessor?.Invoke() ?? new AppSettings();

    public IBotPlugin? Find(string pluginId)
        => _plugins.TryGetValue(pluginId, out var pair) ? pair.Plugin : null;

    public bool IsEnabled(string pluginId)
    {
        if (!_plugins.TryGetValue(pluginId, out var pair))
        {
            return false;
        }

        var binding = _bindingLookup?.Invoke(pluginId);
        if (binding == null)
        {
            return pair.Enabled;
        }

        try
        {
            return binding.Read(CurrentSettingsSnapshot());
        }
        catch
        {
            return pair.Enabled;
        }
    }

    public PluginToggleResult SetEnabled(string pluginId, bool enabled)
    {
        if (!_plugins.TryGetValue(pluginId, out var pair))
        {
            return PluginToggleResult.Fail($"未知插件: {pluginId}");
        }

        // 管理类插件硬拒绝：禁用会让面板在重启后彻底消失。
        // 这条判定必须早于「无绑定」分支 —— WebUI 自身没有配置字段绑定，
        // 若走 MemoryOnly 分支就会"接受"禁用，重启后面板直接没了。
        if (!enabled && pair.Plugin.Category == PluginCategory.Management)
        {
            _log($"[Plugin] 拒绝禁用管理类必需插件: {pair.Plugin.Name} ({pluginId})");
            return PluginToggleResult.Reject("该插件为管理类必需组件，不支持禁用");
        }

        var binding = _bindingLookup?.Invoke(pluginId);
        if (binding == null)
        {
            // 无绑定：只能改内存镜像，重启即失效 —— 如实告知，不假装已生效
            _plugins[pluginId] = (pair.Plugin, enabled);
            _log($"[Plugin] 插件状态更新（仅内存，未落配置）: {pair.Plugin.Name} -> {(enabled ? "已启用" : "已禁用")}");
            return PluginToggleResult.MemoryOnly;
        }

        // 管理类插件硬拒绝：禁用会让面板在重启后彻底消失
        if (!enabled && !binding.AllowDisable)
        {
            _log($"[Plugin] 拒绝禁用必需插件: {pair.Plugin.Name} ({pluginId})");
            return PluginToggleResult.Reject("该插件为必需组件，不支持禁用");
        }

        if (_applySettings == null)
        {
            _plugins[pluginId] = (pair.Plugin, enabled);
            _log($"[Plugin] 插件状态更新（无热重载通道，仅内存）: {pair.Plugin.Name} -> {(enabled ? "已启用" : "已禁用")}");
            return PluginToggleResult.MemoryOnly;
        }

        var applied = _applySettings(target => binding.Write(target, enabled));
        if (!applied)
        {
            return PluginToggleResult.Fail("配置写入失败");
        }

        _plugins[pluginId] = (pair.Plugin, enabled);
        _log($"[Plugin] 插件状态已落配置并热重载: {pair.Plugin.Name} -> {(enabled ? "已启用" : "已禁用")}");
        return PluginToggleResult.PersistedOk;
    }

    void IPluginRegistry.SetEnabled(string pluginId, bool enabled) => SetEnabled(pluginId, enabled);

    public PluginToggleResult TrySetEnabled(string pluginId, bool enabled) => SetEnabled(pluginId, enabled);
    public async Task LoadWithAsync(IPluginLoader loader, IPluginContext context, CancellationToken ct)
    {
        var loaded = await loader.LoadPluginsAsync(context, ct);
        foreach (var plugin in loaded)
        {
            Register(plugin, defaultEnabled: true);
        }
    }

    public async Task StartAllAsync(IPluginContext context, CancellationToken ct)
    {
        foreach (var (id, (plugin, enabled)) in _plugins)
        {
            if (!enabled) continue;
            try
            {
                await plugin.InitializeAsync(context, ct);
                _log($"[Plugin] 插件初始化完成: {plugin.Name}");
            }
            catch (Exception ex)
            {
                _log($"[Plugin] 插件 {plugin.Name} 初始化失败: {ex.Message}");
            }
        }
    }

    public async Task StopAllAsync(CancellationToken ct)
    {
        foreach (var (id, (plugin, enabled)) in _plugins)
        {
            if (!enabled) continue;
            try
            {
                await plugin.ShutdownAsync(ct);
            }
            catch (Exception ex)
            {
                _log($"[Plugin] 插件 {plugin.Name} 卸载异常: {ex.Message}");
            }
        }
    }
}
