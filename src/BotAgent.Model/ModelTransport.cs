using System.Net;
using BotAgent.Model.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Model;

namespace BotAgent.Model;

/// <summary>
/// 模型出网的**传输层**（批次 6 从 <c>OpenAiClient.CompleteAsync</c> 搬出来）：
/// 把"这一轮要送什么"组装成请求体（含多模态图片）、发出去、以及上游抽风时的三条兜底重试。
///
/// 为什么单独一层（§13.4）：这些全是"怎么把消息送出去"的工程细节 —— 超时、退让、重试、图片下载与风控拉黑；
/// 而"该说什么"在 <c>PromptBuilder</c>、"说了什么"在 `Domain.Reply.ModelOutputParser`。三段分开之后，
/// 想改重试策略不必再读提示词，想改提示词也不必担心碰坏重试。
///
/// 四条不变量（都是线上踩出来的，别在重构里松掉）：
///   ① **5xx / 429 / 超时各重试一次**，且第二次只给 <c>TimeoutRetrySeconds</c> 秒（第一次已经等过一大截）；
///   ② **空 choices 重试一次**（上游网关抖动的典型表现），两次都空才算"这轮不说话"；
///   ③ **带图两轮都空 → 去掉图片再试一次**，同时把嫌疑图片的消息 id 拉黑（下次不再送，自愈）；
///   ④ **上下文以自己发言结尾时补一条系统口吻的 user 轮**（上游不接受 model-turn 结尾）。
/// </summary>
public class ModelTransport : IModelTransport
{
    /// <summary>超时重试用的小预算（秒）：第一次已经等过一大截了，第二次不该再等满。</summary>
    private const int TimeoutRetrySeconds = 30;

    /// <summary>撤回标记：内容是保留的，但必须让模型一眼看出“这条已经收回去了”。</summary>
    private const string RecallMark = "[已撤回] ";

    private readonly Func<ModelOptions> _options;
    private readonly IModelHttpFetcher _chatHttp;
    private readonly IModelHttpFetcher _auxHttp;
    private readonly ImageDownloader _imageDownloader;
    private readonly TimeSpan _chatTimeout;

