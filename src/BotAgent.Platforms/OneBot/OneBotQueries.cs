using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;

namespace BotAgent.Services.OneBot;

/// <summary>
/// 私域通道的"查询面"：拉群/好友列表、群资料与成员资料、群历史消息、消息详情、图片地址重签、收藏表情。
///
/// 与动作面（OneBotActions）分开：查询是只读的，失败返回空/null 由上层兜底；
/// 动作会触发协议端行为、失败要记风控。两者唯一的共同点只是"走同一条动作通道"，
/// 所以都依赖 <see cref="OneBotActionClient" />，彼此没有引用。
///
/// `selfId` 通过委托读网关的当前值：登录号是连接生命周期的一部分（握手后才知道），
/// 查询面只消费它，不持有、也不负责更新。
/// </summary>
internal sealed class OneBotQueries
{
    private readonly OneBotActionClient _client;
    private readonly Func<long> _selfId;
    private readonly Action<string> _log;

    public OneBotQueries(OneBotActionClient client, Func<long> selfId, Action<string> log)
    {
        _client = client;
        _selfId = selfId;
        _log = log;
    }

    /// <summary>
    /// 按消息 id 拿回这条消息里所有图片的**当前**地址（OneBot 的 get_msg）。
    /// 用途：QQ 图片地址带时效 rkey，过期后 CDN 一律 400；而协议端能重新签发一份
    /// （实测：同一条消息重新签发后就能下到）。拿不到就返回空列表。
    /// </summary>
    public async Task<IReadOnlyList<string>> RefreshImageUrlsAsync(long messageId, CancellationToken ct = default)
    {
        var urls = new List<string>();
        var result = await _client.SendAsync("get_msg", $"{{\"message_id\":{messageId}}}", ct);
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
        var result = await _client.SendAsync("fetch_custom_face", $"{{\"count\":{count}}}", ct);

        CollectFaceUrls(result?["data"], urls);

        if (urls.Count == 0)
        {
            // 各版本协议端返回形状不一致（数组 / {urls:[…]} / 字符串），
            // 拿不到就把原始响应的前一段写进日志 —— 下次一看就知道是“不支持”还是“格式变了”。
            var raw = result?.ToJsonString() ?? "(null)";
            _log($"fetch_custom_face 未取到图片地址，原始响应：{(raw.Length <= 260 ? raw : raw[..260] + "…")}");
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
        var result = await _client.SendAsync("get_group_info", $"{{\"group_id\":{groupId}}}", ct);
        return result?["data"]?["group_name"]?.GetValue<string>();
    }

    /// <summary>
    /// 拉群成员资料（身份 + 群头衔）。失败/不支持返回 null。
    /// no_cache=true：身份与头衔是“刚改就得看到”的东西（刚升管理员），缓存不值。
    /// </summary>
    public async Task<GroupMemberInfo?> GetGroupMemberInfoAsync(long groupId, long userId, CancellationToken ct = default)
    {
        var result = await _client.SendAsync("get_group_member_info",
            $"{{\"group_id\":{groupId},\"user_id\":{userId},\"no_cache\":true}}", ct);
        var data = result?["data"];
        if (data is null)
        {
            return null;
        }

        var uid = OneBotInboundParser.ReadLong(data["user_id"]) ?? userId;
        if (uid == 0)
        {
            return null;
        }

        return new GroupMemberInfo(
            uid,
            OneBotInboundParser.ReadLong(data["group_id"]) ?? groupId,
            data["nickname"]?.GetValue<string>(),
            data["card"]?.GetValue<string>(),
            data["role"]?.GetValue<string>(),
            data["title"]?.GetValue<string>(),
            OneBotInboundParser.ReadInt(data["level"]));
    }

    /// <summary>拉取群历史消息（NapCat 扩展动作 get_group_msg_history，返回最新在前的列表）。</summary>
    public async Task<List<QqChatMessage>> GetGroupMsgHistoryAsync(long groupId, int count = 20, CancellationToken ct = default)
    {
        var list = new List<QqChatMessage>();
        var result = await _client.SendAsync("get_group_msg_history", $"{{\"group_id\":{groupId},\"count\":{count},\"message_seq\":0}}", ct);
        var messages = result?["data"]?["messages"] as JsonArray;
        if (messages is null)
        {
            return list;
        }

        var selfId = _selfId();
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
            var (text, mentioned, historyImages, _) = OneBotInboundParser.ParseMessage(root["message"] as JsonArray, raw, selfId);
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
        var result = await _client.SendAsync("get_group_list", "{}", ct);
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
        var result = await _client.SendAsync("get_friend_list", "{}", ct);
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
        var result = await _client.SendAsync("get_status", "{}", ct);
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

    /// <summary>
    /// 按消息 id 取回（纯文本、发送者 QQ）：引用原文本地找不到时的兜底。
    /// 调用方不能在接收循环里等它（会死锁）—— 机器人是拿它**事后补写**到那条消息上的。
    /// </summary>
    public async Task<(string? Text, long SenderId)> GetMessageInfoAsync(long messageId, CancellationToken ct = default)
    {
        var result = await _client.SendAsync("get_msg", $"{{\"message_id\":{messageId}}}", ct);
        var data = result?["data"];
        if (data is null)
        {
            return (null, 0);
        }

        // 发送者：NapCat 给 data.sender.user_id，有的实现直接给 data.user_id。
        // 注意这些字段在真实报文里是**数字**（不是字符串）——直接 GetValue<string>() 会抛
        // “An element of type 'Number' cannot be converted to a 'System.String'”，
        // 整个兜底静默失败（2026-09-19 测试拓出来的真 bug）。
        var senderId = OneBotInboundParser.ParseId(
            OneBotInboundParser.NodeAsText(data["sender"]?["user_id"])
            ?? OneBotInboundParser.NodeAsText(data["user_id"])) ?? 0;

        // 正文：优先拼 text 段（数组形式），拿不到再用 raw_message / message 字符串形式
        var text = new System.Text.StringBuilder();
        if (data["message"] is JsonArray segments)
        {
            foreach (var seg in segments)
            {
                if (seg?["type"]?.GetValue<string>() == "text")
                {
                    text.Append(OneBotInboundParser.NodeAsText(seg["data"]?["text"]));
                }
            }
        }

        if (text.Length == 0)
        {
            text.Append(OneBotInboundParser.NodeAsText(data["raw_message"])
                        ?? (data["message"] is JsonArray ? null : OneBotInboundParser.NodeAsText(data["message"])));
        }

        // 有些实现只给 CQ 码字符串（raw_message）：直接当正文会剩下
        // “image,file=0CC3F826…” 这种尾巴（2026-09-19 线上日志里看见过），
        // 用现成的 StripCqCode 转成 [图片]/@某人 这种可读写法。
        var body = OneBotInboundParser.StripCqCode(text.ToString(), _selfId(), out _).Trim();
        return (body.Length == 0 ? null : body, senderId);
    }
}
