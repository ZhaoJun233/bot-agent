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
    /// <summary>按节奏分句发一段回复（返回是否真的发出去了）。</summary>
    Task<bool> SendWithCadenceAsync(bool isGroup, long targetId, string reply, long? replyTo);

    /// <summary>往某个会话直发一段纯文本（不走节奏分句）。</summary>
    Task SendPlainAsync(BotConversation conversation, string text);

    /// <summary>回一条"审批/提问"类消息（入站消息给的被动回复窗口）。</summary>
    Task SendApprovalReplyAsync(QqChatMessage msg, string text);
}
