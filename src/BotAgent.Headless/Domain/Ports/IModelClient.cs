using BotAgent.Domain.Reply;
using BotAgent.Domain.Stickers;
using BotAgent.Domain.Conversation;

namespace BotAgent.Domain.Ports;

/// <summary>
/// 模型客户端端口（由 <c>Services/Agent/OpenAiClient</c> 实现，见 §6.4）。
///
/// 为什么要有它：回复链、表情包运营、画像巡检、音乐理解、`//` 任务、热更新都在用同一个模型客户端，
/// 而它们**只该知道"能问模型什么"**，不该知道它内部是 transport + prompt + parse 三段、
/// 也不该被绑死在具体实现上（探针要能塞一个"只会回固定 JSON"的假客户端 —— 见 SafetyProbe 的替身场景）。
///
/// ⚠ 这几个可写属性不是"配置"，而是**热更新推给模型客户端的旋钮**（面板保存后由 SettingsHotReload 写回）。
/// </summary>
public interface IModelClient
{
    /// <summary>本机登录的机器人 QQ 号（注入模型上下文，帮助理解 @ 与身份）。</summary>
    string? BotIdentity { get; set; }

    /// <summary>模型人设档案（可选，请求时注入系统上下文）。</summary>
    string? BotPersona { get; set; }

    /// <summary>AI 对话欲望（0-100）：越高越倾向主动参与群聊发言。</summary>
    int AiDesire { get; set; }

    /// <summary>发言适合度阈值（模型自评低于它就不发）。</summary>
    int SuitabilityThreshold { get; set; }

    /// <summary>带进上下文的最近消息条数上限。</summary>
    int MaxContextMessages { get; set; }

    /// <summary>聊天那一次请求的超时（面板/日志要如实显示）。</summary>
    TimeSpan ChatTimeout { get; }

    /// <summary>问一轮模型（回复链的主路径）：上下文 + 这一轮的提示词 → 结构化结果。</summary>
    Task<CompletionResult> CompleteAsync(
        IReadOnlyList<ChatMessage> context,
        string? profilesText = null,
        CancellationToken ct = default,
        IReadOnlyList<StickerChoice>? stickers = null,
        bool pokeContext = false,
        string? moodText = null,
        string? musicText = null,
        string? linkText = null,
        bool enableListen = false,
        bool enableVoice = false,
        string? recallText = null,
        bool enableWebSearch = false,
        string? searchText = null,
        string? groupRolesText = null,
        string? vibeHint = null,
        bool proactive = false,
    bool enableAsk = false,
    bool enableToolRequest = false,

    // 批次 D（让模型看见工具）：服务端**按策略裁剪过的**工具清单（纯文本，由 ToolPromptText 生成）。
    // 为什么从外面传进来而不是在客户端里现算：策略快照属于本轮（V3 §5.3），
    // 客户端只该"说了什么"，不该自己决定"哪些工具能用"。
    string? toolList = null);

    /// <summary>一次"问一句、要一段文字"的调用（服务器 agent 的工具循环用它）。</summary>
    Task<string?> CompleteChatAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> messages,
        int maxTokens,
        double temperature,
        CancellationToken ct = default,
        string? baseUrlOverride = null,
        string? apiKeyOverride = null);

    /// <summary>
    /// Agent 工具循环的可选推理强度。默认实现保持旧客户端兼容：不支持时等价于 auto。
    /// </summary>
    async Task<string?> CompleteChatWithReasoningAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> messages,
        int maxTokens,
        double temperature,
        string? reasoningEffort,
        CancellationToken ct = default,
        string? baseUrlOverride = null,
        string? apiKeyOverride = null)
        => await CompleteChatAsync(model, systemPrompt, messages, maxTokens, temperature, ct, baseUrlOverride, apiKeyOverride);

    /// <summary>按 URL 取图片原始字节（多模态识图/表情包用）。</summary>
    Task<(byte[] Data, string Mime, string Ext)?> DownloadImageAsync(string url, CancellationToken ct = default, long? messageId = null);

    /// <summary>表情包自巡检：把库交给模型过一遍，返回该删的 id 与理由。</summary>
    Task<(List<string> Delete, string? Reason)> CurateStickersAsync(string libraryTable, int maxDelete, CancellationToken ct = default);

    /// <summary>让模型看一眼这张图：说明 + 关键词 + "这是不是表情包"。</summary>
    Task<(string? Desc, List<string>? Tags, bool? IsSticker)> DescribeStickerAsync(byte[] image, string mime, CancellationToken ct = default);

    /// <summary>给会话综结一个标题（`//` 任务列表用）。</summary>
    Task<string?> SummarizeSessionTitleAsync(string digest, string? previousTitle, CancellationToken ct = default);

    /// <summary>把某人的发言综结成画像摘要。</summary>
    Task<string?> SummarizePersonaAsync(
        string name,
        string? existingSummary,
        IReadOnlyList<string> messages,
        int maxChars,
        CancellationToken ct = default);

    /// <summary>听一段音频，给出客观描述（听歌那条路用）。</summary>
    Task<string?> DescribeAudioAsync(byte[] audio, string format, string title, string? artist, CancellationToken ct);
}
