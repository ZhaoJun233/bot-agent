using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;
using BotAgent.Services.Qq;

namespace BotAgent.Services.Ports;

/// <summary>
/// 「把一段文本发出去」端口（由 <c>Services/Reply/PlainSender</c> 实现，见 §6.4）。
///
/// **为什么不在 `Domain/Ports/`**（对 §6.4 的又一处偏差，与 <c>ISettingsRepository</c> 同一条理由）：
/// 它的签名要用会话与入站消息这两个**服务层**类型（<c>BotConversation</c> / <c>QqChatMessage</c>），
/// 端口可以放在离它服务的层最近的地方，但不能让 Domain 去引用服务层的类型。
///
/// 为什么要有它：回复链只该说"把这段发出去（按节奏分句、带上引用）"，
/// 不该知道分句表、脱敏、记账与协议端细节捆在哪一个具体类里。
/// </summary>
public interface IQqMessageSender
{
    /// <summary>
    /// 按节奏分句发一段回复。返回**逐段**的发送结果：调用方据此只把真正发出去的段落写进会话历史
    /// （全失败、部分成功、全部成功三种情形都能分辨；见 RFC v2.1 · issue #14）。
    /// </summary>
    Task<CadenceSendReport> SendWithCadenceAsync(bool isGroup, long targetId, string reply, long? replyTo, bool directAddress = false);

    /// <summary>完整会话 key 用于发送记账；不支持的旧实现明确拒绝，不能降级丢失账号/thread。</summary>
    Task<CadenceSendReport> SendWithCadenceAsync(string sourceKey, bool isGroup, long targetId,
        string reply, long? replyTo, bool directAddress = false)
        => throw new NotSupportedException("Full conversation scope is not supported by this sender.");

    /// <summary>往某个会话直发一段纯文本（不走节奏分句）。</summary>
    Task SendPlainAsync(BotConversation conversation, string text);

    /// <summary>回一条"审批/提问"类消息（入站消息给的被动回复窗口）。</summary>
    Task SendApprovalReplyAsync(QqChatMessage msg, string text);
}

/// <summary>
/// 一次分句发送的结果：<see cref="SentSegments" /> 是**真的发出去**的段落（按顺序），
/// <see cref="FailureReasonCode" /> 非 null = 中途有段落失败（或整条被安全闸门拦下，一段都没发）。
/// 只带形状与原因码，不引入新的正文副本 —— 已发出的段落本来就在发送链路上。
/// </summary>
public sealed record CadenceSendReport(IReadOnlyList<string> SentSegments, string? FailureReasonCode = null)
{
    /// <summary>这一轮根本没有尝试发送（调用方占位用）。</summary>
    public static CadenceSendReport None { get; } = new(Array.Empty<string>(), "not_attempted");

    /// <summary>至少有段落发出去。</summary>
    public bool AnySent => SentSegments.Count > 0;

    /// <summary>全部发出去（没有任何失败）。</summary>
    public bool AllSent => FailureReasonCode is null;

    /// <summary>已发出的段落拼回一段文本（部分成功时写进会话历史的就是它）。</summary>
    public string Text => string.Join("\n", SentSegments);
}
