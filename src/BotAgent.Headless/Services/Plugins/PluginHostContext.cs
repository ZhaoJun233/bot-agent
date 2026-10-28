using System.Collections.Concurrent;
using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>
/// 插件宿主上下文实现：提供受控且隔离的核心能力接口。
/// </summary>
public sealed class PluginHostContext : IPluginContext
{
    private readonly Action<string> _log;
    private readonly ConcurrentDictionary<string, object?> _configs = new();
    private readonly ConcurrentDictionary<Type, object> _services = new();
    private readonly ConcurrentDictionary<string, List<Action<object?>>> _eventHandlers = new();
    private readonly ConcurrentDictionary<string, CapabilityDescriptor> _capabilities = new();
    private readonly ConcurrentDictionary<string, IPluginToggleBinding> _toggleBindings = new();
    private readonly ConcurrentDictionary<string, SettingsSection> _settingsSections = new();

    public PluginHostContext(Action<string> log)
    {
        _log = log;
    }

    public bool TryBindToggle(IPluginToggleBinding binding, out string? error)
    {
        if (string.IsNullOrWhiteSpace(binding.PluginId))
        {
            error = "插件 ID 为空";
            return false;
        }

        _toggleBindings[binding.PluginId] = binding;
        error = null;
        return true;
    }

    public IPluginToggleBinding? GetToggleBinding(string pluginId)
        => _toggleBindings.TryGetValue(pluginId, out var binding) ? binding : null;

    public bool TryContributeSettings(SettingsSection section, out string? error)
    {
        if (string.IsNullOrWhiteSpace(section.SectionId))
        {
            error = "分组 ID 为空";
            return false;
        }

        _settingsSections[section.SectionId] = section;
        error = null;
        return true;
    }

    public IReadOnlyList<SettingsSection> GetSettingsSections() => _settingsSections.Values.ToArray();

    public IReadOnlyCollection<IPluginToggleBinding> GetAllToggleBindings() => _toggleBindings.Values.ToArray();

    public bool TryRegisterCapability(CapabilityDescriptor descriptor, out string? error)
    {
        try
        {
            _capabilities[descriptor.CapabilityId] = descriptor;
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public IReadOnlyList<CapabilityDescriptor> GetCapabilities() => _capabilities.Values.ToArray();

    public void RegisterService<T>(T service) where T : class
    {
        _services[typeof(T)] = service;
    }

    public T? GetService<T>() where T : class
    {
        return _services.TryGetValue(typeof(T), out var s) && s is T typed ? typed : null;
    }

    public void SetConfig<T>(string key, T value)
    {
        _configs[key] = value;
    }

    public T? GetConfig<T>(string key)
    {
        return _configs.TryGetValue(key, out var val) && val is T typed ? typed : default;
    }

    public void Log(string message)
    {
        _log($"[Plugin] {message}");
    }

    public void Subscribe(string eventName, Action<object?> handler)
    {
        _eventHandlers.GetOrAdd(eventName, _ => new List<Action<object?>>()).Add(handler);
    }

    public void PublishEvent(string eventName, object? payload)
    {
        if (_eventHandlers.TryGetValue(eventName, out var handlers))
        {
            foreach (var handler in handlers)
            {
                try
                {
                    handler(payload);
                }
                catch (Exception ex)
                {
                    Log($"事件处理异常 ({eventName}): {ex.Message}");
                }
            }
        }
    }
}