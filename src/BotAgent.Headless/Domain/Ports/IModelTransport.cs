using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 模型出网的**传输层**端口（由 <c>Adapters/Model/ModelTransport</c> 实现，见 §5.1）。
///
/// 它管的是"怎么把请求发出去、发不通怎么办"：请求体组装（含多模态图片）、鉴权头、
/// 三条兜底重试（空 choices 重试一次；带图两轮都空 → 去掉图片再试一次）。
/// 客户端（<c>OpenAiClient</c>）只管"说什么"与"回来的怎么判"—— 两件事不再缠在一起。
/// </summary>
public interface IModelTransport
{
    /// <summary>组装这一次的请求体（把上下文窗口、系统提示词、可引用消息与图片都算进去）。</summary>
    Task<BuiltRequest> BuildAsync(
        IReadOnlyList<ChatMessage> window, string systemContent, IReadOnlyCollection<long> quotableIds, CancellationToken ct);

    /// <summary>把请求发出去（含三条兜底重试）；返回拿到的 JSON 与"是不是去掉图片后重试成功的"。</summary>
    Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, CancellationToken ct);
}
