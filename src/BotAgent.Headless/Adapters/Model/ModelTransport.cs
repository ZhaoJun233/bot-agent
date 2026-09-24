using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Model;
using BotAgent.Domain.Ports;
using BotAgent.Services;
using BotAgent.Services.Agent;

namespace BotAgent.Adapters.Model;

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
internal sealed class ModelTransport : IModelTransport
{
    /// <summary>超时重试用的小预算（秒）：第一次已经等过一大截了，第二次不该再等满。</summary>
    private const int TimeoutRetrySeconds = 30;

    /// <summary>撤回标记：内容是保留的，但必须让模型一眼看出“这条已经收回去了”。</summary>
    private const string RecallMark = "[已撤回] ";

    private readonly SettingsBox _box;
    private readonly IHttpFetcher _chatHttp;
    private readonly IHttpFetcher _auxHttp;
    private readonly ImageDownloader _imageDownloader;
    private readonly TimeSpan _chatTimeout;

    public ModelTransport(SettingsBox box, IHttpFetcher chatHttp, IHttpFetcher auxHttp, ImageDownloader imageDownloader, TimeSpan chatTimeout)
    {
        _box = box;
        _chatHttp = chatHttp;
        _auxHttp = auxHttp;
        _imageDownloader = imageDownloader;
        _chatTimeout = chatTimeout;
    }

    /// <summary>配置读取入口（当前发布版，见 <see cref="SettingsBox" />）。</summary>
    private AppSettings _settings => _box.Current;