    public ModelTransport(Func<ModelOptions> options, IModelHttpFetcher chatHttp, IModelHttpFetcher auxHttp, ImageDownloader imageDownloader, TimeSpan chatTimeout)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _chatHttp = chatHttp;
        _auxHttp = auxHttp;
        _imageDownloader = imageDownloader;
        _chatTimeout = chatTimeout;
    }

    /// <summary>配置读取入口（强类型切片）。</summary>
    private ModelOptions _settings => _options();

    /// <summary>拼 /chat/completions 地址（静态：客户端里其它走同一接口的方法也用它）。</summary>
    public static string BuildUrl(string baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    private string Url() => BuildUrl(_settings.BaseUrl);

    /// <summary>
    /// 把上下文窗口 + 系统提示词组装成请求体。返回的 <c>AttachedImages</c> / <c>AttachedImageIds</c>
    /// 供调用方写诊断日志与拉黑嫌疑图片（上游吞回复时这两个数是关键线索）。
    /// </summary>
    public async Task<BuiltRequest> BuildAsync(
        IReadOnlyList<ChatMessage> window, string systemContent, IReadOnlyCollection<long> quotableIds, CancellationToken ct,
        Domain.Reply.SamplingProfile? sampling = null)
    {
        var messages = new JsonArray();
        messages.Add(new JsonObject { ["role"] = "system", ["content"] = systemContent });
        // 最近上下文里出现图片时，在系统提示中提示模型可以看图
        if (window.Any(m => m.ImageUrls is { Count: > 0 }))
        {
            systemContent += "\n(消息中包含图片，已一并提供，请先看图再结合上下文回复。)";
            messages[^1] = new JsonObject { ["role"] = "system", ["content"] = systemContent };
        }

        // 这一轮真送出去的图片张数：上游吞回复时把它写进日志（带图那一轮的风控嫌疑最大）
        var attachedImages = 0;
        // 送去过的图片所属消息 id（两个用途：诊断日志里说清是哪条；疑似触发过滤时拉黑不再送）
        var attachedImageIds = new List<long>();

        foreach (var msg in window) // 上下文窗口
        {
            if (msg.Role == MessageRole.System)
            {
                continue; // 本地系统提示（如"已连接"）不发给模型，避免噪音
            }

            // 对方消息按 {发送者}{内容--时间} 组织，帮助模型分辨谁在何时说了什么；
            // 最近几条再附上 (#id)，供 replyTo 引用。
            // 已撤回的：正文前面加 [已撤回] 标记（**内容保留** —— 它确实看过，只是要让模型
            // 知道“这条已经收回去了”，引用/复述时得自己拿掉分寸）。
            var shownText = msg.Recalled ? RecallMark + msg.Text : msg.Text;
            var content = msg.Role == MessageRole.Peer && !string.IsNullOrWhiteSpace(msg.SenderName)
                ? $"{{{msg.SenderName}}}{{{shownText}--{FormatTime(msg.Timestamp)}}}" +
                  (msg.QqMessageId is long qid && quotableIds.Contains(qid) ? $" (#{qid})" : string.Empty)
                : shownText;
            var role = msg.Role == MessageRole.Self ? "assistant" : "user";

            // 多模态：消息带图片时，把图片（下载转 base64）一并发给模型识图
            if (msg.ImageUrls is { Count: > 0 } && msg.Role == MessageRole.Peer &&
                !(msg.QqMessageId is long suspectId && IsSuspectImage(suspectId)))
            {
                var contentParts = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = content } };
                foreach (var url in msg.ImageUrls.Take(3)) // 每条消息最多带 3 张图
                {
                    // 异步下载（绝不同步阻塞 UI 线程），失败跳过该图；
                    // 带上消息 id：地址过期（rkey 时效）时能找协议端重新签发。
                    var dataUrl = await _imageDownloader.DownloadAsDataUrl(url, ct, msg.QqMessageId);
                    if (dataUrl is not null)
                    {
                        attachedImages++;
                        if (msg.QqMessageId is long iid && !attachedImageIds.Contains(iid))
                        {
                            attachedImageIds.Add(iid);
                        }

                        contentParts.Add(new JsonObject
                        {
                            ["type"] = "image_url",
                            ["image_url"] = new JsonObject { ["url"] = dataUrl }
                        });
                    }
                }

                messages.Add(new JsonObject { ["role"] = role, ["content"] = contentParts });
                continue;
            }

            if (!string.IsNullOrWhiteSpace(content))
            {
                messages.Add(new JsonObject { ["role"] = role, ["content"] = content });
            }
        }

        // 上游（Gemini 等）不接受“最后一条是模型自己说的话”的请求：
        //   Requests ending with a model turn are not supported.
        // 而这恰好是机器人的一种正常情形 —— **自己触发的后续发言**：
        //   • 听完歌回来接着聊（模型填了 listen，分析完再请求一次）
        //   • 被戳之后想回一句（戳一戳不是消息，不往上下文里写）
        //   • 静默兜底补的那次请求
        // 这些时候上下文最后一条就是它自己刚说的话，一问就是 400。
        // 修法：补一条系统口吻的 user 轮把它顶成 user —— 顺便告诉模型“这是你自己的后续动作，
        // 不是又有人说话了”，免得它以为群里刚来了新消息、对着自己的话自问自答。
        var lastRole = messages.Count > 0 ? messages[^1]?["role"]?.GetValue<string>() : null;
        if (lastRole != "user")
        {
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = "（系统提示：上面最后一条是你自己刚说的话，之后没有新的群消息 —— " +
                              "本轮是你自己的后续动作触发的。想说就接着说，不想说就把 reply 留空。）"
            });
            FileLog.Write("Agent", "上下文以自己发言结尾 → 补一条系统口吻的 user 轮（上游不接受 model-turn 结尾）");
        }

        var replyModel = _settings.ReplyModel;
        var payload = new JsonObject
        {
            // 聊天回复：快速档开就换轻量模型（ReplyModel 里包了判断；其余后台活儿仍用主模型）
            ["model"] = replyModel,
            ["messages"] = messages,
            ["max_tokens"] = _settings.MaxTokens > 0 ? _settings.MaxTokens : 2048, // 仅防御非法值（旧数据可能为负数），不设上限
        };

        if (SupportsSamplingParameters(replyModel))
        {
            var temp = sampling?.Temperature ?? _settings.DefaultTemperature;
            payload["temperature"] = Math.Clamp(temp, 0.0, 2.0);
            var topP = sampling?.TopP ?? _settings.DefaultTopP;
            if (topP > 0)
            {
                payload["top_p"] = Math.Clamp(topP, 0.01, 1.0);
            }
        }

        // 主模型使用思考深度；快速档及旧自定义配置保留 Token 预算。
        var isFastModel = _settings.FastReply &&
                          !string.IsNullOrWhiteSpace(_settings.FastModel) &&
                          string.Equals(replyModel, _settings.FastModel.Trim(), StringComparison.Ordinal);
        var (thinkingEffort, thinkingTokens) = _settings.ResolveThinkingBudget(isFastModel);
        if (!string.IsNullOrWhiteSpace(thinkingEffort))
        {
            if (thinkingEffort is "none" or "low" or "medium" or "high" or "xhigh")
            {
                payload["reasoning_effort"] = thinkingEffort;
            }
            else if (thinkingTokens <= 0 && thinkingEffort is not ("off" or "disabled"))
            {
                payload["reasoning_effort"] = thinkingEffort;
            }

            if (thinkingTokens > 0)
            {
                payload["max_thinking_tokens"] = thinkingTokens;
            }
        }

        return new BuiltRequest(payload, attachedImages, attachedImageIds);
    }

    /// <summary>
    /// 检测模型是否支持自定义 temperature 与 top_p 超参数（推理模型如 o1/o3/reasoner 不支持自定义采样参数）。
    /// </summary>
    public static bool SupportsSamplingParameters(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return true;
        var m = model.ToLowerInvariant();
        return !m.StartsWith("o1") && !m.StartsWith("o3") && !m.Contains("reasoner");
    }

    /// <summary>
    /// 发一次聊天请求（含三条兜底重试）。返回上游的响应文本，以及"这次是不是去掉图片才拿到的"（供调用方补一条日志）。
    /// </summary>
    /// <param name="payload">请求体（<see cref="BuildAsync" /> 造的）。</param>
    /// <param name="attachedImages">这一轮真送出去的图片张数（写进诊断日志）。</param>
    /// <param name="attachedImageIds">这一轮真的送出去的图片所属消息 id（去掉图片重试成功后要把它们拉黑）。</param>
    public Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, CancellationToken ct)
        => SendAsync(payload, attachedImages, attachedImageIds, provider: null, ct);

    public async Task<SendOutcome> SendAsync(
        JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds,
        ModelProviderRoute? provider, CancellationToken ct)
    {
        try { return await SendCoreAsync(payload, attachedImages, attachedImageIds, provider, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (ModelFault) { throw; }
        catch (Exception ex) { throw new ModelFault(ModelFault.SafeCode(ex)); }
    }

    private async Task<SendOutcome> SendCoreAsync(
        JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds,
        ModelProviderRoute? provider, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Request-local copies preserve provider isolation and the caller's original payload.
        var requestPayload = (JsonObject)payload.DeepClone();
        if (provider is not null && !string.IsNullOrWhiteSpace(provider.ModelName))
            requestPayload["model"] = provider.ModelName.Trim();
        var requestUrl = provider is null ? Url() : BuildUrl(provider.BaseUrl);
        var apiKey = (provider is null ? _settings.ApiKey : provider.ApiKey).Trim();
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ModelFault("model_credential_missing");

        string json;
        for (var attempt = 0; ; attempt++)
        {
            using var retryBudget = attempt > 0 ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
            retryBudget?.CancelAfter(TimeSpan.FromSeconds(TimeoutRetrySeconds));
            (HttpStatusCode Status, string Body) reply;
            try
            {
                reply = await SendOnceAsync(_chatHttp, requestPayload, requestUrl, apiKey, retryBudget?.Token ?? ct);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (attempt >= 1) throw new ModelFault("model_timeout");
                FileLog.Write("Agent", "model_timeout retry=1");
                await Clock.Delay(TimeSpan.FromSeconds(2), ct);
                continue;
            }
            catch (HttpRequestException ex) when (ex is not ModelFault)
            {
                throw new ModelFault("model_network_error", ex.StatusCode);
            }

            if ((int)reply.Status is >= 200 and < 300)
            {
                ModelFault.ValidateEnvelope(reply.Body);
                json = reply.Body;
                break;
            }

            var status = (int)reply.Status;
            var reasoningError = reply.Body.Contains("reason", StringComparison.OrdinalIgnoreCase)
                || reply.Body.Contains("effort", StringComparison.OrdinalIgnoreCase)
                || reply.Body.Contains("thinking", StringComparison.OrdinalIgnoreCase)
                || reply.Body.Contains("budget", StringComparison.OrdinalIgnoreCase);
            if (status is 400 or 422 && reasoningError &&
                (requestPayload.ContainsKey("reasoning_effort") || requestPayload.ContainsKey("max_thinking_tokens")))
            {
                requestPayload.Remove("reasoning_effort");
                requestPayload.Remove("max_thinking_tokens");
                FileLog.Warn("Agent", "model_reasoning_unsupported fallback=default");
                attempt--; // One parameter fallback, in addition to the original bounded transient retry.
                continue;
            }
            if (attempt >= 1 || (status < 500 && status != 429))
                throw new ModelFault(ModelFault.StatusError(reply.Status), reply.Status);
            FileLog.Write("Agent", $"model_transient_error status={status} retry=1");
            await Clock.Delay(TimeSpan.FromSeconds(2), ct);
        }

        if (!HasChoices(json))
        {
            FileLog.Write("Agent", "model_empty_choices retry=1");
            await Clock.Delay(TimeSpan.FromSeconds(2), ct);
            var retry = await SendFallbackAsync(requestPayload, requestUrl, apiKey, ct);
            if ((int)retry.Status is >= 200 and < 300)
            {
                ModelFault.ValidateEnvelope(retry.Body);
                json = retry.Body;
            }
            else FileLog.Write("Agent", $"model_empty_retry_failed status={(int)retry.Status}");
        }

        var textOnlyRetry = false;
        if (!HasChoices(json) && attachedImages > 0)
        {
            var plain = await SendFallbackAsync(StripImages(requestPayload), requestUrl, apiKey, ct);
            if ((int)plain.Status is >= 200 and < 300)
            {
                ModelFault.ValidateEnvelope(plain.Body);
                if (HasChoices(plain.Body))
                {
                    json = plain.Body;
                    textOnlyRetry = true;
                    RememberSuspectImages(attachedImageIds);
                    FileLog.Write("Agent", $"model_text_only_fallback images={attachedImages}");
                }
            }
        }
        return new SendOutcome(json, textOnlyRetry);
    }

    private static async Task<(HttpStatusCode Status, string Body)> SendOnceAsync(
        IModelHttpFetcher http, JsonObject payload, string url, string apiKey, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using var response = await http.SendAsync(request, ct);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(ct));
    }

    private async Task<(HttpStatusCode Status, string Body)> SendFallbackAsync(
        JsonObject payload, string url, string apiKey, CancellationToken ct)
    {
        try { return await SendOnceAsync(_auxHttp, payload, url, apiKey, ct); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new ModelFault("model_timeout"); }
        catch (HttpRequestException ex) when (ex is not ModelFault) { throw new ModelFault("model_network_error", ex.StatusCode); }
    }
    /// <summary>
    /// 响应里到底有没有 choices。空数组 / 根本没这个字段 = 上游把回复吞了，
    /// **不是**“模型自己决定不说话”—— 两件事在日志里必须分得清，否则主人看不出是网关出事了。
    /// </summary>
    internal static bool HasChoices(string json) => ModelJson.HasChoices(json);

    /// <summary>
    /// 把请求体里所有 image_url 部分抽掉（其他一字不改）—— 上游因图片回零候选时的兜底重试。
    /// 用 JSON 层复制，不重下图片（图已经在内存里，只是不再送给模型）。
    /// </summary>
    private static JsonObject StripImages(JsonObject payload)
    {
        var clone = (JsonObject)JsonNode.Parse(payload.ToJsonString())!;
        if (clone["messages"] is not JsonArray messages)
        {
            return clone;
        }

        foreach (var message in messages)
        {
            if (message?["content"] is not JsonArray parts)
            {
                continue;
            }

            for (var i = parts.Count - 1; i >= 0; i--)
            {
                if (parts[i] is JsonObject part && part["type"]?.GetValue<string>() == "image_url")
                {
                    parts.RemoveAt(i);
                }
            }
        }

        return clone;
    }

    /// <summary>
    /// 记下“疑似害得上游吞回复”的图片消息：后续上下文里不再重复送它们的图。
    /// 有上限（200 条，超了掐最早的），只是个避雷名单，不做持久化 —— 重启就忘了，
    /// 免得一次误判永久屏蔽某张图。
    /// </summary>
    private void RememberSuspectImages(IReadOnlyList<long> messageIds)
    {
        lock (_imageFilterSuspects)
        {
            foreach (var id in messageIds)
            {
                if (_imageFilterSuspects.Add(id))
                {
                    _imageFilterSuspectOrder.Add(id);
                }
            }

            while (_imageFilterSuspectOrder.Count > 200)
            {
                _imageFilterSuspects.Remove(_imageFilterSuspectOrder[0]);
                _imageFilterSuspectOrder.RemoveAt(0);
            }
        }
    }

    /// <summary>这张图是不是被拉黑了（拉黑了就不送，省得整个窗口又被上游掸掉）。</summary>
    private bool IsSuspectImage(long messageId)
    {
        lock (_imageFilterSuspects)
        {
            return _imageFilterSuspects.Contains(messageId);
        }
    }

    private readonly HashSet<long> _imageFilterSuspects = new();
    private readonly List<long> _imageFilterSuspectOrder = new();


    /// <summary>时间短格式：当天 HH:mm，跨天 MM-dd HH:mm。</summary>
    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>时间短格式：当天 HH:mm，跨天 MM-dd HH:mm。</summary>
    private static string FormatTime(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == Clock.LocalDateTime.Date
            ? local.ToString("HH:mm")
            : local.ToString("MM-dd HH:mm");
    }
}
