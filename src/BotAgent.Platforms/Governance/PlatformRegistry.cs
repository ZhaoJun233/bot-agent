using System.Collections.Concurrent;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Platforms;

/// <summary>
/// 平台适配器与出站投递端注册表实现（服务层仅依赖领域端口，不反向依赖 Adapters 具体类型）。
/// </summary>
public sealed class PlatformRegistry : IPlatformRegistry
{
    private readonly IReadOnlyList<IPlatformAdapter> _adapters;
    private readonly IReadOnlyList<IPlatformMessenger> _messengers;
    private readonly ConcurrentDictionary<(string PlatformId, string AccountScope, string InstanceId), IPlatformAdapter> _adaptersByKey = new();
    private readonly ConcurrentDictionary<(string PlatformId, string AccountScope, string InstanceId), IPlatformMessenger> _messengersByKey = new();

    public PlatformRegistry(
        IEnumerable<IPlatformAdapter> adapters,
        IEnumerable<IPlatformMessenger> messengers)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        ArgumentNullException.ThrowIfNull(messengers);
        _adapters = Array.AsReadOnly(adapters.Where(a => a is not null).ToArray());
        _messengers = Array.AsReadOnly(messengers.Where(m => m is not null).ToArray());

        foreach (var adapter in _adapters)
        {
            if (!TryCreateKey(adapter.Context, out var key))
                throw new ArgumentException("invalid_platform_context", nameof(adapters));
            if (!_adaptersByKey.TryAdd(key, adapter))
                throw new ArgumentException("duplicate_platform_instance", nameof(adapters));
        }

        foreach (var messenger in _messengers)
        {
            if (!TryCreateKey(messenger.Context, out var key))
                throw new ArgumentException("invalid_platform_context", nameof(messengers));
            if (!_messengersByKey.TryAdd(key, messenger))
                throw new ArgumentException("duplicate_platform_instance", nameof(messengers));
        }
    }

    public IReadOnlyList<IPlatformAdapter> Adapters => _adapters;

    public IPlatformAdapter? GetAdapter(PlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryCreateKey(context, out var key) && _adaptersByKey.TryGetValue(key, out var adapter)
            ? adapter : null;
    }

    public IPlatformMessenger? GetMessenger(PlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return TryCreateKey(context, out var key) && _messengersByKey.TryGetValue(key, out var messenger)
            ? messenger : null;
    }

    /// <summary>Legacy account-only lookup succeeds only when the registered instance is unique.</summary>
    public IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default)
        => FindUnique(_adaptersByKey, platformId, accountScope);

    public IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default)
        => FindUnique(_messengersByKey, platformId, accountScope);

    private static T? FindUnique<T>(
        ConcurrentDictionary<(string PlatformId, string AccountScope, string InstanceId), T> registrations,
        string platformId, string accountScope) where T : class
    {
        if (string.IsNullOrWhiteSpace(platformId) || string.IsNullOrWhiteSpace(accountScope)) return null;
        var platform = PlatformId.Normalize(platformId);
        var matches = registrations.Where(pair => pair.Key.PlatformId == platform
            && string.Equals(pair.Key.AccountScope, accountScope, StringComparison.Ordinal))
            .Select(pair => pair.Value).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static bool TryCreateKey(PlatformContext? context,
        out (string PlatformId, string AccountScope, string InstanceId) key)
    {
        key = default;
        if (context is null || string.IsNullOrWhiteSpace(context.PlatformId)
            || string.IsNullOrWhiteSpace(context.AccountScope) || context.InstanceId is null
            || (context.InstanceId.Length != 0 && string.IsNullOrWhiteSpace(context.InstanceId))) return false;
        key = (PlatformId.Normalize(context.PlatformId), context.AccountScope, context.InstanceId);
        return true;
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
                Enabled: true, // Loaded registration, not the desired configuration state.
                Connected: a.IsConnected,
                Capabilities: a.Capabilities,
                LastErrorCode: a.LastErrorCode)
            {
                InstanceId = a.Context.InstanceId,
            });
        }

        return list;
    }
}
