using System.Text.Json;

namespace BotAgent.Domain.Reply;

/// <summary>
/// 模型输出的解析（**纯函数、零 IO**）：把一行 JSON / 一段纯文本变成
/// <see cref="CompletionResult" />（动作、正文、副作用字段），外加一串“要说给日志听的话”。
///
/// 为什么它在 domain（批次 6，§13.4）：这段是**行为核心** —— 非法动作要降级成沉默、
/// 超长的控制字段要丢掉、<c>Send=false ⇒ Content=null</c>、正文里夹带的 <c>[SILENT]</c> 不算控制位……
/// 每一条都直接决定“群里看起来什么样”。以前它埋在 <c>OpenAiClient</c>（2686 行）的私有大方法里，
/// 只能靠反射穿进去测；现在是一个能直接调、能单独断言的纯函数。
///
/// **不写日志**：需要让人看见的事情（截断、单字噪声、正文夹带标记…）以
/// <see cref="ParseNotice" /> 的形式**交回调用方**，由宿主按自己的日志口径写出来。
/// 这样 domain 保持零 IO，而日志措辞仍由适配层说了算（措辞是对外契约，不许因为搬家就变）。
/// </summary>
public static class ModelOutputParser
{
    /// <summary>纯文本回复的最短长度：1~2 个字的“回复”几乎都是上游被截断的碎片，不是真的想说话。</summary>
    private const int MinPlainTextReplyLength = 3;

    /// <summary>
    /// 可以单独成句的单字应答（中文口语里确实会这么用）。
    /// 白名单之外的单字一律按上游噪声处理 —— 群里的“彫”就是这么刷起来的。
    /// </summary>
    private const string SingleCharReplyWhitelist = "嗯哦啊哈呃哎咦喂喔唔草6?？!！~～";

