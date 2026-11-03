using System;
using System.Security.Cryptography;
using System.Text;

namespace BotAgent.Domain.Permissions;

/// <summary>
/// 审批票据的**签发与验签**（#137）。
///
/// 为什么需要：票据原本只是一个公开 record，闸门只比对工具与会话 —— 任何拿到结构定义的人
/// 都能 `new ApprovalTicket(...)` 直接通过。现在票据多了 <see cref="ApprovalTicket.Signature" />，
/// 只有 <see cref="ApprovalStore" /> 在消费时用**进程内随机密钥**签得出来，闸门验签不过一律拒绝。
///
/// 边界（诚实）：
///   • 密钥随进程生成：重启后旧票据全部失效 —— 票据本来就只在**同一轮**里活着，这是想要的；
///   • 同进程里能调用 <see cref="Sign" /> 的代码（internal，只有本程序集）仍可签发；
///     这是进程内信任边界，不是加密信任边界（真正的授权仍然是台账里的一次性消费）。
/// </summary>
internal static class ApprovalTicketSigner
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    internal static string Sign(string requestId, string toolId, string conversationKey, DateTimeOffset grantedAt)
    {
        var payload = "approval-ticket/v1\n"
                      + (requestId ?? string.Empty) + "\n"
                      + (toolId ?? string.Empty) + "\n"
                      + (conversationKey ?? string.Empty) + "\n"
                      + grantedAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(payload));
        return Convert.ToBase64String(mac);
    }

    internal static bool Verify(ApprovalTicket ticket)
    {
        if (ticket.Signature is not { Length: > 0 } given) return false;
        var expected = Sign(ticket.RequestId, ticket.ToolId, ticket.ConversationKey, ticket.GrantedAt);
        var a = Encoding.ASCII.GetBytes(expected);
        var b = Encoding.ASCII.GetBytes(given);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }
}
