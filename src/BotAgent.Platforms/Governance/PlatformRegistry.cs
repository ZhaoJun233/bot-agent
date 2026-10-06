using System.Collections.Concurrent;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Platforms;

/// <summary>
/// 平台适配器与出站投递端注册表实现（服务层仅依赖领域端口，不反向依赖 Adapters 具体类型）。
/// </summary>
public sealed class PlatformRegistry : IPlatformRegistry
{
    private readonly List<IPlatformAdapter> _adapters;
    private readonly List<IPlatformMessenger> _messengers;
    private readonly ConcurrentDictionary<(string PlatformId, string AccountScope), IPlatformAdapter> _adaptersByKey = new();
    private readonly ConcurrentDictionary<(string PlatformId, string AccountScope), IPlatformMessenger> _messengersByKey = new();

    public PlatformRegistry(
        IEnumerable<IPlatformAdapter> adapters,
        IEnumerable<IPlatformMessenger> messengers)
    {
        _adapters = [.. adapters.Where(a => a is not null)];
        _messengers = [.. messengers.Where(m => m is not null)];

        foreach (var adapter in _adapters)
        {
            var key = (PlatformId.Normalize(adapter.Context.PlatformId), adapter.Context.AccountScope);
            _adaptersByKey[key] = adapter;
        }

        foreach (var messenger in _messengers)
        {
            var key = (PlatformId.Normalize(messenger.Context.PlatformId), messenger.Context.AccountScope);
            _messengersByKey[key] = messenger;
        }
    }

    public IReadOnlyList<IPlatformAdapter> Adapters => _adapters;

    public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default)
    {
        var key = (PlatformId.Normalize(platformId), string.IsNullOrWhiteSpace(accountScope) ? AccountScope.Default : accountScope);
        return _adaptersByKey.TryGetValue(key, out var adapter) ? adapter : null;
    }

    public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default)
    {
        var key = (PlatformId.Normalize(platformId), string.IsNullOrWhiteSpace(accountScope) ? AccountScope.Default : accountScope);
        return _messengersByKey.TryGetValue(key, out var messenger) ? messenger : null;
    }

    public IReadOnlyList<PlatformStatusSnapshot> GetSnapshots()
    {
        var list = new List<PlatformStatusSnapshot>(_adapters.Count);
        foreach (var a in _adapters)
        {
            list.Add(new PlatformStatusSnapshot(
                PlatformId: a.Context.PlatformId,
                AccountScope: a.Context.AccountScope,
                DisplayName: a.DisplayName,
                Tag: a.Tag,
                Enabled: true,
                Connected: a.IsConnected,
                Capabilities: a.Capabilities,
                LastErrorCode: a.LastErrorCode));
        }

        return list;
    }
}
