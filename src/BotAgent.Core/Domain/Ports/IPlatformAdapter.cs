using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 平台适配器统一抽象：负责平台标识、连接状态、能力声明与归一化入站消息。
/// </summary>
public interface IPlatformAdapter
{
    PlatformContext Context { get; }

    string DisplayName { get; }

    string Tag { get; }

    bool IsConnected { get; }

    PlatformCapabilities Capabilities { get; }

    string? LastErrorCode { get; }

    event Action<InboundMessage>? InboundReceived;

    event Action<bool>? ConnectionChanged;
}

/// <summary>
/// 平台出站消息投递端口。
/// </summary>
public interface IPlatformMessenger
{
    PlatformContext Context { get; }

    PlatformCapabilities Capabilities { get; }

    Task<DeliveryResult> SendAsync(
        PlatformContext context,
        OutboundMessage message,
        CancellationToken ct = default);

    Task<(string? Text, string? SenderId)> GetQuotedMessageAsync(
        MessageRef messageRef,
        CancellationToken ct = default);
}

/// <summary>
/// 平台在线状态与能力快照（不含聊天正文与凭据）。
/// </summary>
public sealed record PlatformStatusSnapshot(
    string PlatformId,
    string AccountScope,
    string DisplayName,
    string Tag,
    bool Enabled,
    bool Connected,
    PlatformCapabilities Capabilities,
    string? LastErrorCode = null)
{
    public string InstanceId { get; init; } = string.Empty;
}

/// <summary>
/// 平台适配器注册表只读端口。
/// </summary>
public interface IPlatformRegistry
{
    IReadOnlyList<IPlatformAdapter> Adapters { get; }

    IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default);

    IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default);

    /// <summary>Looks up one exact instance, never another instance of the same account.</summary>
    IPlatformAdapter? GetAdapter(PlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var matches = Adapters.Where(adapter => SameContext(adapter.Context, context)).Take(2).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    /// <summary>Safe compatibility default for registries without a messenger instance index.</summary>
    IPlatformMessenger? GetMessenger(PlatformContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!ValidContext(context)) return null;
        var candidates = Adapters.Where(adapter => ValidContext(adapter.Context)
            && PlatformId.Normalize(adapter.Context.PlatformId) == PlatformId.Normalize(context.PlatformId)
            && string.Equals(adapter.Context.AccountScope, context.AccountScope, StringComparison.Ordinal))
            .Take(2).ToArray();
        if (candidates.Length != 1 || !SameContext(candidates[0].Context, context)) return null;
        var messenger = GetMessenger(context.PlatformId, context.AccountScope);
        return messenger is not null && SameContext(messenger.Context, context) ? messenger : null;
    }

    private static bool SameContext(PlatformContext left, PlatformContext right)
        => ValidContext(left) && ValidContext(right)
            && PlatformId.Normalize(left.PlatformId) == PlatformId.Normalize(right.PlatformId)
            && string.Equals(left.AccountScope, right.AccountScope, StringComparison.Ordinal)
            && string.Equals(left.InstanceId, right.InstanceId, StringComparison.Ordinal);

    private static bool ValidContext(PlatformContext context)
        => context is not null && !string.IsNullOrWhiteSpace(context.PlatformId)
            && !string.IsNullOrWhiteSpace(context.AccountScope) && context.InstanceId is not null
            && (context.InstanceId.Length == 0 || !string.IsNullOrWhiteSpace(context.InstanceId));

    IReadOnlyList<PlatformStatusSnapshot> GetSnapshots();
}
