using System.Text.Json;
using System.Text.Json.Nodes;
using QQChatAgent.Services.Music;
using QQChatAgent.Services.Qq;

namespace QQChatAgent.Services.OneBot;

public enum GatewayState
{
    Disconnected,
    Connecting,
    Connected
}

/// <summary>
/// OneBot v11 网关门面：按设置选择传输层，统一分发事件与动作响应。
/// 账号登录方案（NapCat / Lagrange / LLOneBot 等协议端）通过此网关接入，
/// 同时实现统一消息源接口 IQqChatSource。
/// </summary>
public sealed class OneBotGateway : IQqChatSource, IDisposable
{
    /// <summary>
    /// 这条路是私域通道（自建协议端 NapCat / OneBot）。
    /// 官方商用那条是 <c>OfficialBotGateway</c>，两者由 <see cref="ChannelRouter"/> 聚合后交给上层。
    /// </summary>
    public string Channel => Channels.Private;

    private readonly object _gate = new();
    private readonly Dictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private IOneBotTransport? _transport;
    private CancellationTokenSource? _cts;
    private AppSettings _settings;
    private long _selfId;

    /// <summary>
    /// 登录账号 QQ 号提示（headless 版由配置注入）。
    /// 协议端（NapCat）有时不在事件里带 self_id，用它兜底识别 @机器人。
    /// </summary>
    public long SelfIdHint { get; set; }

    public event Action<QqChatMessage>? MessageReceived;
    public event Action<GatewayState>? StateChanged;

    /// <summary>连接状态（IQqChatSource 统一事件）。</summary>
    public event Action<bool>? ConnectionChanged;

    public bool IsConnected { get; private set; }

    public OneBotGateway(AppSettings settings) => _settings = settings;

    /// <summary>更新配置；返回连接信息（协议/地址/Token）是否发生变化，供上层决定是否重建连接。</summary>
    public bool UpdateAndMarkIfProtocolChanged(AppSettings settings)
    {
        var changed = _settings.OneBotProtocol != settings.OneBotProtocol ||
                      _settings.OneBotAddress != settings.OneBotAddress ||
                      _settings.OneBotToken != settings.OneBotToken;
        _settings = settings;
        return changed;
    }

    public void Start()
    {
        Stop();

        _cts = new CancellationTokenSource();
        _transport = CreateTransport(_settings);
        _transport.OnText += OnTransportText;
        _transport.OnStateChanged += OnTransportStateChanged;

        try
        {
            _ = _transport.StartAsync();
        }
        catch (Exception ex)
        {
            Log(ex.Message);
        }

        Log($"已启动 ({_settings.OneBotProtocol} → {_settings.OneBotAddress})");
    }

    public void Stop()
    {
        if (_transport is not null)
        {
            _transport.OnText -= OnTransportText;
            _transport.OnStateChanged -= OnTransportStateChanged;
            _transport.Stop();
            _transport.Dispose();
            _transport = null;
        }

        _cts?.Cancel();
        _cts = null;
        SetConnected(false);
    }

    public void Dispose() => Stop();

    /// <summary>向某个私聊/群聊发送纯文本消息。</summary>
    /// <summary>收到戳一戳（post_type=notice）。别人互戳也会报上来。</summary>
    public event Action<QqPokeEvent>? Poked;

    /// <summary>收到撤回（post_type=notice 下的 group_recall / friend_recall）。</summary>
    public event Action<QqRecallEvent>? MessageRecalled;

