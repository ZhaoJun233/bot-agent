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
    string? LastErrorCode = null);

/// <summary>
/// 平台适配器注册表只读端口。
/// </summary>
public interface IPlatformRegistry
{
    IReadOnlyList<IPlatformAdapter> Adapters { get; }

    IPlatformAdapter? GetAdapter(string platformId, string accountScope = AccountScope.Default);

    IPlatformMessenger? GetMessenger(string platformId, string accountScope = AccountScope.Default);

    IReadOnlyList<PlatformStatusSnapshot> GetSnapshots();
}
