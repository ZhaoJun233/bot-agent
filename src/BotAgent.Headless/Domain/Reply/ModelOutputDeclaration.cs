using System.Text.Json;

namespace BotAgent.Domain.Reply;

/// <summary>
/// 模型在 JSON 里**声明了什么**（纯读字段、不做业务判断）：适合度 / 正文 / 挑的表情包 /
/// 引用哪条 / 戳谁 / 想听想分享的歌 / 语音与其语气参数 / 联网查什么读哪页 / 心情 / 氛围 /
/// 以及结构化动作（action / reasonCode / toolRequest）。
///
/// 与判定的分工（批次 6 抽出来的边界，别混回去）：
///   • **这里**只把 JSON 字段读成强类型，并做"形状清洗"（去 # 前缀、去掉超范围的数值、
///     丢掉非字符串、工具名从对象里抠出来）—— 每一步都是"模型写得合不合法"；
///   • **判定**（发不发 / 发什么 / 为什么、副作用字段要不要作废）在
///     <see cref="ReplyDecisionRules" /> + <see cref="ModelOutputParser" />。
///
/// ⚠ 这里解出来的东西**都不构成授权**：工具名要过服务端的固定登记表、poke/replyTo 要过上下文校验。
/// </summary>
/// <param name="Suitability">模型自评的发言适合度（0-100）；null = 没给或给得不合法。</param>
/// <param name="Reply">模型声明要发出去的正文（已 Trim）；null = 没给。</param>
/// <param name="Vibe">归一后的群氛围（<see cref="ModelOutputText.NormalizeVibe" />）。</param>
/// <param name="VibeNote">给氛围补的一句人话（≤ 40 字）。</param>
/// <param name="StickerId">表情包 id（小写十六进制，4~32 位）。</param>
/// <param name="ReplyToId">模型指认"在回哪条"的消息编号（正整数）。</param>
/// <param name="PokeTargetId">想戳的人（正整数；<c>"pokeBack": true</c> 解出来是 0 = 交给上层挑人）。</param>
/// <param name="Mood">模型顺手写的心情。</param>
/// <param name="Listen">想听的歌名（2~60 字）。</param>
/// <param name="ShareSong">想分享的歌名（2~60 字）。</param>
/// <param name="Both">语音之外还要把文字也发出去。</param>
/// <param name="Speak">要用语音说的那句话（<c>true</c> = 就用 reply）。</param>
/// <param name="VoiceEmotion">语音情绪（只认云端那 7 个名字）。</param>
/// <param name="VoiceSpeed">语音语速（0.5~2.0）。</param>
/// <param name="VoicePitch">语音音调（-12~12）。</param>
/// <param name="Search">想联网查的问题（2~120 字）。</param>
/// <param name="Read">想读的网页（http(s)、≤ 500 字）。</param>
/// <param name="Action">声明的动作原文（null = 旧协议没这个字段；非字符串会写成 <c>(non-string)</c>）。</param>
/// <param name="Reason">声明的原因码（reasonCode / reason_code / reason）。</param>
/// <param name="Tool">想调的工具名（还没过登记表）。</param>
public readonly partial record struct ModelOutputDeclaration(
    int? Suitability,
    string? Reply,
    string? Vibe,
    string? VibeNote,
    string? StickerId,
    long? ReplyToId,
    long? PokeTargetId,
    string? Mood,
    string? Listen,
    string? ShareSong,
    bool Both,
    string? Speak,
    string? VoiceEmotion,
    double? VoiceSpeed,
    int? VoicePitch,
    string? Search,
    string? Read,
    string? Action,
    string? Reason,
    string? Tool)
{
    /// <summary>
    /// 把模型输出里的 JSON 对象读成声明（只读、不判定；格式不合法的一律当"没给"）。
    /// ⚠ 名字不能叫 <c>Read</c>：本类型有一个叫 <c>Read</c> 的业务字段（"读哪一页"）。
    /// </summary>
    public static ModelOutputDeclaration FromJson(JsonElement root)
    {
        var (suitability, reply, vibe, vibeNote) = ReadReply(root);
        var (stickerId, replyToId, pokeTargetId) = ReadTargets(root);
        var (listen, shareSong, mood) = ReadMedia(root);
        var (both, speak, voiceEmotion, voiceSpeed, voicePitch) = ReadVoice(root);
        var (search, read) = ReadNet(root);
        var (action, reason, tool) = ReadDecision(root);

        return new ModelOutputDeclaration(
            suitability, reply, vibe, vibeNote, stickerId, replyToId, pokeTargetId, mood,
            listen, shareSong, both, speak, voiceEmotion, voiceSpeed, voicePitch,
            search, read, action, reason, tool);
    }

    /// <summary>结构化动作：action / reasonCode / toolRequest。</summary>
    private static (string? Action, string? Reason, string? Tool) ReadDecision(JsonElement root)
    {
        // ── 结构化动作（V3 §8.1 / §8.2）：action = reply|silent|ask|tool ──
        // 解析与判定都不在这里做业务决定，只把“候选动作 + 正文”交给纯规则类，
        // 由它给出「发不发 / 发什么 / 为什么」—— 这样判定逻辑能被确定性单测覆盖。
        string? action = null;
        if (root.TryGetProperty("action", out var act) || root.TryGetProperty("动作", out act))
        {
            action = act.ValueKind == JsonValueKind.String ? act.GetString() : "(non-string)";
        }

        string? reason = null;
        if (root.TryGetProperty("reasonCode", out var rc) ||
            root.TryGetProperty("reason_code", out rc) ||
            root.TryGetProperty("reason", out rc))
        {
            reason = rc.ValueKind == JsonValueKind.String ? rc.GetString() : null;
        }

        // 模型想调的工具（V3 §8.1 的 toolRequest）：字符串，或 {"tool": "…"} / {"id": "…"} / {"name": "…"}。
        // 这里只是把候选名字抠出来交给判定规则清洗；**它不构成授权** ——
        // 服务端只认自己固定的那个工具（见 ApprovalFlow），别的名字连审批单都开不出来。
        string? tool = null;
        if (root.TryGetProperty("toolRequest", out var tr) ||
            root.TryGetProperty("tool_request", out tr) ||
            root.TryGetProperty("tool", out tr))
        {
            if (tr.ValueKind == JsonValueKind.String)
            {
                tool = tr.GetString();
            }
            else if (tr.ValueKind == JsonValueKind.Object)
            {
                if (tr.TryGetProperty("tool", out var t1) && t1.ValueKind == JsonValueKind.String)
                {
                    tool = t1.GetString();
                }
                else if (tr.TryGetProperty("id", out var t2) && t2.ValueKind == JsonValueKind.String)
                {
                    tool = t2.GetString();
                }
                else if (tr.TryGetProperty("name", out var t3) && t3.ValueKind == JsonValueKind.String)
                {
                    tool = t3.GetString();
                }
            }
        }

        return (action, reason, tool);
    }
}