    public async Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null)
    {
        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        // 带"回复"引用原消息（触发 QQ 回复语句功能）
        string messageJson = replyToMessageId is long rid
            ? $"[{{\"type\":\"reply\",\"data\":{{\"id\":{rid}}}}},{{\"type\":\"text\",\"data\":{{\"text\":{Json(text)}}}}}]"
            : Json(text);
        var result = await SendActionAsync(action, $"{{\"{key}\":{targetId},\"message\":{messageJson}}}", ct);
        var ok = result is not null && GetRetcode(result) == 0;
        // 协议端会在 data.message_id 里回新消息的 id（数字或字符串两种写法都见过）：
        // 记下它，别人引用回复机器人那句话时才能对上号（见 handoff-4 §27）。
        return new SendResult(ok, ok ? ReadMessageId(result) : 0);
    }

    /// <summary>从动作响应里取 data.message_id（数字/字符串都收）。</summary>
    private static long ReadMessageId(JsonNode? result)
    {
        var node = result?["data"]?["message_id"];
        return node switch
        {
            null => 0,
            JsonValue value when value.TryGetValue<long>(out var asLong) => asLong,
            JsonValue value when long.TryParse(value.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }

    /// <summary>
    /// 发一张音乐分享卡片（OneBot music 段）。type=163 → 网易云；QQ 客户端会渲染成可播放卡片。
    /// 这是机器人“主动分享一首歌”的出路 —— 不是每次都只能发一段文字。
    /// </summary>
    public async Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
    {
        // title 只在官方通道（发不了卡片）用得上，这里不接卡片以外的东西。
        if (string.IsNullOrWhiteSpace(songId))
        {
            return false;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        var music = $"{{\"type\":\"music\",\"data\":{{\"type\":{Json(platform)},\"id\":{Json(songId)}}}}}";
        var result = await SendActionAsync(action, $"{{\"{key}\":{targetId},\"message\":[{music}]}}", ct);
        var code = result is null ? -999 : GetRetcode(result);
        if (code != 0)
        {
            // 把上游原话打出来：NapCat/NTQQ 各版本对 music 段的接受程度不一样，
            // 这段日志是判断“到底是不支持还是参数不对”的唯一依据
            Log($"music 段发送失败 retcode={code}（platform={platform}, id={songId}）：{result?.ToJsonString() ?? "(无响应，可能超时)"}");
        }

        return code == 0;
    }

    /// <summary>
    /// 发一条语音（OneBot record 段）。
    ///
    /// 这里只把 TTS 的 URL 塞进 record 段，**由 NapCat 自己下载 → 转 silk → 上传**：
    ///   • 机器人不用实现 silk 编码（NapCat 内置 native 转换器，比我们可靠）；
    ///   • 也不用把几百 KB 音频 base64 塞进 WebSocket。
    /// 代价：NapCat 必须能访问这个 URL（同网段时就是 http://tts:5000）。
    /// 失败时把上游原话打出来（retcode + 响应体）—— 这是判断"协议端不支持"还是
    /// "地址不可达"的唯一依据；上层据此退化成发文字，绝不能什么都不发。
    /// </summary>
    public async Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            return false;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        var record = $"{{\"type\":\"record\",\"data\":{{\"file\":{Json(audioUrl)}}}}}";
        var result = await SendActionAsync(action, $"{{\"{key}\":{targetId},\"message\":[{record}]}}", ct);
        var code = result is null ? -999 : GetRetcode(result);
        if (code != 0)
        {
            Log($"record 段发送失败 retcode={code}（url={audioUrl}）：{result?.ToJsonString() ?? "(无响应，可能超时)"}");
        }

        return code == 0;
    }

    /// <summary>
    /// 发送一张图片（表情包）。
    /// 用 base64:// 而不是本地路径：机器人容器里的 /data/stickers 不在 NapCat 容器里，
    /// 而 base64 不依赖协议端的文件访问权限（NapCat 原生支持 base64://）。
    /// </summary>
    public async Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
    {
        if (data.Length == 0)
        {
            return false;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        var image = $"{{\"type\":\"image\",\"data\":{{\"file\":\"base64://{Convert.ToBase64String(data)}\"}}}}";
        var messageJson = replyToMessageId is long rid
            ? $"[{{\"type\":\"reply\",\"data\":{{\"id\":{rid}}}}},{image}]"
            : $"[{image}]";

        var result = await SendActionAsync(action, $"{{\"{key}\":{targetId},\"message\":{messageJson}}}", ct);
        return result is not null && GetRetcode(result) == 0;
    }

    /// <summary>
    /// 戳一戳某人。NapCat 把动作拆成了两个：群聊 group_poke(group_id+user_id)，私聊 friend_poke(user_id)。
    /// 目标就是“被戳的人”，与发消息的 targetId 无关（群里可以是任何成员）。
    /// </summary>
    public async Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            return false;
        }

        var (action, paramsJson) = isGroup
            ? ("group_poke", $"{{\"group_id\":{targetId},\"user_id\":{userId}}}")
            : ("friend_poke", $"{{\"user_id\":{userId}}}");

        // 协议端不支持（retcode 1404 / 未知动作）不当作错误刷屏，只记一次日志
        var result = await SendActionAsync(action, paramsJson, ct);
        var ok = result is not null && GetRetcode(result) == 0;
        if (!ok && _pokeUnsupported != action)
        {
            _pokeUnsupported = action;
            Log($"{action} 失败：协议端可能不支持戳一戳，以后不再重试该动作");
        }

        return ok;
    }

    private string? _pokeUnsupported;

    // ══════════ QQ 行为动作（服务器内置 agent 用；号主 2026-09-18：“比如点赞”）══════════
    //
    // 与上面那些“机器人自己要发的消息”不同，这一组是**别人让机器人做的小动作**。
    // 共性：都是 OneBot/NapCat 的动作，失败原因（retcode）对使用者有用，所以统一记一条日志；
    // 返回 bool 就够了 —— 给模型看的那句话由 QqActionHost 拼（它知道上下文）。

    /// <summary>
    /// 给某人点赞（OneBot <c>send_like</c>）。times：一次点几个（QQ 自己卡上限，这里 1-20）。
    ///
    /// 为什么失败后还要「先看一眼对方资料卡再补一次」：QQ 侧对**从未互动过的账号**会把赞拦下来
    /// （社区实测：同样是非好友，之前看过/赞过这个人就能成，从没见过就回「由于对方权限设置，点赞失败」
    /// 或干脆静默不生效 —— NapCat issue #617 / #1676）。手机 QQ 点赞时会先打开对方资料卡，
    /// 而 <c>send_like</c> 是直接打点赞接口、没有这一步，所以这里被回绝时自己补上（只补一次，不循环重试）。
    /// </summary>
    public async Task<bool> SendLikeAsync(long userId, int times, CancellationToken ct = default)
    {
        var payload = $"{{\"user_id\":{userId},\"times\":{Math.Clamp(times, 1, 20)}}}";
        if (await RunActionAsync("send_like", payload, ct))
        {
            return true;
        }

        await RunActionAsync("get_stranger_info", $"{{\"user_id\":{userId}}}", ct);
        return await RunActionAsync("send_like", payload, ct);
    }

    /// <summary>给某条消息贴表情回应（NapCat 扩展 <c>set_msg_emoji_like</c>）。emojiId 是字符串，默认 👍 = 128077。</summary>
    public Task<bool> SetMessageEmojiLikeAsync(long messageId, string emojiId, CancellationToken ct = default)
        => RunActionAsync("set_msg_emoji_like",
            $"{{\"message_id\":{messageId},\"emoji_id\":{Json(string.IsNullOrWhiteSpace(emojiId) ? "128077" : emojiId)},\"set\":true}}", ct);

    /// <summary>撤回一条消息（OneBot <c>delete_msg</c>）。只有自己发的、或有管理权限时别人的才撤得掉。</summary>
    public Task<bool> DeleteMessageAsync(long messageId, CancellationToken ct = default)
        => RunActionAsync("delete_msg", $"{{\"message_id\":{messageId}}}", ct);

    /// <summary>禁言/解除禁言（OneBot <c>set_group_ban</c>）。seconds=0 即解除。</summary>
    public Task<bool> SetGroupBanAsync(long groupId, long userId, int seconds, CancellationToken ct = default)
        => RunActionAsync("set_group_ban", $"{{\"group_id\":{groupId},\"user_id\":{userId},\"duration\":{Math.Max(0, seconds)}}}", ct);

    /// <summary>把某人踢出群（OneBot <c>set_group_kick</c>）。</summary>
    public Task<bool> SetGroupKickAsync(long groupId, long userId, bool rejectAdd, CancellationToken ct = default)
        => RunActionAsync("set_group_kick",
            $"{{\"group_id\":{groupId},\"user_id\":{userId},\"reject_add_request\":{(rejectAdd ? "true" : "false")}}}", ct);

    /// <summary>改群名片（OneBot <c>set_group_card</c>）。card 空串 = 清掉。</summary>
    public Task<bool> SetGroupCardAsync(long groupId, long userId, string card, CancellationToken ct = default)
        => RunActionAsync("set_group_card", $"{{\"group_id\":{groupId},\"user_id\":{userId},\"card\":{Json(card ?? string.Empty)}}}", ct);

    /// <summary>改群名（OneBot <c>set_group_name</c>）。</summary>
    public Task<bool> SetGroupNameAsync(long groupId, string groupName, CancellationToken ct = default)
        => RunActionAsync("set_group_name", $"{{\"group_id\":{groupId},\"group_name\":{Json(groupName ?? string.Empty)}}}", ct);

    /// <summary>退群/解散（OneBot <c>set_group_leave</c>）。dismiss 只有群主能成。</summary>
    public Task<bool> SetGroupLeaveAsync(long groupId, bool dismiss, CancellationToken ct = default)
        => RunActionAsync("set_group_leave", $"{{\"group_id\":{groupId},\"is_dismiss\":{(dismiss ? "true" : "false")}}}", ct);

    /// <summary>发一个动作并等结果；失败时把 retcode/wording 记进日志（排查用，不回群）。</summary>
    private async Task<bool> RunActionAsync(string action, string paramsJson, CancellationToken ct)
    {
        var result = await SendActionAsync(action, paramsJson, ct);
        var ok = result is not null && GetRetcode(result) == 0;
        if (!ok)
        {
            var code = result is null ? "null" : GetRetcode(result).ToString();
            var why = result?["wording"]?.GetValue<string>() ?? result?["message"]?.GetValue<string>() ?? result?["msg"]?.GetValue<string>() ?? string.Empty;
            Log($"动作 {action} 失败（retcode={code}{(why.Length > 0 ? $" {Shorten(why, 60)}" : string.Empty)}）");
        }

        return ok;
    }

    /// <summary>
    /// 按消息 id 拿回这条消息里所有图片的**当前**地址（OneBot 的 get_msg）。
    /// 用途：QQ 图片地址带时效 rkey，过期后 CDN 一律 400；而协议端能重新签发一份
    /// （实测：同一条消息重新签发后就能下到）。拿不到就返回空列表。
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
    {
        var urls = new List<string>();
        var result = await SendActionAsync("get_msg", $"{{\"message_id\":{messageId}}}", ct);
        var segments = result?["data"]?["message"] as JsonArray;
        if (segments is null)
        {
            return urls;
        }

        foreach (var seg in segments)
        {
            if (seg?["type"]?.GetValue<string>() != "image")
            {
                continue;
            }

            var url = seg["data"]?["url"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(url) &&
                (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                 url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            {
                urls.Add(url);
            }
        }

        return urls;
    }

    /// <summary>
    /// 拉取登录账号在 QQ 里的“收藏表情”图片地址（NapCat 扩展动作 fetch_custom_face）。
    /// 这是机器人“自己添加表情包”的来源之一。接口不存在/未登录时返回空列表。
    /// </summary>
    public async Task<List<string>> FetchCustomFacesAsync(int count = 48, CancellationToken ct = default)
    {
        var urls = new List<string>();
        var result = await SendActionAsync("fetch_custom_face", $"{{\"count\":{count}}}", ct);

        CollectFaceUrls(result?["data"], urls);

        if (urls.Count == 0)
        {
            // 各版本协议端返回形状不一致（数组 / {urls:[…]} / 字符串），
            // 拿不到就把原始响应的前一段写进日志 —— 下次一看就知道是“不支持”还是“格式变了”。
            var raw = result?.ToJsonString() ?? "(null)";
            Log($"fetch_custom_face 未取到图片地址，原始响应：{(raw.Length <= 260 ? raw : raw[..260] + "…")}");
        }

        return urls;
    }

    /// <summary>从 protocol 的各种返回形状里抽图片地址（数组 / 对象字段 / JSON 字符串）。</summary>
    private static void CollectFaceUrls(JsonNode? data, List<string> urls)
    {
        switch (data)
        {
            case JsonArray array:
                foreach (var item in array)
                {
                    CollectFaceUrls(item, urls);
                }

                break;

            case JsonObject obj:
                foreach (var key in new[] { "url", "file", "urls", "faces", "data", "list" })
                {
                    if (obj.TryGetPropertyValue(key, out var inner))
                    {
                        CollectFaceUrls(inner, urls);
                    }
                }

                break;

            case JsonValue value:
                var text = value.GetValue<object?>() switch
                {
                    string s => s,
                    _ => null
                };

                if (!string.IsNullOrWhiteSpace(text))
                {
                    // 有的版本把“地址数组”当 JSON 字符串返回
                    if (text.StartsWith('[') || text.StartsWith('{'))
                    {
                        try
                        {
                            CollectFaceUrls(JsonNode.Parse(text), urls);
                            return;
                        }
                        catch
                        {
                            // 不是 JSON，当普通字符串处理
                        }
                    }

                    if (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        urls.Add(text);
                    }
                }

                break;
        }
    }

    /// <summary>获取群名称（用于会话列表显示）。失败返回 null。</summary>
    public async Task<string?> GetGroupNameAsync(long groupId, CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_group_info", $"{{\"group_id\":{groupId}}}", ct);
        return result?["data"]?["group_name"]?.GetValue<string>();
    }

    /// <summary>
    /// 拉群成员资料（身份 + 群头衔）。失败/不支持返回 null。
    /// no_cache=true：身份与头衔是“刚改就得看到”的东西（刚升管理员），缓存不值。
    /// </summary>
    public async Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_group_member_info",
            $"{{\"group_id\":{groupId},\"user_id\":{userId},\"no_cache\":true}}", ct);
        var data = result?["data"];
        if (data is null)
        {
            return null;
        }

        var uid = ReadLong(data["user_id"]) ?? userId;
        if (uid == 0)
        {
            return null;
        }

        return new GroupMemberInfo(
            uid,
            ReadLong(data["group_id"]) ?? groupId,
            data["nickname"]?.GetValue<string>(),
            data["card"]?.GetValue<string>(),
            data["role"]?.GetValue<string>(),
            data["title"]?.GetValue<string>(),
            ReadInt(data["level"]));
    }

    /// <summary>
    /// 宽容地读一个数字。
    /// 为什么需要：协议端对同一字段的类型并不统一 —— NapCat 的 <c>level</c> 是字符串（"1"），
    /// 直接用 <c>GetValue&lt;int&gt;()</c> 会抛异常，而异常会把**整次查询**废掉（刚踩过：
    /// 头衔永远拿不到，日志里只有一句 “An element of type 'String' cannot be converted to a 'System.Int32'”）。
    /// </summary>
    private static int ReadInt(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return 0;
        }

        if (value.TryGetValue<int>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, out var parsed) ? parsed : 0;
    }

    /// <summary>宽容地读一个 64 位整数（同上：有的实现用字符串发 id）。</summary>
    private static long? ReadLong(JsonNode? node)
    {
        if (node is not JsonValue value)
        {
            return null;
        }

        if (value.TryGetValue<long>(out var number))
        {
            return number;
        }

        return value.TryGetValue<string>(out var text) && long.TryParse(text, out var parsed) ? parsed : null;
    }

    /// <summary>拉取群历史消息（NapCat 扩展动作 get_group_msg_history，返回最新在前的列表）。</summary>
    public async Task<List<QqChatMessage>> GetGroupMsgHistoryAsync(long groupId, int count = 20, CancellationToken ct = default)
    {
        var list = new List<QqChatMessage>();
        var result = await SendActionAsync("get_group_msg_history", $"{{\"group_id\":{groupId},\"count\":{count},\"message_seq\":0}}", ct);
        var messages = result?["data"]?["messages"] as JsonArray;
        if (messages is null)
        {
            return list;
        }

        var selfId = _selfId;
        foreach (var item in messages)
        {
            if (item is not JsonObject root)
            {
                continue;
            }

            var userId = root["user_id"]?.GetValue<long>() ?? 0;
            var messageId = root["message_id"]?.GetValue<long>() ?? 0;
            var time = root["time"]?.GetValue<long>() ?? 0;
            var raw = root["raw_message"]?.GetValue<string>();
            var (text, mentioned, historyImages, _) = ParseMessage(root["message"] as JsonArray, raw, selfId);
            var sender = root["sender"] is JsonObject so
                ? (string.IsNullOrWhiteSpace(so["card"]?.GetValue<string>())
                    ? so["nickname"]?.GetValue<string>() ?? userId.ToString()
                    : so["card"]!.GetValue<string>())
                : userId.ToString();

            if (string.IsNullOrWhiteSpace(text) && !mentioned)
            {
                continue;
            }

            list.Add(new QqChatMessage(
                messageId,
                true,
                userId,
                groupId,
                sender,
                text,
                DateTimeOffset.FromUnixTimeSeconds(time),
                mentioned,
                historyImages.Count > 0 ? historyImages : null));
        }

        return list;
    }

    /// <summary>拉取机器人所在群列表（get_group_list）。</summary>
    public async Task<List<(long GroupId, string GroupName)>> GetGroupListAsync(CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_group_list", "{}", ct);
        var list = new List<(long, string)>();
        var data = result?["data"] as JsonArray;
        if (data is null)
        {
            return list;
        }

        foreach (var item in data)
        {
            var id = item?["group_id"]?.GetValue<long>() ?? 0;
            var name = item?["group_name"]?.GetValue<string>() ?? string.Empty;
            if (id != 0)
            {
                list.Add((id, name));
            }
        }

        return list;
    }

    /// <summary>拉取好友列表（get_friend_list）。</summary>
    public async Task<List<(long UserId, string Nickname)>> GetFriendListAsync(CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_friend_list", "{}", ct);
        var list = new List<(long, string)>();
        var data = result?["data"] as JsonArray;
        if (data is null)
        {
            return list;
        }

        foreach (var item in data)
        {
            var id = item?["user_id"]?.GetValue<long>() ?? 0;
            var nick = item?["nickname"]?.GetValue<string>() ?? string.Empty;
            if (id != 0)
            {
                list.Add((id, nick));
            }
        }

        return list;
    }

    /// <summary>兜底动作调用（协议端握手后的自检，例如获取登录号信息）。</summary>
    public async Task<long?> GetSelfIdAsync(CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_login_info", "{}", ct);
        var id = result?["data"]?["user_id"]?.GetValue<long>();
        if (id is not null)
        {
            _selfId = id.Value;
        }

        return id;
    }

    /// <summary>
    /// 询问协议端：QQ 账号是否在线。null = 协议端没给明确答复（不支持该动作/超时），
    /// 调用方应**保持原状态**，不要误报。
    ///
    /// 为什么需要它：**OneBot 的 WebSocket 连着不代表 QQ 账号在线**。
    /// 被顶号或登录失效时连接照旧、`get_login_info` 还会回显配置里的 UIN，
    /// 于是面板一直显示“已连接”，消息却一条都收不到 —— 极难排查。
    /// </summary>
    public async Task<bool?> GetAccountOnlineAsync(CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_status", "{}", ct);
        var node = result?["data"]?["online"];
        if (node is null)
        {
            return null;
        }

        try
        {
            return node.GetValue<bool>();
        }
        catch
        {
            return null; // online 不是布尔（某些实现返回字符串）→ 当未知处理
        }
    }

    private async Task<JsonNode?> SendActionAsync(string action, string paramsJson, CancellationToken ct)
    {
        var transport = _transport;
        if (transport is null)
        {
            return null;
        }

        var echo = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _pending[echo] = tcs;
        }

        try
        {
            await transport.SendActionAsync(action, paramsJson, echo, ct);
            var done = await Task.WhenAny(tcs.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
            if (done != tcs.Task)
            {
                lock (_gate)
                {
                    _pending.Remove(echo);
                }

                return null;
            }

            return await tcs.Task;
        }
        catch
        {
            lock (_gate)
            {
                _pending.Remove(echo);
            }

            return null;
        }
    }

    private void OnTransportStateChanged(bool connected)
    {
        SetConnected(connected);
        if (connected)
        {
            _ = GetSelfIdAsync();
        }
    }

    private void OnTransportText(string json)
    {
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(json);
        }
        catch
        {
            return;
        }

        if (root is null)
        {
            return;
        }

        var postType = root["post_type"]?.GetValue<string>();
        if (postType is not null)
        {
            if (postType == "message")
            {
                // 有合并转发时要去发 get_forward_msg 取内容，这一步必须异步 ——
                // 在接收线程上同步阻塞会死锁：动作响应要回到同一条接收泵才能读到。
                // 但**普通消息绝不能异步**：线程池调度顺序 ≠ 到达顺序，
                // 实测会把“第6句”排在“第11句”后面（会话序号、上下文全跟着乱）。
                // 所以只有带转发的消息才丢到后台去，其余照旧同步处理（其中不含任何 await 动作）。
                if (HasForwardSegment(root))
                {
                    _ = Task.Run(() => HandleMessageEventAsync(root));
                }
                else
                {
                    HandleMessageEventCoreAsync(root).GetAwaiter().GetResult();
                }
            }
            else if (postType == "notice")
            {
                HandleNoticeEvent(root);
            }

            return;
        }

        var echo = root["echo"]?.GetValue<string>();
        if (echo is not null)
        {
            TaskCompletionSource<JsonNode?>? tcs;
            lock (_gate)
            {
                if (_pending.Remove(echo, out var found))
                {
                    tcs = found;
                }
                else
                {
                    tcs = null;
                }
            }

            tcs?.TrySetResult(root);
        }
    }

    /// <summary>
    /// 处理 notice 事件 —— 目前只关心戳一戳。
    /// 格式在不同协议端/不同版本里两种都有，所以两种写法都收：
    ///   ① {post_type:notice, notice_type:notify, sub_type:poke, user_id, target_id}
    ///   ② {post_type:notice, notice_type:poke, sub_type:poke, user_id, target_id}
    /// user_id = 发起戳的人，target_id = 被戳的人（等于 self_id 就是戳了机器人）。
    /// </summary>
    private void HandleNoticeEvent(JsonNode root)
    {
        var noticeType = root["notice_type"]?.GetValue<string>();
        var subType = root["sub_type"]?.GetValue<string>();

        // 撤回：group_recall（群）/ friend_recall（私聊）。
        // 这两种事件的 message_id 在不同协议端有 number/string 两种写法，两种都收。
        if (noticeType is "group_recall" or "friend_recall")
        {
            HandleRecallEvent(root, noticeType == "group_recall");
            return;
        }

        var isPoke = noticeType == "poke" || (noticeType == "notify" && subType == "poke");
        if (!isPoke)
        {
            return;
        }

        var selfId = root["self_id"]?.GetValue<long>() ?? _selfId;
        if (selfId == 0)
        {
            selfId = SelfIdHint;
        }

        var userId = root["user_id"]?.GetValue<long>() ?? 0;
        var targetId = root["target_id"]?.GetValue<long>() ?? 0;
        var groupId = root["group_id"]?.GetValue<long>() ?? 0;

        // raw_info 兜底：有的版本把真正的 user_id/target_id 藏在里面
        if (root["raw_info"] is JsonArray rawInfo)
        {
            foreach (var item in rawInfo)
            {
                var kind = item?["type"]?.GetValue<string>();
                if (kind == "poke")
                {
                    userId = item?["user_id"]?.GetValue<long>() ?? userId;
                    targetId = item?["target_id"]?.GetValue<long>() ?? targetId;
                }
                else if (kind == "group" && groupId == 0)
                {
                    groupId = item?["group_id"]?.GetValue<long>() ?? 0;
                }
            }
        }

        if (userId <= 0 || targetId <= 0)
        {
            return;
        }

        var time = root["time"]?.GetValue<long>() ?? DateTimeOffset.Now.ToUnixTimeSeconds();
        Poked?.Invoke(new QqPokeEvent(
            groupId > 0,
            groupId,
            userId,
            targetId,
            selfId != 0 && targetId == selfId,
            DateTimeOffset.FromUnixTimeSeconds(time)));
    }

    /// <summary>
    /// 撤回事件（OneBot v11）：
    ///   group_recall : { group_id, user_id（原发送者）, operator_id（动手的人）, message_id }
    ///   friend_recall: { user_id, message_id }（私聊，没有 group_id/operator_id）
    /// 拿不到 message_id 就当没看见 —— 没有它就无法定位是哪一条，硬猜只会标记错消息。
    /// </summary>
    private void HandleRecallEvent(JsonNode root, bool isGroup)
    {
        var messageId = ReadLong(root, "message_id");
        if (messageId <= 0)
        {
            Log("收到撤回事件但没有 message_id，已忽略（无法定位是哪一条）");
            return;
        }

        var time = root["time"]?.GetValue<long>() ?? DateTimeOffset.Now.ToUnixTimeSeconds();
        MessageRecalled?.Invoke(new QqRecallEvent(
            isGroup,
            ReadLong(root, "group_id"),
            ReadLong(root, "user_id"),
            ReadLong(root, "operator_id"),
            messageId,
            DateTimeOffset.FromUnixTimeSeconds(time)));
    }

    /// <summary>数字字段兼容 number / string 两种写法（协议端版本差异），取不到返回 0。</summary>
    private static long ReadLong(JsonNode root, string name)
    {
        var node = root[name];
        return node switch
        {
            null => 0,
            JsonValue value when value.TryGetValue<long>(out var parsed) => parsed,
            JsonValue value when long.TryParse(value.ToString(), out var parsed) => parsed,
            _ => 0
        };
    }

    private async Task HandleMessageEventAsync(JsonNode root)
    {
        try
        {
            await HandleMessageEventCoreAsync(root);
        }
        catch (Exception ex)
        {
            Log("处理消息事件出错: " + ex.Message);
        }
    }

    /// <summary>这条消息里有没有合并转发段（决定要不要走异步取内容）。</summary>
    private static bool HasForwardSegment(JsonNode root)
    {
        if (root["message"] is not JsonArray segments)
        {
            return root["raw_message"]?.GetValue<string>()?.Contains("[CQ:forward", StringComparison.Ordinal) == true;
        }

        foreach (var seg in segments)
        {
            if (seg?["type"]?.GetValue<string>() == "forward")
            {
                return true;
            }
        }

        return false;
    }

    private async Task HandleMessageEventCoreAsync(JsonNode root)
    {
        var messageType = root["message_type"]?.GetValue<string>();
        if (messageType is not "private" and not "group")
        {
            return;
        }

        var isGroup = messageType == "group";
        var userId = root["user_id"]?.GetValue<long>() ?? 0;
        var groupId = root["group_id"]?.GetValue<long>() ?? 0;
        var messageId = root["message_id"]?.GetValue<long>() ?? 0;
        // self_id 兜底：NapCat 有时不返回该字段，用登录账号识别 @
        var selfId = root["self_id"]?.GetValue<long>() ?? _selfId;
        if (selfId == 0)
        {
            selfId = SelfIdHint;
        }
        var time = root["time"]?.GetValue<long>() ?? DateTimeOffset.Now.ToUnixTimeSeconds();

        var sender = root["sender"] is JsonObject so
            ? new OneBotSender
            {
                UserId = so["user_id"]?.GetValue<long>() ?? userId,
                Nickname = so["nickname"]?.GetValue<string>(),
                Card = so["card"]?.GetValue<string>(),
                Role = so["role"]?.GetValue<string>(),
                Title = so["title"]?.GetValue<string>()
            }
            : null;

        var raw = root["raw_message"]?.GetValue<string>();

        // 合并转发：内容不在消息里，要另发一个 get_forward_msg 去取（可能好几条，先全部取回来）
        var forwards = await FetchForwardRecordsAsync(root["message"] as JsonArray, CancellationToken.None);

        var (text, mentioned, imageUrls, musicShares) = ParseMessage(root["message"] as JsonArray, raw, selfId, forwards);
        var (replyToId, replyPreview, replySenderId) = ParseReply(root["message"] as JsonArray, raw);

        var senderName = sender is null
            ? (isGroup ? $"成员 {userId}" : userId.ToString())
            : isGroup
                ? (string.IsNullOrWhiteSpace(sender.Card) ? sender.Nickname ?? userId.ToString() : sender.Card)
                : sender.Nickname ?? userId.ToString();

        // 日志里记一笔“这条是引用回复”—— 真出问题时（引到哪、有没有认出）不用猜。
        // 放在早退之前：**只点“回复”不写正文**的消息也得留痕（号主就是这么测的：正文是一个空格）
        var hasReply = replyToId is not null || !string.IsNullOrWhiteSpace(replyPreview);
        if (hasReply)
        {
            Log(replyToId is long quotedId
                ? $"引用回复：消息 {messageId} → 引用了 {quotedId}" +
                  (replyPreview is null ? "（段里没带摘要，从上下文找）" : $"（段里带了摘要：{Truncate(replyPreview, 30)}）")
                : $"引用回复：消息 {messageId} → reply 段里没有可用的 id（原始片段：{Truncate(raw ?? string.Empty, 80)}）");
        }

        // 只有表情/图片等无文本且未提及本机、**也没有引用目标**时才不建会话。
        // （以前只看“正文是否为空”，于是“引用某条 + 正文为空”的消息被静默丢掉：
        //   机器人既没记下这条、也没标出它引用了什么 —— 号主反馈“识别不了引用回复”的一个真原因）
        if (string.IsNullOrWhiteSpace(text) && !mentioned && musicShares.Count == 0 && !hasReply)
        {
            return;
        }

        MessageReceived?.Invoke(new QqChatMessage(
            messageId,
            isGroup,
            userId,
            groupId,
            senderName,
            text,
            DateTimeOffset.FromUnixTimeSeconds(time),
            mentioned,
            imageUrls.Count > 0 ? imageUrls : null,
            musicShares.Count > 0 ? musicShares : null,
            sender?.Role,
            sender?.Title,
            replyToId,
            replyPreview,
            replySenderId));
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 取“引用回复”的目标：QQ 的回复会带一个 reply 段（数组形式）或 [CQ:reply,id=…]（CQ 码形式）。
    /// 以前两种都被当成“不认识的段”丢掉 —— 模型只看到一句“我也是”，不知道在回哪条（handoff-4 §27）。
    /// 字段兼容：id / message_id 两种写法都收；有的实现还会直接带 text/summary 摘要，带了就用；
    /// 被引用者的 QQ 号（qq / user_id / sender_id 三种写法）也一并收下 —— 2026-09-19 修“引用机器人的消息被吞”：
    /// 光看本地“我发过哪些”表（内存、上限 200、重启清空）会在部署重启后认不出“他是在回我”。
    /// </summary>
    private static (long? Id, string? Preview, long? SenderId) ParseReply(JsonArray? segments, string? rawMessage)
    {
        if (segments is not null)
        {
            foreach (var seg in segments)
            {
                if (seg?["type"]?.GetValue<string>() != "reply")
                {

                    continue;
                }

                var data = seg["data"];
                var id = ParseId(data?["id"]?.GetValue<string>() ?? data?["message_id"]?.GetValue<string>());
                var preview = data?["text"]?.GetValue<string>() ?? data?["summary"]?.GetValue<string>();
                var quotedSender = ParseId(data?["qq"]?.GetValue<string>()
                                           ?? data?["user_id"]?.GetValue<string>()
                                           ?? data?["sender_id"]?.GetValue<string>());
                return (id, string.IsNullOrWhiteSpace(preview) ? null : preview.Trim(), quotedSender);
            }
        }

        if (rawMessage is null)
        {
            return (null, null, null);
        }

        var marker = rawMessage.IndexOf("[CQ:reply,", StringComparison.Ordinal);
        if (marker < 0)
        {
            return (null, null, null);
        }

        var end = rawMessage.IndexOf(']', marker);
        if (end < 0)
        {
            return (null, null, null);
        }

        var args = rawMessage[(marker + 10)..end];
        return (ParseId(GetArg(args, "id")), null, ParseId(GetArg(args, "qq")));
    }

    /// <summary>
    /// 按消息 id 取回（纯文本、发送者 QQ）：引用原文本地找不到时的兜底。
    /// 调用方不能在接收循环里等它（会死锁）—— 机器人是拿它**事后补写**到那条消息上的。
    /// </summary>
    public async Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
    {
        var result = await SendActionAsync("get_msg", $"{{\"message_id\":{messageId}}}", ct);
        var data = result?["data"];
        if (data is null)
        {
            return (null, 0);
        }

        // 发送者：NapCat 给 data.sender.user_id，有的实现直接给 data.user_id。
        // 注意这些字段在真实报文里是**数字**（不是字符串）——直接 GetValue<string>() 会抛
        // “An element of type 'Number' cannot be converted to a 'System.String'”，
        // 整个兜底静默失败（2026-09-19 测试拓出来的真 bug）。
        var senderId = ParseId(NodeAsText(data["sender"]?["user_id"]) ?? NodeAsText(data["user_id"])) ?? 0;

        // 正文：优先拼 text 段（数组形式），拿不到再用 raw_message / message 字符串形式
        var text = new System.Text.StringBuilder();
        if (data["message"] is JsonArray segments)
        {
            foreach (var seg in segments)
            {
                if (seg?["type"]?.GetValue<string>() == "text")
                {
                    text.Append(NodeAsText(seg["data"]?["text"]));
                }
            }
        }

        if (text.Length == 0)
        {
            text.Append(NodeAsText(data["raw_message"]) ?? (data["message"] is JsonArray ? null : NodeAsText(data["message"])));
        }

        // 有些实现只给 CQ 码字符串（raw_message）：直接当正文会剩下
        // “image,file=0CC3F826…” 这种尾巴（2026-09-19 线上日志里看见过），
        // 用现成的 StripCqCode 转成 [图片]/@某人 这种可读写法。
        var body = StripCqCode(text.ToString(), _selfId, out _).Trim();
        return (body.Length == 0 ? null : body, senderId);
    }

    /// <summary>把 JsonNode 当文本读（数字/字符串都收）—— 协议端字段类型不稳定，直接 GetValue&lt;string&gt;() 会抛。</summary>
    private static string? NodeAsText(JsonNode? node)
    {
        switch (node)
        {
            case null:
                return null;
            case JsonValue value when value.TryGetValue<string>(out var s):
                return s;
            case JsonValue value when value.TryGetValue<long>(out var l):
                return l.ToString();
            case JsonValue value when value.TryGetValue<double>(out var d):
                return ((long)d).ToString();
            case JsonValue value when value.TryGetValue<bool>(out var b):
                return b ? "true" : "false";
            default:
                return node.ToJsonString();
        }
    }

    /// <summary>消息 id 解析：字符串/数字都收；拿不到就 null。</summary>
    private static long? ParseId(string? raw)
        => long.TryParse((raw ?? string.Empty).Trim(), out var id) && id > 0 ? id : null;

    /// <summary>
    /// 取合并转发的内容：把消息里所有 forward 段逐个发 get_forward_msg。
    /// 失败就当没有（渲染成“[合并转发]”—— 整条消息不能被一条取不回来的转发拖死）。
    /// </summary>
    private async Task<Dictionary<string, JsonNode?>> FetchForwardRecordsAsync(JsonArray? segments, CancellationToken ct)
    {
        var records = new Dictionary<string, JsonNode?>();
        if (segments is null)
        {
            return records;
        }

        foreach (var seg in segments)
        {
            if (seg?["type"]?.GetValue<string>() != "forward")
            {
                continue;
            }

            var id = seg["data"]?["id"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(id) || records.ContainsKey(id))
            {
                continue;
            }

            try
            {
                var result = await SendActionAsync("get_forward_msg", $"{{\"id\":\"{id}\"}}", ct);
                // 注意：SendActionAsync 返回的是整个响应（status/retcode/data），内容在 data 里
                var count = (result?["data"]?["messages"] as JsonArray)?.Count ?? 0;
                records[id] = result?["data"];
                Log($"转发聊天记录 id={id} → {count} 条");
            }
            catch (Exception ex)
            {
                Log($"取转发聊天记录失败（id={id}）: {ex.Message}");
                records[id] = null;
            }
        }

        return records;
    }

    /// <summary>解析消息成纯文本；数组段（text/at/image…）或 raw_message（CQ 码）均支持。</summary>
    private static (string Text, bool Mentioned, List<string> ImageUrls, List<MusicShare> MusicShares) ParseMessage(
        JsonArray? segments, string? rawMessage, long selfId, Dictionary<string, JsonNode?>? forwards = null)
    {
        var sb = new System.Text.StringBuilder();
        var mentioned = false;
        var imageUrls = new List<string>();
        var musicShares = new List<MusicShare>();

        if (segments is not null && segments.Count > 0)
        {
            foreach (var seg in segments)
            {
                var type = seg?["type"]?.GetValue<string>();
                var data = seg?["data"];
                switch (type)
                {
                    case "text":
                        sb.Append(data?["text"]?.GetValue<string>());
                        break;
                    case "at":
                        var atId = data?["qq"]?.GetValue<string>();
                        if (atId == selfId.ToString() || atId == "all")
                        {
                            mentioned = true;
                        }

                        sb.Append(atId == "all" ? "@全体成员 " : $"@{atId} "); // 保留 @ 目标，消息内容不丢
                        break;
                    case "image":
                        sb.Append("[图片]");
                        var imgUrl = data?["url"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(imgUrl))
                        {
                            imgUrl = data?["file"]?.GetValue<string>();
                        }

                        if (!string.IsNullOrWhiteSpace(imgUrl) && (imgUrl.StartsWith("http://") || imgUrl.StartsWith("https://")))
                        {
                            imageUrls.Add(imgUrl);
                        }

                        break;
                    case "face":
                        // 原生小表情：带上名字（id 对照表来自 NapCat 自己的 face_config.json）
                        var faceName = QqFaceCatalog.Describe(data?["id"]?.GetValue<string>());
                        sb.Append("[表情:").Append(faceName).Append(']');
                        // 把原始 id 也记一笔：万一 QQ 换了 id 体系（名字就会张冠李戴），
                        // 这条日志是唯一能对出“实际 id ↔ 显示名字”的现场。每次滚动排查完可删。
                        Log($"表情段 id={data?["id"]?.GetValue<string>() ?? "?"} → {faceName}");
                        break;
                    case "mface":
                        // 弹幕/收藏表情：协议端会给一个 summary（如“尴尬”）
                        sb.Append("[动画表情:").Append(FirstNonEmpty(data?["summary"]?.GetValue<string>(), "未命名")).Append(']');
                        break;
                    case "sface":
                    case "bface":
                        sb.Append("[超级表情:").Append(
                            FirstNonEmpty(data?["text"]?.GetValue<string>(), QqFaceCatalog.Describe(data?["id"]?.GetValue<string>()))).Append(']');
                        break;
                    case "dice":
                        sb.Append("[骰子:").Append(FirstNonEmpty(data?["result"]?.GetValue<string>(), "?" )).Append(']');
                        break;
                    case "rps":
                        sb.Append("[猜拳:").Append(FirstNonEmpty(data?["result"]?.GetValue<string>(), "?" )).Append(']');
                        break;
                    case "poke":
                        sb.Append("[戳一戳]");
                        break;
                    case "record":
                        sb.Append("[语音]");
                        break;
                    case "video":
                        sb.Append("[视频]");
                        break;
                    case "music":
                    case "json":
                        // 音乐分享：OneBot 的 music 段（带平台+id）或 QQ 客户端的新版 json 卡片。
                        // 这两种段以前直接被丢掉，机器人只见一个空消息 —— 现在把歌认出来。
                        // 不是音乐的其他分享卡片（新闻/小程序/链接）走下面的“分享卡片”分支。
                        if (MusicShareParser.TryParseSegment(seg) is { } share)
                        {
                            if (IsMusicShare(share))
                            {
                                musicShares.Add(share);
                                sb.Append("[音乐分享:").Append(share.Title ?? share.SongId ?? share.Platform).Append(']');
                            }
                            else
                            {
                                sb.Append("[分享卡片:").Append(share.Title ?? "未命名");
                                if (!string.IsNullOrWhiteSpace(share.Artist))
                                {
                                    sb.Append(' ').Append(share.Artist);
                                }

                                sb.Append(']');
                                if (!string.IsNullOrWhiteSpace(share.PageUrl))
                                {
                                    sb.Append(' ').Append(share.PageUrl);
                                }
                            }
                        }

                        break;

                    case "file":
                        var fileName = data?["name"]?.GetValue<string>() ?? data?["file"]?.GetValue<string>();
                        sb.Append("[文件:").Append(string.IsNullOrWhiteSpace(fileName) ? "未命名" : fileName).Append(']');
                        break;

                    case "forward":
                        // 合并转发的聊天记录：内容在 get_forward_msg 里，展开成正文
                        var forwardId = data?["id"]?.GetValue<string>();
                        var record = forwardId is not null && forwards is not null && forwards.TryGetValue(forwardId, out var node)
                            ? ForwardRecord.Render(node, 20, 1200)
                            : null;
                        sb.Append(' ').Append(record ?? "[合并转发的聊天记录，内容未能取回]");
                        break;

                    case "reply":
                        // 引用回复：这里不拼正文（“在回哪条”由 BotAgent 查上下文后标成 [回复 X「…」]）——
                        // 目标 id 由 ParseReply 单独取（那里同时兼顾数组段与 CQ 码两种形式）。
                        break;
                    default:
                        break;
                }
            }

            var text = sb.ToString().Trim();
            if (musicShares.Count == 0 && MusicShareParser.TryParseText(text) is { } fromText)
            {
                // 手动粘的分享链接（网易云短链/长链）也算
                musicShares.Add(fromText);
            }

            return (text, mentioned, imageUrls, musicShares);
        }

        if (rawMessage is not null)
        {
            var text = StripCqCode(rawMessage, selfId, out mentioned, forwards);
            var sharesFromRaw = new List<MusicShare>();
            if (MusicShareParser.TryParseText(rawMessage) is { } fromRaw)
            {
                sharesFromRaw.Add(fromRaw);
            }

            return (text, mentioned, imageUrls, sharesFromRaw);
        }

        return (string.Empty, false, imageUrls, musicShares);
    }

    /// <summary>这张卡片是“歌”还是普通分享链接（决定走听音乐还是走链接预览）。</summary>
    private static bool IsMusicShare(MusicShare share)
        => share.SongId is not null || share.DirectAudioUrl is not null ||
           share.Platform is "netease" or "qq" or "kugou" or "kuwo" or "migu";

    /// <summary>把 CQ 码文本转成可读文本：[CQ:at,qq=123] 保留 @ 目标，图片/表情等替换为占位。</summary>
    private static string StripCqCode(string raw, long selfId, out bool mentioned, Dictionary<string, JsonNode?>? forwards = null)
    {
        mentioned = false;
        var result = new System.Text.StringBuilder();
        int i = 0;
        while (i < raw.Length)
        {
            if (raw[i] == '[' && i + 4 < raw.Length && raw.AsSpan(i + 1, 4).SequenceEqual("CQ:".AsSpan()))
            {
                int end = raw.IndexOf(']', i);
                if (end < 0)
                {
                    break;
                }

                var body = raw.Substring(i + 4, end - i - 4);
                var typeEnd = body.IndexOf(',');
                var type = typeEnd < 0 ? body : body[..typeEnd];
                var args = typeEnd < 0 ? string.Empty : body[(typeEnd + 1)..];

                switch (type)
                {
                    case "at":
                        var atId = GetArg(args, "qq");
                        if (atId == selfId.ToString() || atId == "all")
                        {
                            mentioned = true;
                        }
                        else
                        {
                            result.Append(@"@").Append(atId);
                        }

                        break;
                    case "image":
                        result.Append("[图片]");
                        break;
                    case "face":
                        result.Append("[表情:").Append(QqFaceCatalog.Describe(GetArg(args, "id"))).Append(']');
                        break;
                    case "mface":
                        result.Append("[动画表情:").Append(FirstNonEmpty(GetArg(args, "summary"), "未命名")).Append(']');
                        break;
                    case "sface":
                    case "bface":
                        result.Append("[超级表情:").Append(
                            FirstNonEmpty(GetArg(args, "text"), QqFaceCatalog.Describe(GetArg(args, "id")))).Append(']');
                        break;
                    case "dice":
                        result.Append("[骰子:").Append(FirstNonEmpty(GetArg(args, "result"), "?" )).Append(']');
                        break;
                    case "rps":
                        result.Append("[猜拳:").Append(FirstNonEmpty(GetArg(args, "result"), "?" )).Append(']');
                        break;
                    case "poke":
                        result.Append("[戳一戳]");
                        break;
                    case "record":
                        result.Append("[语音]");
                        break;
                    case "video":
                        result.Append("[视频]");
                        break;
                    case "file":
                        result.Append("[文件:").Append(FirstNonEmpty(GetArg(args, "name"), GetArg(args, "file"), "未命名")).Append(']');
                        break;
                    case "music":
                        result.Append("[音乐分享]");
                        break;
                    case "forward":
                        // CQ 码形式的合并转发：id 取出来就能查内容
                        var cqForwardId = GetArg(args, "id");
                        var cqRecord = cqForwardId.Length > 0 && forwards is not null && forwards.TryGetValue(cqForwardId, out var cqNode)
                            ? ForwardRecord.Render(cqNode, 20, 1200)
                            : null;
                        result.Append(' ').Append(cqRecord ?? "[合并转发的聊天记录，内容未能取回]");
                        break;
                    case "json":
                        result.Append("[分享卡片]");
                        break;
                }

                i = end + 1;
            }
            else
            {
                result.Append(raw[i]);
                i++;
            }
        }

        return result.ToString().Trim();
    }

    private static string GetArg(string args, string key)
    {
        var sb = new System.Text.StringBuilder();
        int i = 0;
        while (i < args.Length)
        {
            if (args[i] == key[0] &&
                (i == 0 || args[i - 1] == ',') &&
                i + key.Length < args.Length + 1 &&
                args.AsSpan(i, key.Length).SequenceEqual(key.AsSpan()) &&
                i + key.Length < args.Length &&
                args[i + key.Length] == '=')
            {
                i += key.Length + 1;
                while (i < args.Length && args[i] != ',')
                {
                    sb.Append(args[i]);
                    i++;
                }

                return sb.ToString();
            }

            i++;
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string?[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static int GetRetcode(JsonNode node)
    {
        var retcode = node?["retcode"]?.GetValue<int>();
        if (retcode is not null)
        {
            return retcode.Value;
        }

        return node?["status"]?.GetValue<string>() == "ok" ? 0 : -1;
    }

    private static IOneBotTransport CreateTransport(AppSettings settings) => settings.OneBotProtocol switch
    {
        "ReverseWebSocket" => new ReverseWsTransport(settings.OneBotAddress, settings.OneBotToken),
        "Http" => new HttpTransport(settings.OneBotAddress, settings.OneBotToken),
        _ => new ForwardWsTransport(settings.OneBotAddress, settings.OneBotToken)
    };

    private void SetConnected(bool connected)
    {
        if (IsConnected == connected)
        {
            return;
        }

        IsConnected = connected;
        StateChanged?.Invoke(connected ? GatewayState.Connected : GatewayState.Disconnected);
        ConnectionChanged?.Invoke(connected);
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);

    /// <summary>日志里截短（动作失败原因可能很长）。</summary>
    private static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static void Log(string message) =>
        FileLog.Write("OneBot", message);
}