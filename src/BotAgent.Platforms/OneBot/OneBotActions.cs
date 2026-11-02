using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;
using BotAgent.Services.Qq;
using BotAgent.Services.Resilience;

namespace BotAgent.Services.OneBot;

/// <summary>
/// 这条通道上的"动作面"：发文本 / 图片 / 音乐卡片 / 语音 / 戳一戳，以及协议端的管理动作（点赞、撤回、禁言…）。
///
/// 从 `OneBotGateway` 里搬出来，是因为动作面只依赖"能把一个动作名 + JSON 参数发出去并等回执"，
/// 与连接生命周期、入站事件解析没有交集；网关只保留 <see cref="IQqActions" /> 的转发入口。
///
/// 三条语义原样保留（都写在调用点上，别改）：
/// ① 风控退避在**发之前**判断，被退避的动作直接返回失败且不写协议端；
/// ② 失败只观察一次（`ObserveMessageSendFailure`），日志里带 action 与协议端措辞；
/// ③ 消息 id 支持数字与字符串两种写法（`ReadMessageId`），引用回复靠它对上号。
/// </summary>
internal sealed class OneBotActions
{
    private readonly ProtocolRiskBackoff? _riskBackoff;
    private readonly OneBotActionClient _client;
    private readonly Action<string> _log;

    public OneBotActions(ProtocolRiskBackoff? riskBackoff, OneBotActionClient client, Action<string> log)
    {
        _riskBackoff = riskBackoff;
        _client = client;
        _log = log;
    }

    private string? _pokeUnsupported;

