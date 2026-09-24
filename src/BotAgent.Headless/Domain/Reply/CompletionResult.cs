namespace BotAgent.Domain.Reply;

/// <summary>模型一次生成的结构化结果。</summary>
/// <param name="Suitability">模型自评的发言适合度（0-100）；null = 模型未按 JSON 格式输出。</param>
/// <param name="Reply">要发出去的内容；null/空 = 不说话。</param>
/// <param name="RawText">模型原始输出（排障用）。</param>
/// <param name="StickerId">模型挑中的表情包 id；null = 不发图。</param>
/// <param name="ReplyToMessageId">模型自己指认的“我在回哪条消息”（对应提示里的 (#id)）；null = 没指定。</param>
/// <param name="PokeTargetId">模型想戳的人的 QQ 号（对应提示里的 poke 字段）；null = 不戳。</param>
/// <param name="Mood">模型顺手写的“我现在的心情”（≤ 24 字）；null = 没写。</param>
/// <param name="Listen">模型想“听一听”的歌名/歌手（机器人会去搜索并分析波形）；null = 不想听。</param>
/// <param name="ShareSong">模型想分享给群里的歌（机器人搜到后发一张网易云卡片）；null = 不分享。</param>
/// <param name="Speak">模型想“用语音说”的句子（机器人合成语音发出去）；null = 不发语音。</param>
/// <param name="Search">模型想上网查的问题（机器人真去搜，下一轮把结果给它）；null = 不搜。</param>
/// <param name="Read">模型想读的网页地址（机器人抓正文，下一轮把正文给它）；null = 不读。</param>
/// <param name="Vibe">它读到的**群里的情绪氛围**（开心/吐槽/低落/求助/生气/吵架/中性…）；null = 没说。</param>
/// <param name="VibeNote">给上一行补一句人话（例：“在吐槽加班，情绪烦燥”），会交给下一轮的自己；null = 没写。</param>
public readonly record struct CompletionResult(int? Suitability, string? Reply, string? RawText, string? StickerId = null, long? ReplyToMessageId = null, long? PokeTargetId = null, string? Mood = null, string? Listen = null, string? ShareSong = null, string? Speak = null, bool Both = false, string? Search = null, string? Read = null, string? Vibe = null, string? VibeNote = null,
    /// <summary>
    /// 模型给这句语音定的**情绪**（happy/sad/angry/surprised/fearful/disgusted/neutral）；
    /// null = 它没表态 → 用面板里配的默认情绪。
    /// </summary>
    string? VoiceEmotion = null,
    /// <summary>模型给这句语音定的**语速**（1.0 = 原速，0.5~2.0）；null = 用面板默认。</summary>
    double? VoiceSpeed = null,
    /// <summary>模型给这句语音定的**音调**（-12~+12）；null = 用面板默认。</summary>
    int? VoicePitch = null,
    
    /// <summary>上游回 200 但没给 choices（网关吞回复/风控）—— 与“模型自己决定沉默”不是一回事。</summary>
    bool UpstreamEmpty = false,

    /// <summary>
    /// 模型这一轮建议的动作（V3 §8.1 的 <c>action</c>，已由服务端归一化）。
    /// 缺省 = 旧协议没有这个字段，按“正常回复”处理。
    /// </summary>
    ReplyAction Action = ReplyAction.Reply,

    /// <summary>结构化原因码（短串、**不含正文**）：这轮为什么发 / 为什么不发。</summary>
    string? ReasonCode = null,

    /// <summary>
    /// 模型想调的工具名（已清洗成短标识符）。**仅用于记录与审批展示**：
    /// 服务端只认自己固定的那个假工具，模型写别的名字照样不执行（V3 §9.1）。
    /// </summary>
    string? ToolId = null,

    /// <summary>
    /// 模型想问的问题（仅当 <c>action=ask</c> 且面板「允许提问」打开时才有值）。
    /// **不是正文**：<see cref="Reply"/> 仍为 null；服务端会包上编号与有效期再发。
    /// </summary>
    string? QuestionText = null,

    /// <summary>模型输出不合法（非法动作 / 空回复 / 上游空响应）—— 与“它自己选择不说”区分开。</summary>
    bool Malformed = false);

/// <summary>
/// 解析器要"说给日志听"的一件事（<see cref="ModelOutputParser" /> 是纯函数，不自己写日志）。
/// 宿主（<c>OpenAiClient</c>）按自己的口径把它渲染成原来的那几行 —— 措辞是对外契约，别改。
/// </summary>
/// <param name="Kind">哪一类现场。</param>
/// <param name="Length">相关的**长度**（可见字符数）；只记形状、不记正文（V3 §8.2）。</param>
/// <param name="ReplyChar">单字回复场景里那一个字（"模型只回了 1 个字“X”" 要印出来）。</param>
/// <param name="Preview">已经截断过的原文片段（单字场景要印；日志会贴出来排障，所以要短）。</param>
/// <param name="ReasonCode">判定给出的原因码（<see cref="ParseNoticeKind.VerdictRejected" /> 用）。</param>
public readonly record struct ParseNotice(
    ParseNoticeKind Kind,
    int Length = 0,
    string? ReplyChar = null,
    string? Preview = null,
    string? ReasonCode = null);

/// <summary>解析现场的类别（每一类对应宿主日志里的一种写法）。</summary>
public enum ParseNoticeKind
{
    /// <summary>看似 JSON 但格式不对 → 沉默（别把代码发进群）。</summary>
    SchemaMismatch,

    /// <summary>非 JSON 且极短 → 疑似被上游截断，沉默。</summary>
    TruncatedOutput,

    /// <summary>只回了一个字的噪声 → 沉默（群里那个“彫”）。</summary>
    SingleCharNoise,

    /// <summary>只回了一个字、但在口语白名单里 → 照发（留一条现场）。</summary>
    SingleCharReply,

    /// <summary>正文里夹带了静默标记 → 照发，但说明它把标记当内容用了。</summary>
    SilentMarkerInBody,

    /// <summary>判定未通过（非法动作 / 超长控制字段…）→ 沉默。</summary>
    VerdictRejected,
}

/// <summary>解析的产物：结构化结果 + 要说给日志听的那几件事。</summary>
public readonly record struct ParsedModelOutput(
    CompletionResult Result,
    IReadOnlyList<ParseNotice> Notices);
