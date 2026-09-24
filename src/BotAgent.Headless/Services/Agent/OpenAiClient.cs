using System.Net;
using BotAgent.Domain.Stickers;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Reply;
using BotAgent.Services.Model;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Agent;

/// <summary>
/// Agent 大脑：OpenAI 兼容 Chat Completions 客户端。
/// 由用户自配 Base URL / API Key / 模型，可对接 OpenAI、DeepSeek、通义、本地 Ollama 等。
/// </summary>
public sealed class OpenAiClient : IModelClient
{
    /// <summary>辅助调用（画像摘要 / 表情包审核 / 听歌 / 综结标题…）用的出网：这些不该拖，60 秒拿不到就放弃。</summary>
    private readonly IHttpFetcher _auxHttp = null!;

    /// <summary>
    /// 聊天那次请求单独一个客户端，超时放宽（默认 120 秒，可用 <c>QQCHAT_MODEL_TIMEOUT_SECONDS</c> 覆盖）。
    /// 为什么：上游是“思考型”模型 + 多账号网关，实测一次 12~40 秒起步，长上下文/带图更久；
    /// 60 秒太紧 —— 群里出现过 <c>TaskCanceledException: 60 秒超时</c> 把一整轮回复丢掉（号主 11:32 截的图）。
    /// 但也不能无限等（群里干等几分钟也是一种坏体验），所以配一次“重试 + 30 秒封顶”：
    /// 第一次拿到就是拿到，真卡住了第二次 30 秒内给个结果（成功就用它，不成就丢掉这一轮）。
    /// 辅助调用继续用 60 秒那个，免得网关抽风时把画像/审核也拖几分钟。
    /// </summary>
    private readonly IHttpFetcher _chatHttp = null!;

    /// <summary>超时重试用的小预算（秒）：第一次已经等过一大截了，第二次不该再等满。</summary>
    private const int TimeoutRetrySeconds = 30;

