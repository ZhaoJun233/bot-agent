using BotAgent.Engine.Runtime;
using System.Collections.Concurrent;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>
/// 插件管理宿主：负责插件生命周期统筹与查询。
/// </summary>
public sealed class PluginManager : IPluginRegistry
{
    private readonly ConcurrentDictionary<string, (IBotPlugin Plugin, bool Enabled)> _plugins = new();
    private readonly Action<string> _log;

    public PluginManager(Action<string> log)
    {
        _log = log;
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
            list.Add(new PluginInfo(p.Id, p.Name, p.Version, p.Description, p.Category, p.IsPreset, enabled));
        }
        return list;
    }

    public IBotPlugin? Find(string pluginId)
        => _plugins.TryGetValue(pluginId, out var pair) ? pair.Plugin : null;

    public bool IsEnabled(string pluginId)
        => _plugins.TryGetValue(pluginId, out var pair) && pair.Enabled;

    public void SetEnabled(string pluginId, bool enabled)
    {
        if (_plugins.TryGetValue(pluginId, out var pair))
        {
            _plugins[pluginId] = (pair.Plugin, enabled);
            _log($"[Plugin] 插件状态更新: {pair.Plugin.Name} -> {(enabled ? "已启用" : "已禁用")}");
        }
    }
    public async Task StartAllAsync(CancellationToken ct)
    {
        foreach (var (id, (plugin, enabled)) in _plugins)
        {
            if (!enabled) continue;
            try
            {
                await plugin.InitializeAsync(ct);
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
