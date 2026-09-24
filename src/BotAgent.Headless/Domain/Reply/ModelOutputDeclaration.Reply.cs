using System.Text.Json;

namespace BotAgent.Domain.Reply;

/// <summary>
/// <see cref="ModelOutputDeclaration" /> 的**回复与目标**读取（只读不判）：适合度 / 正文 / 氛围 / 氛围补注，
/// 以及表情包、引用目标、戳谁。体积/形状不合法的值一律当“没给”，判定不在这里。
/// </summary>
public readonly partial record struct ModelOutputDeclaration
{
    /// <summary>适合度 / 正文 / 氛围 / 氛围补注。</summary>
    private static (int? Suitability, string? Reply, string? Vibe, string? VibeNote) ReadReply(JsonElement root)
    {
        int? suitability = null;
        if (root.TryGetProperty("suitability", out var s))
        {
            suitability = s.ValueKind switch
            {
                JsonValueKind.Number => ModelOutputText.ReadScore(s),
                JsonValueKind.String => int.TryParse(s.GetString(), out var parsed) ? parsed : null,
                _ => null
            };
        }

        // 只有字符串才算回复；reply 是对象/数组时不能 GetString（会抛）
        string? reply = null;
        if (root.TryGetProperty("reply", out var r) && r.ValueKind == JsonValueKind.String)
        {
            reply = r.GetString()?.Trim();
        }

        // 群里的情绪氛围（模型自己的读法）：归一到一个固定集合，程序侧才能按它调发言策略
        string? vibe = null;
        if (root.TryGetProperty("vibe", out var vb) && vb.ValueKind == JsonValueKind.String)
        {
            vibe = ModelOutputText.NormalizeVibe(vb.GetString());
        }

        string? vibeNote = null;
        if (root.TryGetProperty("vibeNote", out var vn) && vn.ValueKind == JsonValueKind.String)
        {
            vibeNote = ModelOutputText.Truncate((vn.GetString() ?? string.Empty).Trim(), 40);
            if (vibeNote.Length == 0)
            {
                vibeNote = null;
            }
        }

        return (suitability, reply, vibe, vibeNote);
    }

    /// <summary>表情包 / 引用目标 / 戳谁。</summary>
    private static (string? StickerId, long? ReplyToId, long? PokeTargetId) ReadTargets(JsonElement root)
    {
        // 表情包：模型可能只发图不说话，所以单独解析（id 去掉 # 前缀，拒绝奇怪的值）
        string? stickerId = null;
        if (root.TryGetProperty("sticker", out var st) || root.TryGetProperty("stickerId", out st))
        {
            var raw = st.ValueKind switch
            {
                JsonValueKind.String => st.GetString(),
                JsonValueKind.Number => st.ToString(),
                _ => null
            };

            var cleaned = raw?.Trim().TrimStart('#').Trim();
            if (!string.IsNullOrWhiteSpace(cleaned) &&
                cleaned.Length is >= 4 and <= 32 &&
                cleaned.All(char.IsAsciiHexDigit))
            {
                stickerId = cleaned.ToLowerInvariant();
            }
        }

        // 模型自己指认的“我在回哪条”（replyTo）。只取正整数：
        // 具体是否采信由上层校验（必须在本次上下文里），这里不做业务判断。
        long? replyToId = null;
        if (root.TryGetProperty("replyTo", out var rt) || root.TryGetProperty("reply_to", out rt))
        {
            replyToId = rt.ValueKind switch
            {
                JsonValueKind.Number when rt.TryGetInt64(out var value) => value,
                JsonValueKind.String when long.TryParse(rt.GetString(), out var parsed) => parsed,
                _ => null
            };

            if (replyToId is <= 0)
            {
                replyToId = null;
            }
        }

        // 模型想戳谁（poke，也可以是 pokeBack）。同样只取正整数，
        // “这个人到底存不存在”由上层用上下文校验（能防住模型编造号码）。
        long? pokeTargetId = null;
        if (root.TryGetProperty("poke", out var pk) || root.TryGetProperty("pokeBack", out pk) || root.TryGetProperty("pokeTo", out pk))
        {
            pokeTargetId = pk.ValueKind switch
            {
                JsonValueKind.Number when pk.TryGetInt64(out var value) => value,
                JsonValueKind.String when pk.ValueKind == JsonValueKind.String && long.TryParse(pk.GetString(), out var parsed) => parsed,
                JsonValueKind.True => 0, // "pokeBack": true = 戳回去，具体号码交给上层（别在这里猜）
                _ => null
            };

            if (pokeTargetId is <= 0)
            {
                pokeTargetId = null;
            }
        }

        return (stickerId, replyToId, pokeTargetId);
    }
}