    /// <summary>拼 /chat/completions 地址（静态：客户端里其它走同一接口的方法也用它）。</summary>
    public static string BuildUrl(string baseUrl)
    {
        var url = (baseUrl ?? string.Empty).Trim().TrimEnd('/');
        return url.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)
            ? url
            : url + "/chat/completions";
    }

    private string Url() => BuildUrl(_settings.ModelBaseUrl);

    /// <summary>
    /// 把上下文窗口 + 系统提示词组装成请求体。返回的 <c>AttachedImages</c> / <c>AttachedImageIds</c>
    /// 供调用方写诊断日志与拉黑嫌疑图片（上游吞回复时这两个数是关键线索）。
    /// </summary>
    public async Task<BuiltRequest> BuildAsync(
        IReadOnlyList<ChatMessage> window, string systemContent, IReadOnlyCollection<long> quotableIds, CancellationToken ct)
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

        var payload = new JsonObject
        {
            // 聊天回复：快速档开就换轻量模型（ReplyModel 里包了判断；其余后台活儿仍用主模型）
            ["model"] = _settings.ReplyModel,
            ["messages"] = messages,
            ["max_tokens"] = _settings.MaxTokens > 0 ? _settings.MaxTokens : 2048, // 仅防御非法值（旧数据可能为负数），不设上限
            ["temperature"] = 0.7
        };

        return new BuiltRequest(payload, attachedImages, attachedImageIds);
    }

    /// <summary>
    /// 发一次聊天请求（含三条兜底重试）。返回上游的响应文本，以及"这次是不是去掉图片才拿到的"（供调用方补一条日志）。
    /// </summary>
    /// <param name="payload">请求体（<see cref="BuildAsync" /> 造的）。</param>
    /// <param name="attachedImages">这一轮真送出去的图片张数（写进诊断日志）。</param>
    /// <param name="attachedImageIds">这一轮真的送出去的图片所属消息 id（去掉图片重试成功后要把它们拉黑）。</param>
    public async Task<SendOutcome> SendAsync(JsonObject payload, int attachedImages, IReadOnlyList<long> attachedImageIds, CancellationToken ct)
    {
        // 请求体是一次性的（HttpContent 只能读一次），每次重发都要重建一份带鉴权头的请求
        var request = new HttpRequestMessage(HttpMethod.Post, Url())
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());

        // 5xx/429 与**超时**各重试一次。
        // 为什么要：上游网关（多账号池网关之类）经常回 503 auth_unavailable / No capacity ——
        // 实测 17:28 连挨三次，每条都直接“模型请求失败”丢掉一次回复；晚 2 秒再问一次往往就能拿到。
        // 超时同理：号主 11:30 那次就是 60 秒到点被取消（当时超时写死 60 秒），其实再问一次经常能拿到。
        // 只重试一次、且只对 5xx/429/超时：4xx 是请求本身的问题，重试没意义。
        HttpResponseMessage? response = null;
        string? failureDetail = null;
        for (var attempt = 0; ; attempt++)
        {
            // 第二次尝试只给 30 秒（第一次已经等了一大截，卡住就早点放手）
            using var retryBudget = attempt > 0
                ? CancellationTokenSource.CreateLinkedTokenSource(ct)
                : null;
            if (retryBudget is not null)
            {
                retryBudget.CancelAfter(TimeSpan.FromSeconds(TimeoutRetrySeconds));
            }

            var sendToken = retryBudget?.Token ?? ct;
            try
            {
                response = await _chatHttp.SendAsync(request, sendToken);
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                // 不是调用方取消的 = 超时（HttpClient 自己的，或者上面那个 30 秒预算）
                response?.Dispose();
                response = null;
                var limit = attempt > 0 ? TimeoutRetrySeconds : (int)_chatTimeout.TotalSeconds;
                failureDetail = $"模型请求超时（{limit} 秒没回应）";
                if (attempt >= 1)
                {
                    break;
                }

                Services.FileLog.Write("Agent", $"模型请求超时（{limit} 秒）→ 2 秒后重试一次（这次最多等 {TimeoutRetrySeconds} 秒）");
                await Clock.Delay(TimeSpan.FromSeconds(2), ct);
                request = new HttpRequestMessage(HttpMethod.Post, Url())
                {
                    Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
                continue;
            }

            if (response.IsSuccessStatusCode)
            {
                break;
            }

            var detail = await response.Content.ReadAsStringAsync(ct);
            var status = (int)response.StatusCode;
            failureDetail = $"Chat Completions 返回 {status}：{Truncate(detail, 200)}";
            if (attempt >= 1 || (status < 500 && status != 429))
            {
                response.Dispose();
                response = null;
                break;
            }

            Services.FileLog.Write("Agent", $"模型返回 {status}（{Truncate(detail, 80)}）→ 2 秒后重试一次");
            response.Dispose();
            response = null;
            await Clock.Delay(TimeSpan.FromSeconds(2), ct);

            // 请求体是一次性的，重试要重建一份
            request = new HttpRequestMessage(HttpMethod.Post, Url())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
        }

        if (response is null)
        {
            throw new HttpRequestException(failureDetail ?? "Chat Completions 请求失败");
        }

        using var _ = response;

        var json = await response.Content.ReadAsStringAsync(ct);

        for (var emptyTry = 0; emptyTry < 1 && !HasChoices(json); emptyTry++)
        {
            Services.FileLog.Write("Agent", $"上游返回空 choices（{Truncate(json, 140)}）→ 2 秒后重试一次");
            await Clock.Delay(TimeSpan.FromSeconds(2), ct);

            // 请求体是一次性的，重试要重建一份（与上面 5xx 重试同理）
            using var retryRequest = new HttpRequestMessage(HttpMethod.Post, Url())
            {
                Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json")
            };
            retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var retryResponse = await _auxHttp.SendAsync(retryRequest, ct);
            if (!retryResponse.IsSuccessStatusCode)
            {
                // 重试本身也塌了：保留上一次的响应文本（下面按空响应处理），别再把它覆盖成错误页
                Services.FileLog.Write("Agent", $"空 choices 后的重试返回 {(int)retryResponse.StatusCode} → 这轮按空响应处理");
                break;
            }

            json = await retryResponse.Content.ReadAsStringAsync(ct);
        }

        // ③ 带图的两轮都空 → 去掉图片再试一次。
        //    为什么值得试：上游（上游网关 后端）对某些内容会直接回**零候选**（HTTP 200、
        //    用量里只有 prompt tokens），表现就是“这个群这几轮怎么问都是空的”。
        //    实测那种情况下同一个 payload 重发多少次都空（22:42-22:46 同一尺寸两发两空），
        //    而同时段别的会话正常 —— 是内容触发，不是网络抖动。
        //    去掉图往往就能说话：这一轮先据文字回（总比一句话不说强），
        //    同时把嫌疑图片的消息 id 记下来 —— 后续上下文不再重复送它们（自愈）。
        var textOnlyRetry = false;
        if (!HasChoices(json) && attachedImages > 0)
        {
            var stripped = StripImages(payload);
            using var plainRequest = new HttpRequestMessage(HttpMethod.Post, Url())
            {
                Content = new StringContent(stripped.ToJsonString(), Encoding.UTF8, "application/json")
            };
            plainRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.ApiKey.Trim());
            using var plainResponse = await _auxHttp.SendAsync(plainRequest, ct);
            if (plainResponse.IsSuccessStatusCode)
            {
                var plainJson = await plainResponse.Content.ReadAsStringAsync(ct);
                if (HasChoices(plainJson))
                {
                    json = plainJson;
                    textOnlyRetry = true;
                    RememberSuspectImages(attachedImageIds);
                    Services.FileLog.Write("Agent",
                        $"两轮空后去掉图片再试 → 拿到了回复：疑似图片触发上游过滤（消息 {string.Join(",", attachedImageIds)}），本轮只据文字回；" +
                        $"这些图后续会从上下文里跳过");
                }
            }
        }

        return new SendOutcome(json, textOnlyRetry);
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
