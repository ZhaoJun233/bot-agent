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

    /// <summary>
    /// 一次回包里的 token 用量（#98）。缓存侧两个字段是**三态**：
    /// <c>null</c> = 上游根本没提供这个字段（未知），<c>0</c> = 上游明确写了 0，正数 = 实际值。
    /// 统计里绝不能把未知当 0 —— 那会让「供应商不支持 / 字段没透传」看起来像「一次都没命中」。
    /// </summary>
    public readonly record struct ModelUsage(int PromptTokens, int CompletionTokens, int? CachedPromptTokens = null, int? CacheWriteTokens = null);

    /// <summary>
    /// 从回包里读**完整**用量（含缓存侧）。兼容几种上游形状：
    /// OpenAI 的 <c>usage.prompt_tokens_details.cached_tokens</c>、DeepSeek 的 <c>prompt_cache_hit_tokens</c>、
    /// Anthropic 的 <c>usage.cache_read_input_tokens</c> / <c>cache_creation_input_tokens</c>、
    /// Gemini 的 <c>usage.cachedContentTokenCount</c>，以及本仓协议适配器**归一化后**的同形状
    /// （见 ProtocolJson.Usage：prompt_tokens_details.cached_tokens / cache_write_tokens）。
    /// 未提供或非标准时返回 default（两个计数为 0、缓存侧为 null）。
    /// </summary>
    public static ModelUsage ReadUsageDetail(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("usage", out var usage) ||
                usage.ValueKind != JsonValueKind.Object)
            {
                return default;
            }

            var prompt = Number(usage, "prompt_tokens") ?? 0;
            var completion = Number(usage, "completion_tokens") ?? 0;
            var cached = Number(usage, "prompt_tokens_details", "cached_tokens")
                ?? Number(usage, "input_tokens_details", "cached_tokens")
                ?? Number(usage, "prompt_cache_hit_tokens")
                ?? Number(usage, "cache_read_input_tokens")
                ?? Number(usage, "cachedContentTokenCount");
            var written = Number(usage, "cache_write_tokens")
                ?? Number(usage, "cache_creation_input_tokens");
            return new ModelUsage(Math.Max(0, prompt), Math.Max(0, completion), cached, written);
        }
        catch (JsonException)
        {
            // 忽略非标准或损坏的 JSON
            return default;
        }
    }

    /// <summary>从 OpenAI 兼容响应中提取 token 用量（纯函数）。未提供或非标准时返回 (0, 0)。</summary>
    public static (int PromptTokens, int CompletionTokens) ReadUsage(string json)
    {
        var usage = ReadUsageDetail(json);
        return (usage.PromptTokens, usage.CompletionTokens);
    }

    /// <summary>按路径取一个非负整数；任一层缺失或不是数字都返回 null（= 未知，不是 0）。</summary>
    private static int? Number(JsonElement root, params string[] path)
    {
        var node = root;
        foreach (var part in path)
        {
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty(part, out node))
            {
                return null;
            }
        }

        return node.ValueKind == JsonValueKind.Number && node.TryGetInt32(out var value) ? Math.Max(0, value) : null;
    }
}
