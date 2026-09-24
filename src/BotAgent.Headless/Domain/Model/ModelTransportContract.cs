using System.Text.Json.Nodes;

namespace BotAgent.Domain.Model;

/// <summary>一次请求的**组装结果**（请求体 + 这一轮真带了几张图）。</summary>
public readonly record struct BuiltRequest(JsonObject Payload, int AttachedImages, List<long> AttachedImageIds);

/// <summary>一次发送的**结果**：拿到的 JSON，以及"是不是去掉图片后重试成功的"。</summary>
public readonly record struct SendOutcome(string Json, bool TextOnlyRetry);
