using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Digest;

/// <summary>单条同源历史消息摘要切片（已剔除撤回消息，不含跨会话标识）。</summary>
public sealed record DigestMessageItem(
    long Seq,
    string Role,
    string SenderLabel,
    string Text,
    long TimestampUnix);

/// <summary>会话纪要包运行期参数快照。</summary>
public sealed record DigestOptions(
    int DefaultMessageCount = 80,
    int MaxLookbackHours = 24,
    int CooldownSeconds = 20);

/// <summary>
/// 宿主为 <c>preset.feature.digest</c> 提供的严格同会话只读窗口与模型总结端口。
/// <c>sourceKey</c> 由宿主工具执行器从当前会话直接传入，绝不接受模型参数指定。
/// </summary>
public interface IDigestHostPorts
{
    DigestOptions ReadOptions();
    IReadOnlyList<DigestMessageItem> ReadConversationWindow(string sourceKey, long? sinceUnix, int maxMessages, string? focusKeyword);
    Task<string?> CompleteDigestAsync(string systemPrompt, string transcriptBlock, CancellationToken ct);
}

/// <summary>会话纪要包向宿主暴露的诊断与纪要能力贡献。</summary>
public interface IDigestContribution : IPluginContribution
{
    bool IsReady { get; }
    Task<(bool Ok, string? Summary, string ReasonCode)> SummarizeAsync(string sourceKey, int? count, int? hours, string? focus, CancellationToken ct);
    void ClearConversation(string sourceKey) { }
}
