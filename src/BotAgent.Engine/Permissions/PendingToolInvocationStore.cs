using System.Text.Json.Nodes;

namespace BotAgent.Services.Permissions;

/// <summary>一条等待人工批准的真实工具调用快照（纯内存；冻结深拷贝入参，一次性消费）。</summary>
internal sealed record PendingToolInvocation(
    string RequestId,
    string ToolId,
    string ConversationKey,
    JsonObject FrozenArguments,
    string SanitizedSummary,
    DateTimeOffset ExpiresAt)
{
    public override string ToString() => "PendingToolInvocation";
}

/// <summary>
/// 纯内存待批工具调用台账（重启即自动作废，上限 128 条；绝不落库）。
/// 只存冻结的 JSON 参数深拷贝与脱敏参数键名摘要，杜绝“审批看 A、执行跑 B”的参数篡改（TOCTOU）。
/// </summary>
internal sealed class PendingToolInvocationStore
{
    public const int MaxEntries = 128;

    private readonly object _sync = new();
    private readonly Dictionary<string, PendingToolInvocation> _items = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();

    public void Put(PendingToolInvocation invocation, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        var cloned = (JsonObject)(invocation.FrozenArguments?.DeepClone() ?? new JsonObject());
        var normalized = invocation with { FrozenArguments = cloned };
        lock (_sync)
        {
            if (_items.ContainsKey(normalized.RequestId))
                _order.Remove(normalized.RequestId);

            if (_items.Count >= MaxEntries && !_items.ContainsKey(normalized.RequestId))
                EvictLocked(now);

            _items[normalized.RequestId] = normalized;
            _order.Add(normalized.RequestId);
        }
    }

    public bool TryTake(string requestId, out PendingToolInvocation invocation)
    {
        invocation = null!;
        if (string.IsNullOrWhiteSpace(requestId)) return false;
        lock (_sync)
        {
            if (!_items.Remove(requestId, out var found)) return false;
            _order.Remove(requestId);
            invocation = found;
            return true;
        }
    }

    public void Remove(string requestId)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        lock (_sync)
        {
            if (_items.Remove(requestId))
                _order.Remove(requestId);
        }
    }

    public int Prune(DateTimeOffset now)
    {
        lock (_sync)
        {
            var expired = _items.Values.Where(x => now >= x.ExpiresAt).Select(x => x.RequestId).ToArray();
            foreach (var id in expired)
            {
                _items.Remove(id);
                _order.Remove(id);
            }
            return expired.Length;
        }
    }

    public static string BuildSanitizedSummary(string toolId, JsonObject? arguments)
    {
        if (arguments is null || arguments.Count == 0)
            return $"{toolId}（无参数）";
        var keys = arguments
            .Select(p => p.Key)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Take(4)
            .ToArray();
        return keys.Length == 0
            ? $"{toolId}（无参数）"
            : $"{toolId}（参数键: {string.Join(", ", keys)}）";
    }

    private void EvictLocked(DateTimeOffset referenceNow)
    {
        var firstExpired = _items.Values.FirstOrDefault(x => referenceNow >= x.ExpiresAt)?.RequestId;
        var victim = firstExpired ?? (_order.Count > 0 ? _order[0] : null);
        if (victim is null) return;
        _items.Remove(victim);
        _order.Remove(victim);
    }
}