    public async Task<SendResult> SendTextAsync(bool isGroup, long targetId, string text, CancellationToken ct = default, long? replyToMessageId = null, bool directAddress = false)
    {
        var sourceKey = Channels.Key(Channels.Private, isGroup, targetId);
        if (_riskBackoff is not null)
        {
            var decision = _riskBackoff.EvaluateText(sourceKey, directAddress, text);
            if (!decision.Allowed)
            {
                _log($"协议端风控退避中，跳过文本发送（会话={Channels.Describe(sourceKey)}，原因={decision.ReasonCode}）");
                return new SendResult(false);
            }

            text = decision.Text;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        // 带"回复"引用原消息（触发 QQ 回复语句功能）
        string messageJson = replyToMessageId is long rid
            ? $"[{{\"type\":\"reply\",\"data\":{{\"id\":{rid}}}}},{{\"type\":\"text\",\"data\":{{\"text\":{Json(text)}}}}}]"
            : Json(text);
        var result = await _client.SendAsync(action, $"{{\"{key}\":{targetId},\"message\":{messageJson}}}", ct);
        var ok = result is not null && OneBotInboundParser.GetRetcode(result) == 0;
        if (!ok)
        {
            ObserveMessageSendFailure(isGroup, targetId, action, result);
        }
        // 协议端会在 data.message_id 里回新消息的 id（数字或字符串两种写法都见过）：
        // 记下它，别人引用回复机器人那句话时才能对上号（见 handoff-4 §27）。
        return new SendResult(ok, ok ? ReadMessageId(result) : 0);
    }

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

    public async Task<bool> SendMusicAsync(bool isGroup, long targetId, string platform, string songId, string title = "", CancellationToken ct = default)
    {
        // title 只在官方通道（发不了卡片）用得上，这里不接卡片以外的东西。
        if (string.IsNullOrWhiteSpace(songId))
        {
            return false;
        }

        if (_riskBackoff?.IsActive(Channels.Key(Channels.Private, isGroup, targetId)) == true)
        {
            _log($"协议端风控中，跳过音乐发送（会话={Channels.Describe(Channels.Key(Channels.Private, isGroup, targetId))}）");
            return false;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        var music = $"{{\"type\":\"music\",\"data\":{{\"type\":{Json(platform)},\"id\":{Json(songId)}}}}}";
        var result = await _client.SendAsync(action, $"{{\"{key}\":{targetId},\"message\":[{music}]}}", ct);
        var code = result is null ? -999 : OneBotInboundParser.GetRetcode(result);
        if (code != 0)
        {
            ObserveMessageSendFailure(isGroup, targetId, action, result);
            // 把上游原话打出来：NapCat/NTQQ 各版本对 music 段的接受程度不一样，
            // 这段日志是判断“到底是不支持还是参数不对”的唯一依据
            _log($"music 段发送失败 retcode={code}（platform={platform}, id={songId}）：{result?.ToJsonString() ?? "(无响应，可能超时)"}");
        }

        return code == 0;
    }

    public async Task<bool> SendVoiceAsync(bool isGroup, long targetId, string audioUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(audioUrl))
        {
            return false;
        }

        if (_riskBackoff?.IsActive(Channels.Key(Channels.Private, isGroup, targetId)) == true)
        {
            _log($"协议端风控退避中，跳过语音发送（会话={Channels.Describe(Channels.Key(Channels.Private, isGroup, targetId))}）");
            return false;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        var record = $"{{\"type\":\"record\",\"data\":{{\"file\":{Json(audioUrl)}}}}}";
        var result = await _client.SendAsync(action, $"{{\"{key}\":{targetId},\"message\":[{record}]}}", ct);
        var code = result is null ? -999 : OneBotInboundParser.GetRetcode(result);
        if (code != 0)
        {
            ObserveMessageSendFailure(isGroup, targetId, action, result);
            _log($"record 段发送失败 retcode={code}（url={audioUrl}）：{result?.ToJsonString() ?? "(无响应，可能超时)"}");
        }

        return code == 0;
    }

    public async Task<bool> SendImageAsync(bool isGroup, long targetId, byte[] data, CancellationToken ct = default, long? replyToMessageId = null)
    {
        if (data.Length == 0)
        {
            return false;
        }

        if (_riskBackoff?.IsActive(Channels.Key(Channels.Private, isGroup, targetId)) == true)
        {
            _log($"协议端风控中，跳过图片发送（会话={Channels.Describe(Channels.Key(Channels.Private, isGroup, targetId))}）");
            return false;
        }

        var action = isGroup ? "send_group_msg" : "send_private_msg";
        var key = isGroup ? "group_id" : "user_id";
        var image = $"{{\"type\":\"image\",\"data\":{{\"file\":\"base64://{Convert.ToBase64String(data)}\"}}}}";
        var messageJson = replyToMessageId is long rid
            ? $"[{{\"type\":\"reply\",\"data\":{{\"id\":{rid}}}}},{image}]"
            : $"[{image}]";

        var result = await _client.SendAsync(action, $"{{\"{key}\":{targetId},\"message\":{messageJson}}}", ct);
        var ok = result is not null && OneBotInboundParser.GetRetcode(result) == 0;
        if (!ok)
        {
            ObserveMessageSendFailure(isGroup, targetId, action, result);
        }

        return ok;
    }

    public async Task<bool> SendPokeAsync(bool isGroup, long targetId, long userId, CancellationToken ct = default)
    {
        if (userId <= 0)
        {
            return false;
        }

        var sourceKey = Channels.Key(Channels.Private, isGroup, targetId);
        if (_riskBackoff?.IsActive(sourceKey) == true)
        {
            _log($"协议端风控中，跳过戳一戳（会话={Channels.Describe(sourceKey)}）");
            return false;
        }

        var (action, paramsJson) = isGroup
            ? ("group_poke", $"{{\"group_id\":{targetId},\"user_id\":{userId}}}")
            : ("friend_poke", $"{{\"user_id\":{userId}}}");

        // 协议端不支持（retcode 1404 / 未知动作）不当作错误刷屏，只记一次日志
        var result = await _client.SendAsync(action, paramsJson, ct);
        var ok = result is not null && OneBotInboundParser.GetRetcode(result) == 0;
        if (!ok)
        {
            ObserveMessageSendFailure(isGroup, targetId, action, result);
        }

        if (!ok && _pokeUnsupported != action)
        {
            _pokeUnsupported = action;
            _log($"{action} 失败：协议端可能不支持戳一戳，以后不再重试该动作");
        }

        return ok;
    }

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

    public Task<bool> SetMessageEmojiLikeAsync(long messageId, string emojiId, CancellationToken ct = default)
        => RunActionAsync("set_msg_emoji_like",
            $"{{\"message_id\":{messageId},\"emoji_id\":{Json(string.IsNullOrWhiteSpace(emojiId) ? "128077" : emojiId)},\"set\":true}}", ct);

    public Task<bool> DeleteMessageAsync(long messageId, CancellationToken ct = default)
        => RunActionAsync("delete_msg", $"{{\"message_id\":{messageId}}}", ct);

    public Task<bool> SetGroupBanAsync(long groupId, long userId, int seconds, CancellationToken ct = default)
        => RunActionAsync("set_group_ban", $"{{\"group_id\":{groupId},\"user_id\":{userId},\"duration\":{Math.Max(0, seconds)}}}", ct);

    public Task<bool> SetGroupKickAsync(long groupId, long userId, bool rejectAdd, CancellationToken ct = default)
        => RunActionAsync("set_group_kick",
            $"{{\"group_id\":{groupId},\"user_id\":{userId},\"reject_add_request\":{(rejectAdd ? "true" : "false")}}}", ct);

    public Task<bool> SetGroupCardAsync(long groupId, long userId, string card, CancellationToken ct = default)
        => RunActionAsync("set_group_card", $"{{\"group_id\":{groupId},\"user_id\":{userId},\"card\":{Json(card ?? string.Empty)}}}", ct);

    public Task<bool> SetGroupNameAsync(long groupId, string groupName, CancellationToken ct = default)
        => RunActionAsync("set_group_name", $"{{\"group_id\":{groupId},\"group_name\":{Json(groupName ?? string.Empty)}}}", ct);

    public Task<bool> SetGroupLeaveAsync(long groupId, bool dismiss, CancellationToken ct = default)
        => RunActionAsync("set_group_leave", $"{{\"group_id\":{groupId},\"is_dismiss\":{(dismiss ? "true" : "false")}}}", ct);

    private async Task<bool> RunActionAsync(string action, string paramsJson, CancellationToken ct)
    {
        var result = await _client.SendAsync(action, paramsJson, ct);
        var ok = result is not null && OneBotInboundParser.GetRetcode(result) == 0;
        if (!ok)
        {
            var code = result is null ? "null" : OneBotInboundParser.GetRetcode(result).ToString();
            var why = result?["wording"]?.GetValue<string>() ?? result?["message"]?.GetValue<string>() ?? result?["msg"]?.GetValue<string>() ?? string.Empty;
            _log($"动作 {action} 失败（retcode={code}{(why.Length > 0 ? $" {OneBotInboundParser.Shorten(why, 60)}" : string.Empty)}）");
        }

        return ok;
    }

    private void ObserveMessageSendFailure(bool isGroup, long targetId, string action, JsonNode? result)
    {
        if (_riskBackoff is null || targetId <= 0)
        {
            return;
        }

        var retcode = result is null ? -1 : OneBotInboundParser.GetRetcode(result);
        var wording = result?["wording"]?.GetValue<string>()
                      ?? result?["message"]?.GetValue<string>()
                      ?? result?["msg"]?.GetValue<string>();
        _riskBackoff.ObserveFailure(Channels.Key(Channels.Private, isGroup, targetId), action, retcode, wording);
    }

    private static string Json(string s) => JsonSerializer.Serialize(s);
}
