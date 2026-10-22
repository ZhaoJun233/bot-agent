using System.Text.Json;

namespace BotAgent.Domain.Model;

/// <summary>
/// 模型回包的**形状判定**（纯函数）。它原来挂在传输层上（<c>ModelTransport.HasChoices</c>），
/// 而客户端（<c>OpenAiClient</c>）也要用它 —— 静态方法过不了端口，于是按"纯函数下沉"的老规矩搬到 Domain。
/// </summary>
public static class ModelJson
{
    /// <summary>回包里有没有 <c>choices</c>（空数组也算"没有"——上游把额度/风控错误塞进 200 里就是这样）。</summary>
    public static bool HasChoices(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object &&
                   doc.RootElement.TryGetProperty("choices", out var choices) &&
                   choices.ValueKind == JsonValueKind.Array &&
                   choices.GetArrayLength() > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>从 OpenAI 兼容响应中提取 token 用量（纯函数）。未提供或非标准时返回 (0, 0)。</summary>
    public static (int PromptTokens, int CompletionTokens) ReadUsage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return (0, 0);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("usage", out var usage) &&
                usage.ValueKind == JsonValueKind.Object)
            {
                var p = usage.TryGetProperty("prompt_tokens", out var pt) && pt.TryGetInt32(out var pv) ? pv : 0;
                var c = usage.TryGetProperty("completion_tokens", out var ct) && ct.TryGetInt32(out var cv) ? cv : 0;
                return (Math.Max(0, p), Math.Max(0, c));
            }
        }
        catch (JsonException)
        {
            // 忽略非标准或损坏的 JSON
        }

        return (0, 0);
    }
}