    /// <summary>聊天超时（秒）：默认 120，环境变量可覆盖（测试用它把超时调短）。
    /// 装配点用同一个口径造聊天那条出网客户端（见 <c>CompositionRoot</c>）。</summary>
    public static TimeSpan ModelTimeout()
    {
        var raw = Environment.GetEnvironmentVariable("QQCHAT_MODEL_TIMEOUT_SECONDS");
        var seconds = double.TryParse(raw, out var parsed) && parsed > 0 ? parsed : 120;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 5, 1800));
    }

    // 每个客户端一个下载器实例（里面有缓存）：它要拿到“重新签发图片地址”的回调，不能做成静态的
    private readonly IImageDownloader _imageDownloader;

    /// <summary>
    /// 图片地址过期（QQ 的 rkey 时效）时，让协议端重新签发地址的回调（由 BotAgentHost 接到 IQqChatSource 上）。
    /// 不接也能跑，只是过期图这轮看不到。
    /// </summary>
    public Func<long, CancellationToken, Task<IReadOnlyList<string>>>? RefreshImageUrls
    {
        get => _imageDownloader.RefreshUrls;
        set => _imageDownloader.RefreshUrls = value;
    }

    /// <summary>图片缓存命中数 / “过期后重新签发取回”次数（测试与排障用）。</summary>
    public int ImageCacheHits => _imageDownloader.CacheHits;
    public int ImageRefreshedCount => _imageDownloader.RefreshedCount;

    /// <summary>配置读取入口（当前发布版，见 <see cref="SettingsBox" />）：模型地址 / 密钥 / 模型名都是每轮现读的。</summary>
    private AppSettings _settings => _box.Current;

    private readonly SettingsBox _box;

    public OpenAiClient(
        SettingsBox box, IHttpFetcher auxHttp, IHttpFetcher chatHttp, IImageDownloader images, IModelTransport transport)
    {
        _box = box;
        _auxHttp = auxHttp;
        _chatHttp = chatHttp;
        _imageDownloader = images;
        ChatTimeout = chatHttp.Timeout;
        _transport = transport;
    }

    /// <summary>出网那一层（请求体组装 / 发送 / 三条兜底重试），见 <see cref="ModelTransport" />。</summary>
    private readonly IModelTransport _transport;

    /// <summary>聊天那次请求的超时（面板/日志要如实显示；值来自装配点那个出网客户端）。</summary>
    public TimeSpan ChatTimeout { get; }

    /// <summary>本机登录的机器人 QQ 号（注入模型上下文，帮助理解 @ 与身份）。</summary>
    public string? BotIdentity { get; set; }

    /// <summary>模型人设档案（可选，请求时注入系统上下文）。</summary>
    public string? BotPersona { get; set; }

    /// <summary>AI 对话欲望（0-100）：越高越倾向主动参与群聊发言。</summary>
    public int AiDesire { get; set; } = 50;

    /// <summary>发言适合度阈值（0-100）：模型评出低于此值则不发言。**代码侧强制执行**。</summary>
    public int SuitabilityThreshold { get; set; } = 10;

    /// <summary>给模型的最大上下文消息条数（与 BotAgentHost 侧共用同一值，避免两头不一样）。</summary>
    public int MaxContextMessages { get; set; } = 200;

    /// <summary>
    /// 生成回复。context 为按时间正序的最近消息（角色已映射为 system/user/assistant）。
    /// 返回结构化结果：调用方据此判断“沉默”还是“发言”（含模型自评的适合度）。
    /// 网络/接口异常向上抛，由调用方记日志。
    /// </summary>
    /// <summary>
    /// 同会话两条语音的最小间隔（秒）——**跟着面板的「语音积极性」缩放**。
    /// 为什么要跟着动：写死 45 秒时，“积极性拉到 100”其实一点也积极不起来（该发还是被拦）。
    /// 提示词里告诉模型的数字与这里拦住它的数字**是同一个函数**算出来的，不会出现“面板说 100、实际卡 45 秒”的错位。
    /// </summary>
    internal static int VoiceIntervalSeconds(int eagerness) => Math.Clamp(eagerness, 0, 100) switch
    {
        <= 20 => 180,
        <= 40 => 90,
        <= 60 => 45,
        <= 80 => 25,
        _ => 15
    };

    public async Task<CompletionResult> CompleteAsync(IReadOnlyList<ChatMessage> context, string? profilesText = null, CancellationToken ct = default,
        IReadOnlyList<StickerChoice>? stickers = null, bool pokeContext = false, string? moodText = null, string? musicText = null, string? linkText = null, bool enableListen = false, bool enableVoice = false, string? recallText = null, bool enableWebSearch = false, string? searchText = null, string? groupRolesText = null, string? vibeHint = null, bool proactive = false,
        bool enableAsk = false, bool enableToolRequest = false, string? toolList = null)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            throw new InvalidOperationException("未配置 API Key");
        }

        if (_settings.ApiKey.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            _settings.ApiKey.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("API Key 填成了 URL，请填入密钥 token（设置页「Agent 大脑」）");
        }

        // 统一在此处截断一次（以前 BotAgentHost 和本方法各截一次，重复且容易不一致）
        var window = context.Count > MaxContextMessages
            ? context.Skip(context.Count - MaxContextMessages).ToArray()
            : context;

        // 可供模型指认的“最近几条别人的消息”：给它们附上 (#QQ消息id)，模型在 JSON 里用 replyTo 指明它在回哪一条。
        // 为什么要这么做：光靠启发式（“最新那条”或“排队时的触发”）猜不准 ——
        // 线上就出现过“正文在接一个哏，引用却挂在另一个人那句上”。模型自己知道回哪句，让它说出来。只标最近 16 条。
        // 撤回的消息不在此列：引用一条群里已经看不到的消息，群友看到的就是莫名其妙。
        var quotableIds = new HashSet<long>(
            window.Where(m => m.Role == MessageRole.Peer && !m.Recalled && m.QqMessageId is > 0 &&
                              !MessageMarkers.IsAside(m.Text))   // 整条旁白（“（笑）”）不是一句话，不给编号
                  .TakeLast(16)
                  .Select(m => m.QqMessageId!.Value));

        // 系统提示词整块在 PromptBuilder（纯字符串拼装，顺序即语义）；这里只负责"把素材交出去"。
        // 用具名实参：这条调用有 25 个参数，位置写法一旦排错就会把"开关"喂成"阈值"（编译过的错才会被抓到）。
        var systemContent = PromptBuilder.Build(new PromptBuilder.PromptRequest(
            SystemPrompt: SystemPrompt,
            BotIdentity: BotIdentity,
            Persona: BotPersona,
            AiDesire: AiDesire,
            Window: window,
            QuotableIds: quotableIds,
            ProfilesText: profilesText,
            Stickers: stickers,
            PokeContext: pokeContext,
            Proactive: proactive,
            MoodText: moodText,
            MusicText: musicText,
            LinkText: linkText,
            RecallText: recallText,
            GroupRolesText: groupRolesText,
            VibeHint: vibeHint,
            SearchText: searchText,
            SuitabilityThreshold: SuitabilityThreshold,
            EnableListen: enableListen,
            EnableVoice: enableVoice,
            VoiceMaxChars: Math.Clamp(_settings.VoiceMaxChars, 10, 300),
            VoiceEagerness: Math.Clamp(_settings.VoiceEagerness, 0, 100),
            EnableWebSearch: enableWebSearch,
            EnableAsk: enableAsk,
            EnableToolRequest: enableToolRequest,
            ToolList: toolList));
        // 请求体（含多模态图片）与发送都在 ModelTransport：这里只管"说什么"与"回来的怎么判"
        var built = await _transport.BuildAsync(window, systemContent, quotableIds, ct);

        // 上游偶尔会回 200 但**没有 choices**。实测三种情形：
        //   a) 慢的 `-high` 模型“思考”把预算吃光（17:55-18:01 连报 21 次）
        //   b) 网关抖动（回 200 但内容空）
        //   c) 带图那一轮被上游风控吞掉（22:09 这条：usage 里 prompt 12744 / completion 0，前面刚取回 4 张图）
        // 以前直接 `choices[0]` → IndexOutOfRangeException，被记成“模型请求失败”：一条消息就这么没了。
        // 之后改成“按沉默处理”，但**一条都不重试**：明明多半是上游吞了，却白丢一轮回复。
        // 现在由 ModelTransport 分两步兜：① 空 choices 重试一次；② 带图两轮都空 → 去掉图片再试一次。
        var (json, textOnlyRetry) = await _transport.SendAsync(built.Payload, built.AttachedImages, built.AttachedImageIds, ct);

        using var doc = JsonDocument.Parse(json);

        if (!ModelJson.HasChoices(json))
        {
            Services.FileLog.Write("Agent",
                $"上游连续两次都没给 choices（带图 {built.AttachedImages} 张）→ 本轮按沉默处理：{Truncate(json, 200)}");
            return new CompletionResult(null, null, null, ReasonCode: "upstream_empty", Malformed: true, UpstreamEmpty: true);
        }

        if (textOnlyRetry)
        {
            Services.FileLog.Write("Agent", "本轮回复是去掉图片后拿到的（那张图已被拉黑）");
        }
        var choices = doc.RootElement.GetProperty("choices");

        if (choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var contentNode))
        {
            var rawReply = contentNode.ValueKind switch
            {
                JsonValueKind.String => contentNode.GetString(),
                JsonValueKind.Array => string.Concat(contentNode.EnumerateArray()
                    .Select(s => s.TryGetProperty("text", out var t) ? t.GetString() : null)),
                _ => null
            };

            // 判定用的开关取**调用方传进来的快照**，不再读实时设置 —— 否则配置热更新会改到在途请求（V3 §5.3）
            var parsed = ModelOutputParser.Parse(rawReply, questionsEnabled: enableAsk);
            LogParseNotices(parsed.Notices);
            return parsed.Result;
        }

        // 有 choices 但里面没有 message.content：同样按沉默处理，不招异常
        Services.FileLog.Write("Agent", $"模型返回里没有 message.content → 本轮按沉默处理：{Truncate(json, 200)}");
        return new CompletionResult(null, null, null, ReasonCode: "upstream_empty", Malformed: true, UpstreamEmpty: true);
    }
    private readonly List<long> _imageFilterSuspectOrder = new();

    /// <summary>给表情包库用：下载图片原始字节（内部走同一套 SSRF 防护与大小限制）。</summary>
    public Task<(byte[] Data, string Mime, string Ext)?> DownloadImageAsync(string url, CancellationToken ct = default, long? messageId = null)
        => _imageDownloader.DownloadBytesAsync(url, ct, messageId);

    /// <summary>
    /// 表情包自巡检：让模型看一遍库（表格形式），自己决定删哪些。
    /// 返回要删的 id 与理由；解析不了就返回空（宁可什么都不删）。
    /// </summary>
    /// <summary>
    /// 通用一次性补全：调用方自己给 system + 多轮 messages，直接拿原始文本。
    /// 为什么不走 <see cref="CompleteAsync"/>：那套是“群里该怎么回”的人设 JSON 约定，
    /// 而 agent（服务器内置 / 工具循环）要的是自由格式 + 自己控制历史。
    /// 429/5xx 退让 2 秒重试一次（跟主流程同口径：上游“No capacity”是常态）。
    /// </summary>
    public async Task<string?> CompleteChatAsync(
        string model,
        string systemPrompt,
        IReadOnlyList<(string Role, string Text)> messages,
        int maxTokens,
        double temperature,
        CancellationToken ct = default,
        string? baseUrlOverride = null,
        string? apiKeyOverride = null)
    {
        var apiKey = string.IsNullOrWhiteSpace(apiKeyOverride) ? _settings.ApiKey?.Trim() : apiKeyOverride!.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return null;
        }

        var url = string.IsNullOrWhiteSpace(baseUrlOverride) ? BuildUrl() : BuildUrl(baseUrlOverride!);
        var payload = new JsonObject
        {
            ["model"] = string.IsNullOrWhiteSpace(model) ? _settings.Model : model,
            ["max_tokens"] = Math.Clamp(maxTokens, 64, 32000),
            ["temperature"] = temperature
        };

        var array = new JsonArray { new JsonObject { ["role"] = "system", ["content"] = systemPrompt } };
        foreach (var (role, text) in messages)
        {
            array.Add(new JsonObject { ["role"] = role, ["content"] = text });
        }

        payload["messages"] = array;

        for (var attempt = 0; ; attempt++)
        {
            HttpResponseMessage response;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, url)
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                response = await _auxHttp.SendAsync(request, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                FileLog.Warn("Agent", $"补全请求失败：{ex.Message}");
                return null;
            }

            using (response)
            {
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    using var doc = JsonDocument.Parse(body);
                    if (!doc.RootElement.TryGetProperty("choices", out var choices) ||
                        choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
                    {
                        FileLog.Warn("Agent", $"补全返回空结果：{Truncate(body, 160)}");
                        return null;
                    }

                    var node = choices[0].TryGetProperty("message", out var msg) &&
                               msg.TryGetProperty("content", out var content)
                        ? content
                        : default;
                    return node.ValueKind switch
                    {
                        JsonValueKind.String => node.GetString(),
                        JsonValueKind.Array => string.Concat(node.EnumerateArray()
                            .Select(s => s.TryGetProperty("text", out var t) ? t.GetString() : null)),
                        _ => null
                    };
                }

                var status = (int)response.StatusCode;
                if (attempt >= 1 || (status < 500 && status != 429))
                {
                    FileLog.Warn("Agent", $"补全请求失败 {status}：{Truncate(body, 120)}");
                    return null;
                }

                FileLog.Warn("Agent", $"补全返回 {status} → 2 秒后重试一次");
            }

            await Clock.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
    }

    public async Task<(List<string> Delete, string? Reason)> CurateStickersAsync(string libraryTable, int maxDelete, CancellationToken ct = default)
    {
        var empty = (new List<string>(), (string?)null);
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || string.IsNullOrWhiteSpace(libraryTable) || maxDelete <= 0)
        {
            return empty;
        }

        await _stickerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var system =
                "你在帮一个 QQ 群聊机器人整理表情包库。下面是库里的图，每行：id | 说明（含关键词）| 用过几次 | 多久前加入。\n" +
                "挑出不值得留的：与群聊语境无关的（广告、聊天截图、二维码、纯文字通知、屏幕截图）、画质/内容不合适（低俗、恶心、涉政涉黄）、" +
                "说明模糊且从未用过的、和已有的重复表达。标了“24h 内用过”的不要动。\n" +
                $"最多删 {maxDelete} 张（也可以一张都不删）。只输出一行 JSON：" +
                "{\"delete\": [\"id1\", \"id2\"], \"reason\": \"一句话说清为什么删这些\"}。" +
                "没把握就少删：库里图少的时候宁可留着。";

            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = system },
                    new JsonObject { ["role"] = "user", ["content"] = libraryTable }
                },
                ["max_tokens"] = 300,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await _auxHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return empty;
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ParseCuration(raw, maxDelete);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包巡检请求失败：{ex.Message}");
            return empty;
        }
        finally
        {
            _stickerGate.Release();
        }
    }

    /// <summary>解析巡检结果（只收合法 id，并强制不得超过上限）。</summary>
    internal static (List<string> Delete, string? Reason) ParseCuration(string? raw, int maxDelete)
    {
        var delete = new List<string>();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (delete, null);
        }

        var text = ModelOutputText.StripCodeFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            return (delete, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                ? r.GetString()?.Trim()
                : null;

            foreach (var name in new[] { "delete", "deletes", "remove", "removes" })
            {
                if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var item in arr.EnumerateArray())
                {
                    var id = (item.ValueKind == JsonValueKind.String ? item.GetString() : null)?.Trim().TrimStart('#').Trim();
                    if (!string.IsNullOrWhiteSpace(id) && id.Length is >= 4 and <= 32 && id.All(char.IsAsciiHexDigit)
                        && !delete.Contains(id.ToLowerInvariant()))
                    {
                        delete.Add(id.ToLowerInvariant());
                    }
                }

                break;
            }

            if (delete.Count > maxDelete)
            {
                delete = delete.Take(maxDelete).ToList();
            }

            return (delete, reason);
        }
        catch (JsonException)
        {
            return (delete, null);
        }
    }

    private readonly SemaphoreSlim _stickerGate = new(1, 1);

    /// <summary>
    /// 让模型看一张图并给出“一句话说明 + 情绪/场景关键词 + 这是不是表情包”。
    /// 表情包能不能“按语境发”，完全取决于这一步：靠关键词才能检索；
    /// 而“是不是表情包”这一步是入库闸门 —— 群友发的聊天截图/广告不能被当成表情包收下来。
    /// 走独立闸门，不和聊天抢并发。
    /// </summary>
    public async Task<(string? Desc, List<string>? Tags, bool? IsSticker)> DescribeStickerAsync(byte[] image, string mime, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || image.Length == 0)
        {
            return (null, null, null);
        }

        await _stickerGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var dataUrl = $"data:{mime};base64,{Convert.ToBase64String(image)}";
            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["role"] = "system",
                        ["content"] =
                            "你在给一个 QQ 群聊机器人的表情包库做入库审核。看图片内容，只输出一行 JSON：" +
                            "{\"sticker\": true/false, \"desc\": \"一句话说明这张图的画面与用途，20 字以内\", \"tags\": [\"3-6 个情绪或使用场景关键词\"]}。\n" +
                            "sticker 只在它真是“可以用来说话的表情包/梗图”（人或角色+情绪、能当反应那张图用）时为 true；" +
                            "聊天截图、屏幕截图、纯文字图、广告、二维码、文档照片、随手拍的实物 —— 这些一律 false（会被丢掉）。\n" +
                            "关键词要能用在其他句子里检索到它（例如：大笑、无语、嘲讽、点赞、崩溃、狗头、摸鱼）。" +
                            "如果是纯文字图，把文字内容也写进 desc。不要输出 JSON 之外的内容。"
                    },
                    new JsonObject
                    {
                        ["role"] = "user",
                        ["content"] = new JsonArray
                        {
                            new JsonObject { ["type"] = "text", ["text"] = "这张图是什么？" },
                            new JsonObject { ["type"] = "image_url", ["image_url"] = new JsonObject { ["url"] = dataUrl } }
                        }
                    }
                },
                ["max_tokens"] = 200,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await _auxHttp.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return (null, null, null);
            }

            var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var raw = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            return ParseStickerDescription(raw);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            FileLog.Warn("Sticker", $"表情包识别失败：{ex.Message}");
            return (null, null, null);
        }
        finally
        {
            _stickerGate.Release();
        }
    }

    /// <summary>解析表情包编目结果（容忍代码块围栏与多余文字）。</summary>
    internal static (string? Desc, List<string>? Tags, bool? IsSticker) ParseStickerDescription(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return (null, null, null);
        }

        var text = ModelOutputText.StripCodeFence(raw.Trim());
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start)
        {
            var plain = text.Trim();
            return plain.Length is > 0 and <= 40 ? (plain, null, null) : (null, null, null);
        }

        try
        {
            using var doc = JsonDocument.Parse(text[start..(end + 1)]);
            var root = doc.RootElement;
            var desc = root.TryGetProperty("desc", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()?.Trim() : null;
            List<string>? tags = null;
            if (root.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                tags = t.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!.Trim())
                    .Where(x => x.Length > 0)
                    .Take(8)
                    .ToList();
            }

            // 缺字段时不默认 true：宁可多留一张待定，也不要让截图混进来
            bool? isSticker = null;
            if (root.TryGetProperty("sticker", out var s))
            {
                isSticker = s.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String when bool.TryParse(s.GetString(), out var b) => b,
                    _ => null
                };
            }

            return (desc, tags, isSticker);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// 给一个 agent 会话综结标题（每轮跑完调一次；失败就返回 null —— 起名不能影响任务本身）。
    /// 为什么要模型综结：用“第一句指令”当标题时，一个会话跑了十几轮之后标题还是那句开场白；
    /// 号主要的是“根据上下文综结出这个会话在干什么”。提示词里带 `[会话标题]` 标记，
    /// 测试的假上游靠它识别这类请求（不会把脚本回复吃掉）。
    /// </summary>
    public async Task<string?> SummarizeSessionTitleAsync(string digest, string? previousTitle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(digest) || string.IsNullOrWhiteSpace(_settings.ApiKey))
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder();
            sb.Append("[会话标题] 你在给一段「主人（通过 QQ 使唤）× 编程 agent」的会话起标题。\n");
            sb.Append("标题要从这段会话的**整体内容**综结出来：它在干什么活、干成了什么 —— 主人看一眼就知道“哦是这个会话”。\n");
            sb.Append("要求：中文（除非全篇是英文术语）；8~16 个字；名词性短语；不要句号、不要引号、不要“会话/任务”这类废话前缀；\n");
            sb.Append("多轮时综结主线，别只照抄最新那一句命令。\n");
            sb.Append("只输出一行 JSON：{\"title\":\"...\"}。\n");
            if (!string.IsNullOrWhiteSpace(previousTitle))
            {
                sb.Append("\n[现有标题]（内容没大变就沿用，别为改而改）\n").Append(previousTitle.Trim()).Append('\n');
            }

            var payload = new JsonObject
            {
                ["model"] = _settings.Model,
                ["messages"] = new JsonArray
                {
                    new JsonObject { ["role"] = "system", ["content"] = sb.ToString() },
                    new JsonObject { ["role"] = "user", ["content"] = "[会话脉]\n" + digest.Trim() }
                },
                ["max_tokens"] = 96,
                ["temperature"] = 0.2
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

            using var response = await _auxHttp.SendAsync(request, ct);
            var json = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode || !ModelJson.HasChoices(json))
            {
                Services.FileLog.Warn("Agent", $"[会话标题] 没拿到标题（HTTP {(int)response.StatusCode}），本次不改名");
                return null;
            }

            using var doc = JsonDocument.Parse(json);
            var contentNode = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
            var raw = contentNode.ValueKind == JsonValueKind.Array
                ? string.Concat(contentNode.EnumerateArray().Select(s => s.TryGetProperty("text", out var t) ? t.GetString() : null))
                : contentNode.GetString();
            if (string.IsNullOrWhiteSpace(raw))
            {
                return null;
            }

            var text = ModelOutputText.StripCodeFence(raw.Trim());
            var start = text.IndexOf('{');
            var end = text.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                // 看着像 JSON 就只认 title 字段：万一模型没按格式回（或者假上游给了别的 JSON），
                // 宁可不起名，也不要把一整条 JSON 当标题挂上去（实测就发生过，标题变成 {…}）。
                using var parsed = JsonDocument.Parse(text[start..(end + 1)]);
                if (parsed.RootElement.TryGetProperty("title", out var titleNode) && titleNode.ValueKind == JsonValueKind.String)
                {
                    text = titleNode.GetString() ?? string.Empty;
                }
                else
                {
                    Services.FileLog.Warn("Agent", $"[会话标题] 模型没给 title 字段，本次不改名：{Truncate(raw, 80)}");
                    return null;
                }
            }

            var title = text.Replace('\n', ' ').Replace('\r', ' ').Trim().Trim('"', '“', '”', '\'');
            if (title.Length > 24)
            {
                title = title[..24];
            }

            return string.IsNullOrWhiteSpace(title) ? null : title;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Services.FileLog.Warn("Agent", $"[会话标题] 综结失败（不影响任务）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 把一堆历史发言压缩成一段人物画像（长期记忆）。
    /// 与聊天请求共用同一个模型与端点，但走独立的并发闸门。
    /// </summary>
    public async Task<string?> SummarizePersonaAsync(
        string name,
        string? existingSummary,
        IReadOnlyList<string> messages,
        int maxChars,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_settings.ApiKey) || messages.Count == 0)
        {
            return null;
        }

        var sb = new StringBuilder();
        sb.Append("你在为一个 QQ 群聊机器人维护“人物档案”（长期记忆）。\n");
        sb.Append("把下面这些发言压缩成一段人物画像，供机器人以后认人用。\n");
        sb.Append("画像需覆盖：身份/职业线索、性格与说话风格、常聊的话题、与其他群友的关系、值得记住的事实。\n");
        sb.Append("要求：用第三人称陈述句；只保留稳定、有信息量的内容，忽略寒暄与一次性琐事；\n");
        sb.Append($"不要逐条罗列，不要分点，不要客服腔；总长不超过 {maxChars} 字；只输出画像正文。\n");

        if (!string.IsNullOrWhiteSpace(existingSummary))
        {
            sb.Append("\n[已有画像]（在此基础上融合新信息，不要推翻已证实的稳定事实）\n");
            sb.Append(existingSummary.Trim()).Append('\n');
        }

        sb.Append($"\n[新的发言]（{name}）\n");
        foreach (var m in messages)
        {
            sb.Append("- ").Append(m).Append('\n');
        }

        var payload = new JsonObject
        {
            ["model"] = _settings.Model,
            ["messages"] = new JsonArray
            {
                new JsonObject { ["role"] = "system", ["content"] = sb.ToString() },
                new JsonObject { ["role"] = "user", ["content"] = $"请输出 {name} 的人物画像。" }
            },
            ["max_tokens"] = Math.Clamp(maxChars * 3, 200, 2000),
            ["temperature"] = 0.3
        };

        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        using var response = await _auxHttp.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"画像摘要返回 {(int)response.StatusCode}：{Truncate(detail, 200)}");
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var contentNode = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content");
        var text = contentNode.ValueKind == JsonValueKind.Array
            ? string.Concat(contentNode.EnumerateArray().Select(s => s.GetProperty("text").GetString()))
            : contentNode.GetString();

        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var trimmed = text.Trim().Trim('"', '“', '”');
        return SafeTruncate(trimmed, maxChars);
    }

    /// <summary>
    /// 按字符数截断，但绝不切开代理对（emoji 占两个 char）。
    /// 直接 `s[..max]` 会把 emoji 切一半 → 输出乱码。
    /// </summary>
    private static string SafeTruncate(string text, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (text.Length <= maxChars)
        {
            return text;
        }

        var cut = maxChars;
        if (char.IsHighSurrogate(text[cut - 1]))
        {
            cut--; // 回退一格，不要切开代理对
        }

        return text[..cut];
    }

    /// <summary>
    /// 让模型“亲耳听”：把低码率音频（截前 N 秒 / N KB）交给**独立的音频识别模型**，
    /// 让它客观描述听到的内容（曲风/编配/人声/情绪/节奏感）。
    ///
    /// 为什么要单独配一个模型：很多网关/中转会把音频静默丢掉，主模型根本收不到声音，
    /// 它只能看到文字，于是“听歌”就只剩手写 DSP 的客观数字（听不出曲风情绪）。
    /// 换一个确实支持音频输入、而且网关愿意转发的模型就能听到（可以用一段 440Hz 蜂鸣自查）。
    ///
    /// 返回 null 表示“这次没听到”（未配置/模型不支持/请求失败）—— 调用方降级回只给 DSP 实测数据。
    /// </summary>
    public async Task<string?> DescribeAudioAsync(byte[] audio, string format, string title, string? artist, CancellationToken ct)
    {
        var model = _settings.MusicUnderstandModel?.Trim();
        if (string.IsNullOrWhiteSpace(model) || !_settings.MusicSendAudioToModel || audio.Length == 0)
        {
            return null;
        }

        var capKb = Math.Clamp(_settings.MusicAudioToModelMaxKb, 128, 4096);
        var bytes = audio.Length > capKb * 1024 ? audio[..(capKb * 1024)] : audio;

        var text = "这是一首歌的片段（低码率、可能被截断）。" +
            $"歌名：{title}" + (string.IsNullOrWhiteSpace(artist) ? "。" : $"，歌手：{artist}。") +
            "请只说你**听到的**：曲风、编配与主要乐器、人声特点（音色/唱法）、情绪与氛围、节奏快慢与律动、" +
            "如果能听清歌词就引用一两句。听不清/听不到就直接说听不到，不要根据歌名猜、不要编造。用 3-5 句话。";

        var messages = new JsonArray
        {
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = new JsonArray
                {
                    new JsonObject { ["type"] = "text", ["text"] = text },
                    new JsonObject
                    {
                        ["type"] = "input_audio",
                        ["input_audio"] = new JsonObject
                        {
                            ["data"] = Convert.ToBase64String(bytes),
                            ["format"] = format
                        }
                    }
                }
            }
        };

        var payload = new JsonObject
        {
            ["model"] = model,
            ["messages"] = messages,
            ["max_tokens"] = 512,
            ["temperature"] = 0.3
        };

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var response = await _auxHttp.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                Services.FileLog.Warn("Agent", $"[Music] 音频识别模型 {model} 返回 {(int)response.StatusCode}：{Truncate(body, 160)}");
                return null;
            }

            using var doc = JsonDocument.Parse(body);
            var content = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            // 模型说它听不到（模型/链路不支持音频）→ 当作“没听到”，别把这句话当听感塞进上下文
            if (content.Contains("听不到", StringComparison.Ordinal) || content.Contains("无法接收", StringComparison.Ordinal) ||
                content.Contains("无法播放", StringComparison.Ordinal) || content.Contains("没有声音", StringComparison.Ordinal))
            {
                Services.FileLog.Warn("Agent", $"[Music] 音频识别模型 {model} 声称听不到音频（该模型/链路可能不支持 input_audio）");
                return null;
            }

            Services.FileLog.Write("Agent", $"[Music] {model} 听感：{Truncate(content, 200)}");
            return content;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Services.FileLog.Warn("Agent", $"[Music] 音频识别请求失败（{model}）: {ex.Message}");
            return null;
        }
    }

    private string BuildUrl() => BuildUrl(_settings.ModelBaseUrl);

    /// <summary>拼 /chat/completions 地址（允许指定地址 —— 服务器 agent 可以走自己的接口）。</summary>
    private static string BuildUrl(string baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    private const string SystemPrompt =
        "你是运行在桌面 QQ 客户端里的聊天机器人「Bot Agent」。\n" +
        "回复规则：\n" +
        "- 私聊与群聊中，先根据上下文判断当前对话是否与你相关（是否在对你说话、询问你、需要你参与）；" +
        "相关则自然简洁地回复；与你无关（例如群友之间与你无关的闲聊）则只输出空内容，不要回复。\n" +
        "- 不使用任何固定关键词作为回复条件，一切凭上下文理解。\n" +
        "- 上下文中每条对方消息形如 {发送者}{内容--时间}，发送者可能是真人、群成员或角色。\n" +
        "- 若上下文中出现角色卡片（描述某角色的名字、性格、背景、说话风格的设定），" +
        "且对话正在与该角色互动，请以该角色卡片中的身份、性格与说话风格进行思考与回复。\n" +
        "- 像真人群友一样说话：口语化、简短、有来有回，多用群聊常见的语气与口头禅，" +
        "不要像客服/助手一样客套，不要用“首先其次最后”，不要过分礼貌，可以带点调侃。\n" +
        "- 回复长度按场景动态把握：轻松闲聊一句 15 字以内；认真讨论/专业问题 30 字左右；" +
        "需要详细说明时也不要超过 60 字，宁可分几次说。\n" +
        "- 使用中文。";

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// 把解析器“要说给日志听的话”按原来的措辞写出来（<see cref="ModelOutputParser" /> 是纯函数，不自己写日志）。
    /// ⚠ 这几行是**对外契约**（号主按它们排障），搬家时逐字保留；只记形状与长度，不记正文（V3 §8.2）。
    /// </summary>
    private static void LogParseNotices(IReadOnlyList<ParseNotice> notices)
    {
        foreach (var notice in notices)
        {
            switch (notice.Kind)
            {
                case ParseNoticeKind.SchemaMismatch:
                    FileLog.Warn("Agent",
                        $"模型输出看似 JSON 但格式不对，按沉默处理（避免把代码发进群）；长度 {notice.Length}，只记形状不记正文");
                    break;

                case ParseNoticeKind.TruncatedOutput:
                    FileLog.Warn("Agent",
                        $"模型输出疑似被截断（非 JSON，只有 {notice.Length} 个可见字符），按沉默处理（只记形状不记正文）");
                    break;

                case ParseNoticeKind.SingleCharNoise:
                    FileLog.Warn("Agent",
                        $"模型只回了 1 个字“{notice.ReplyChar}”，不属于正常应答，按沉默处理。原文：{notice.Preview}");
                    break;

                case ParseNoticeKind.SingleCharReply:
                    FileLog.Write("Agent", $"模型回复只有 1 个字（{notice.ReplyChar}），原文：{notice.Preview}");
                    break;

                case ParseNoticeKind.SilentMarkerInBody:
                    FileLog.Warn("Agent", "模型正文里夹带了静默标记（按普通正文发送，不当控制位）");
                    break;

                case ParseNoticeKind.VerdictRejected:
                    FileLog.Warn("Agent",
                        $"模型输出未通过判定（{notice.ReasonCode}）→ 按沉默处理；长度 {notice.Length}，只记形状不记正文");
                    break;
            }
        }
    }
    /// <summary>撤回标记：内容是保留的，但必须让模型一眼看出“这条已经收回去了”。</summary>
    private const string RecallMark = "[已撤回] ";
}
