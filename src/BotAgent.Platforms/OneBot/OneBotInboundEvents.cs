using System.Text.Json;
using BotAgent.Domain.Ports;
using System.Text.Json.Nodes;
using BotAgent.Services.Music;
using BotAgent.Services.Qq;
using BotAgent.Domain.Qq;
using BotAgent.Services.Resilience;
using BotAgent.Services.Platforms;

namespace BotAgent.Services.OneBot;

/// <summary>
/// 入站事件面：把协议端推上来的一条条报文翻译成领域事件。
///
/// 分出来的理由：这里做的全是"解读"——报文形状兜底（self_id 缺失、戳一戳两种写法、
/// message_id 数字/字符串两种写法、raw_info 兜底）、消息段拼装、合并转发补取，以及
/// "什么样的消息不值得建会话"的判定。它不碰连接生命周期，也不拼动作请求（那是
/// OneBotActionClient / OneBotActions 的事），只消费动作通道去补取转发内容。
///
/// 两条不变量：
/// ① <b>普通消息必须按到达顺序处理</b>：线程池调度顺序 ≠ 到达顺序，实测会把"第 6 句"
///    排到"第 11 句"后面（会话序号、上下文全跟着乱）。所以只有带合并转发的消息才丢后台，
///    其余同步处理（且同步路径上不得有 await 动作）。
/// ② 不上报无意义的消息：空文本、没被提及、没有音乐卡片、也不是引用回复的消息直接丢弃。
/// </summary>
internal sealed class OneBotInboundEvents
{
    private readonly OneBotActionClient _client;
    private readonly PlatformOperationLifetime _operations;
    private readonly Func<long> _selfId;
    private readonly Func<long> _selfIdHint;
    private readonly Action<QqChatMessage> _messageReceived;
    private readonly Action<QqPokeEvent> _poked;
    private readonly Action<QqRecallEvent> _messageRecalled;
    private readonly Action<string> _log;

    public OneBotInboundEvents(
        OneBotActionClient client,
        PlatformOperationLifetime operations,
        Func<long> selfId,
        Func<long> selfIdHint,
        Action<QqChatMessage> messageReceived,
        Action<QqPokeEvent> poked,
        Action<QqRecallEvent> messageRecalled,
        Action<string> log)
    {
        _client = client;
        _operations = operations;
        _selfId = selfId;
        _selfIdHint = selfIdHint;
        _messageReceived = messageReceived;
        _poked = poked;
        _messageRecalled = messageRecalled;
        _log = log;
    }

    public void HandleTransportText(string json)
    {
        if (!_operations.IsActive) return;
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
                if (OneBotInboundParser.HasForwardSegment(root))
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
            _client.Complete(echo, root);
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

        var selfId = root["self_id"]?.GetValue<long>() ?? _selfId();
        if (selfId == 0)
        {
            selfId = _selfIdHint();
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

        var time = root["time"]?.GetValue<long>() ?? Clock.Now.ToUnixTimeSeconds();
        _poked(new QqPokeEvent(
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
        var messageId = OneBotInboundParser.ReadLong(root, "message_id");
        if (messageId <= 0)
        {
            _log("收到撤回事件但没有 message_id，已忽略（无法定位是哪一条）");
            return;
        }

        var time = root["time"]?.GetValue<long>() ?? Clock.Now.ToUnixTimeSeconds();
        _messageRecalled(new QqRecallEvent(
            isGroup,
            OneBotInboundParser.ReadLong(root, "group_id"),
            OneBotInboundParser.ReadLong(root, "user_id"),
            OneBotInboundParser.ReadLong(root, "operator_id"),
            messageId,
            DateTimeOffset.FromUnixTimeSeconds(time)));
    }

    private async Task HandleMessageEventAsync(JsonNode root)
    {
        try
        {
            await HandleMessageEventCoreAsync(root);
        }
        catch (Exception ex)
        {
            _log("处理消息事件出错: " + ex.Message);
        }
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
        var selfId = root["self_id"]?.GetValue<long>() ?? _selfId();
        if (selfId == 0)
        {
            selfId = _selfIdHint();
        }
        var time = root["time"]?.GetValue<long>() ?? Clock.Now.ToUnixTimeSeconds();

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

        var (text, mentioned, imageUrls, musicShares) =
            OneBotInboundParser.ParseMessage(root["message"] as JsonArray, raw, selfId, forwards);
        var (replyToId, replyPreview, replySenderId) =
            OneBotInboundParser.ParseReply(root["message"] as JsonArray, raw);

        var senderName = sender is null
            ? (isGroup ? $"成员 {userId}" : userId.ToString())
            : isGroup
                ? (string.IsNullOrWhiteSpace(sender.Card) ? sender.Nickname ?? userId.ToString() : sender.Card)
                : sender.Nickname ?? userId.ToString();

        // 日志里记一笔“这条是引用回复”—— 真出问题时（引到哪、有没有认出）不用猜。
        // 放在早退之前：**只点“回复”不写正文**的消息也得留痕（管理员就是这么测的：正文是一个空格）
        var hasReply = replyToId is not null || !string.IsNullOrWhiteSpace(replyPreview);
        if (hasReply)
        {
            _log(replyToId is long quotedId
                ? $"引用回复：消息 {messageId} → 引用了 {quotedId}" +
                  (replyPreview is null ? "（段里没带摘要，从上下文找）" : $"（段里带了摘要：{OneBotInboundParser.Truncate(replyPreview, 30)}）")
                : $"引用回复：消息 {messageId} → reply 段里没有可用的 id（原始片段：{OneBotInboundParser.Truncate(raw ?? string.Empty, 80)}）");
        }

        // 只有表情/图片等无文本且未提及本机、**也没有引用目标**时才不建会话。
        // （以前只看“正文是否为空”，于是“引用某条 + 正文为空”的消息被静默丢掉：
        //   机器人既没记下这条、也没标出它引用了什么 —— 管理员反馈“识别不了引用回复”的一个真原因）
        if (string.IsNullOrWhiteSpace(text) && !mentioned && musicShares.Count == 0 && !hasReply)
        {
            return;
        }

        _messageReceived(new QqChatMessage(
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
                var result = await _client.SendAsync("get_forward_msg", $"{{\"id\":\"{id}\"}}", ct);
                // 注意：动作通道返回的是整个响应（status/retcode/data），内容在 data 里
                var count = (result?["data"]?["messages"] as JsonArray)?.Count ?? 0;
                records[id] = result?["data"];
                _log($"转发聊天记录 id={id} → {count} 条");
            }
            catch (Exception ex)
            {
                _log($"取转发聊天记录失败（id={id}）: {ex.Message}");
                records[id] = null;
            }
        }

        return records;
    }
}