    /// <summary>
    /// 解析模型输出。规则（逐条都是不变量，别在重构里松掉）：
    ///   • 空 / 全空白 → 沉默（模型没说话）
    ///   • 围栏 / 前后夹着解释文字 → 先把真正的 JSON 抠出来
    ///   • 看着像我们的 JSON 但解析不了 → 判为格式错误，**沉默**（绝不把 JSON 原文当成回复发出去）
    ///   • 完全不像 JSON（纯文本）→ 当普通回复，适合度未知
    ///   • 显式 <c>action=silent</c> 或 <c>[SILENT]</c> → 沉默（正文不进发送队列）
    ///   • 显式非 reply 的动作 → 正文之外的副作用字段一律作废（“说了不说”就不许发音/分享/出网/发图/戳）
    /// </summary>
    /// <param name="questionsEnabled">
    /// 面板上的「允许提问」开关（默认关）。关着时 <c>action=ask</c> 仍是安全静默
    /// （<c>ask_not_enabled</c>）；打开后才把问题文本带出来交给服务端走提问流程。
    /// 这个开关**不授予任何权限** —— 提问只是“往当前会话说一句带编号的话”。
    /// </param>
    public static ParsedModelOutput Parse(string? rawReply, bool questionsEnabled)
    {
        if (string.IsNullOrWhiteSpace(rawReply))
        {
            return new ParsedModelOutput(new CompletionResult(null, null, rawReply), Array.Empty<ParseNotice>());
        }

        var text = ModelOutputText.StripCodeFence(rawReply.Trim());

        // 模型很爱在 JSON 前面写一句解释（“好的，我来回：”）或者把 JSON 裹在围栏里再另起一段 ——
        // 以前这种“不以 { 开头”的输出会直接走纯文本分支，把整段 JSON 发进群里。
        // 这里先在全文里找“长得像我们约定的那个 JSON”的片段。
        if (!text.StartsWith('{') && ModelOutputText.TryExtractJsonBlock(text, out var extracted))
        {
            text = extracted;
        }

        // 再兜一层：纯文本里带着我们的字段名（suitability/reply/sticker…）说明它本来就是想输出 JSON，
        // 只是格式没弄对 —— 宁可沉默，也绝不把 JSON 代码吐进群里。
        if (!text.StartsWith('{') && ModelOutputText.LooksLikeSchemaJson(text))
        {
            // 只记结构化字段（长度），不记正文 —— V3 §8.2「不保存完整模型输出」
            return new ParsedModelOutput(
                new CompletionResult(null, null, rawReply, ReasonCode: "schema_mismatch", Malformed: true),
                new[] { new ParseNotice(ParseNoticeKind.SchemaMismatch, ModelOutputText.VisibleLength(text)) });
        }

        // 只有“以 { 开头”才当作 JSON 尝试。
        // 理由：真人语气里也会出现花括号（“这个 {a:1} 是啥”），那些必须当普通文本发出去。
        if (!text.StartsWith('{'))
        {
            // 纯文本兜底：有些模型就是不按 JSON 输出，这里必须放行。
            // 但**极短**的纯文本是个例外 —— 群里实测过单字“悼”刷屏：
            // 上游网关偶发只回一个字符，旧实现把它当正常回复发了出去，
            // 而模型又能从上下文里读到自己那条“悼”，于是开始自我复读。
            // 不到 3 个字、又不像 JSON，就当作“没有回复”，并把原文写进日志以便回溯上游异常。
            if (ModelOutputText.VisibleLength(text) < MinPlainTextReplyLength)
            {
                return new ParsedModelOutput(
                    new CompletionResult(null, null, rawReply, ReasonCode: "truncated_output", Malformed: true),
                    new[] { new ParseNotice(ParseNoticeKind.TruncatedOutput, ModelOutputText.VisibleLength(text)) });
            }

            // 纯文本兜底也要过判定：显式静默标记 [SILENT] 必须在这里被拦下，
            // 绝不能进 QQ 发送队列（V3 §8.2）。其余纯文本照发（legacy_text）。
            var plainVerdict = ReplyDecisionRules.Decide(action: null, reply: text);
            return new ParsedModelOutput(
                new CompletionResult(
                    null,
                    plainVerdict.Content,
                    rawReply,
                    Action: plainVerdict.Action,
                    ReasonCode: plainVerdict.ReasonCode,
                    Malformed: plainVerdict.Malformed),
                Array.Empty<ParseNotice>());
        }

        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        try
        {
            using var doc = JsonDocument.Parse(end > start ? text[start..(end + 1)] : text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return new ParsedModelOutput(
                    new CompletionResult(null, null, rawReply, ReasonCode: "json_not_object", Malformed: true),
                    Array.Empty<ParseNotice>());
            }

            // 字段读取整块在 ModelOutputDeclaration（只读字段、不做业务判断）——
            // 判定（发不发 / 发什么 / 为什么）与副作用裁剪留在这一步，让 Parse 一眼能看完。
            var declared = ModelOutputDeclaration.FromJson(root);

            var notices = new List<ParseNotice>();

            // 单字回复：中文口语里“嗯/哦/哈”确实是正常应答，其余单字基本都是上游噪声——
            // 群里实测就是单个“彫”在刷屏（上游偶发只回一个字符）。
            if (declared.Reply is { Length: 1 } && !SingleCharReplyWhitelist.Contains(declared.Reply[0]))
            {
                notices.Add(new ParseNotice(ParseNoticeKind.SingleCharNoise, ReplyChar: declared.Reply,
                    Preview: ModelOutputText.Truncate(rawReply, 80)));
                return new ParsedModelOutput(
                    new CompletionResult(declared.Suitability, null, rawReply, ReasonCode: "single_char_noise", Malformed: true),
                    notices);
            }

            // 1 个字的正常应答：照发。
            if (declared.Reply is { Length: 1 })
            {
                notices.Add(new ParseNotice(ParseNoticeKind.SingleCharReply, ReplyChar: declared.Reply,
                    Preview: ModelOutputText.Truncate(rawReply, 80)));
            }
            var verdict = ReplyDecisionRules.Decide(
                declared.Action, declared.Reply, declared.Reason,
                toolRequest: declared.Tool,
                askEnabled: questionsEnabled);

            if (verdict.Send && ReplyDecisionRules.ContainsSilentMarker(verdict.Content))
            {
                // 正文里夹带标记 ≠ 控制位：照常发送，但留一条现场（说明模型把标记当内容用了）
                notices.Add(new ParseNotice(ParseNoticeKind.SilentMarkerInBody));
            }

            if (verdict.Malformed)
            {
                // 只记结构化字段（原因码 + 长度）：正文可能转述群聊内容，不能进日志/面板
                notices.Add(new ParseNotice(ParseNoticeKind.VerdictRejected, ModelOutputText.VisibleLength(rawReply), ReasonCode: verdict.ReasonCode));
            }

            // 副作用字段（正文之外的动作：发音 / 分享 / 出网 / 发图 / 戳）只在**这一轮真的按 reply 处理**
            // 时才带出去（V3 §8.1：silent = 不发送任何 QQ 消息、不留隐藏副作用）。
            //   • 旧协议（压根没有 action 字段）：保持改造前行为 —— 空 reply + speak 仍是“只发语音”；
            //   • action=reply：同上（模型可以用 speak / sticker 表达“这句用声音说 / 用图说”）；
            //   • 显式写了非 reply 的动作（silent / ask / tool / 未知 / 超长 / 非字符串）：一律作废，
            //     免得“说了不说”却还发语音、分享歌曲或悄悄出网。
            var keepSideEffects = string.IsNullOrWhiteSpace(declared.Action)
                ? verdict.ReasonCode != "silent_marker"
                : verdict.Action == ReplyAction.Reply;

            return new ParsedModelOutput(
                new CompletionResult(
                    declared.Suitability,
                    verdict.Send ? verdict.Content : null,
                    rawReply,
                    keepSideEffects ? declared.StickerId : null,
                    keepSideEffects ? declared.ReplyToId : null,
                    keepSideEffects ? declared.PokeTargetId : null,
                    declared.Mood,
                    keepSideEffects ? declared.Listen : null,
                    keepSideEffects ? declared.ShareSong : null,
                    keepSideEffects ? declared.Speak : null,
                    keepSideEffects && declared.Both,
                    keepSideEffects ? declared.Search : null,
                    keepSideEffects ? declared.Read : null,
                    declared.Vibe,
                    declared.VibeNote,
                    declared.VoiceEmotion,
                    declared.VoiceSpeed,
                    declared.VoicePitch,
                    Action: verdict.Action,
                    ReasonCode: verdict.ReasonCode,
                    Malformed: verdict.Malformed,
                    ToolId: verdict.ToolId,
                    QuestionText: verdict.QuestionText),
                notices);
        }
        catch (JsonException)
        {
            // 长得像 JSON 却解析不了：判为格式错误 → 沉默。
            // （以前这里会落到“按普通文本处理”，把整段 JSON 发进群里。）
            return new ParsedModelOutput(
                new CompletionResult(null, null, rawReply, ReasonCode: "invalid_json", Malformed: true),
                Array.Empty<ParseNotice>());
        }
    }

}
