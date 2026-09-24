using System.Net;
using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;
using BotAgent.Services.Local;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// 面板上的**本地通道**（general-agent-platform-plan.md 批次 F）：
///   · `GET /api/local`：这条通有没有开、出箱里有什么（**只有形状**：长度 + 会话 key）；
///   · `POST /api/local/message`：把一条本地消息**注入**成入站消息 —— 它走的是与 QQ 两条上行**完全相同**的链路。
///
/// 两道前置（fail-closed，与面板审批同一个理由：这可能是从"外面"进来的一条消息）：
///   · 本地通道没配名单（`LocalChannelIds` 为空）→ 403 `local_channel_disabled`；
///   · 面板令牌没配 → 403 `panel_token_required`（未配令牌时面板对回环是全开的，这条入口不能顺带被放开）。
///
/// 注意它**不是**"绕过白名单的捷径"：注入之后照样过 <c>WhitelistGate</c>（本地通道有自己的名单，
/// 空 = 全拦），过参与判断、过工具闸门与预算 —— 一个例外都没有。
/// </summary>
public sealed partial class WebUiServer
{
    private JsonObject BuildLocalChannelPayload()
    {
        var s = _box.Current;
        var enabled = !string.IsNullOrWhiteSpace(s.LocalChannelIds) && _localChannel is not null;
        var outbox = new JsonArray();

        if (_localChannel is not null)
        {
            foreach (var item in _localChannel.Outbox.TakeLast(20))
            {
                outbox.Add(new JsonObject
                {
                    ["id"] = item.MessageId,
                    // 会话 key 走面板既有的显示层脱敏（本地 id 一般很短，脱敏规则会原样放行 —— 这是刻意的）
                    ["key"] = _dto.Mask(item.SourceKey),
                    ["length"] = item.Text.Length,
                    ["sentAt"] = item.SentAt.ToUnixTimeMilliseconds(),
                });
            }
        }

        return new JsonObject
        {
            ["available"] = _localChannel is not null,
            ["enabled"] = enabled,
            ["tokenConfigured"] = !string.IsNullOrWhiteSpace(s.PanelToken),
            ["canInject"] = enabled && !string.IsNullOrWhiteSpace(s.PanelToken),
            ["outbox"] = outbox,
        };
    }

    private async Task HandleLocalMessageAsync(HttpListenerContext context)
    {
        var s = _box.Current;

        if (string.IsNullOrWhiteSpace(s.LocalChannelIds) || _localChannel is null)
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "本地通道没开（LocalChannelIds 为空 = 整条通道都不建）",
                ["reason"] = "local_channel_disabled",
            });
            return;
        }

        if (string.IsNullOrWhiteSpace(s.PanelToken))
        {
            await WriteJsonAsync(context, 403, new JsonObject
            {
                ["error"] = "未配置面板令牌 → 本地通道入口不可用（先在设置里配一个）",
                ["reason"] = "panel_token_required",
            });
            return;
        }

        var body = await ReadJsonAsync(context) as JsonObject;
        var configuredId = body?["id"]?.GetValue<long>() ?? 0;
        var text = (body?["text"]?.GetValue<string>() ?? string.Empty).Trim();
        var sender = (body?["sender"]?.GetValue<string>() ?? string.Empty).Trim();
        var isGroup = body?["isGroup"]?.GetValue<bool>() ?? true;
        if (text.Length == 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject
            {
                ["error"] = "缺 text（非空）",
                ["reason"] = "bad_request",
            });
            return;
        }

        // 配置里写的是短 id（1、2、1001…），这里换算成本地号段的**内部目标号**；
        // 换算失败（<=0 或超上限）就拒绝 —— 宁可 400，也不让它撞进 QQ / 官方号段（那会串台）。
        var id = Channels.LocalTarget(configuredId);
        if (id <= 0)
        {
            await WriteJsonAsync(context, 400, new JsonObject
            {
                ["error"] = $"id 必须是 1..{Channels.LocalIdMax}（本地号段由服务端换算，别写真实 QQ 号）",
                ["reason"] = "bad_id",
            });
            return;
        }

        // 注入 = 与协议端推上来的是同一种东西；收不收由白名单闸门说了算（这里不判）。
        var msg = _localChannel.Inject(isGroup, id, sender, text);
        await WriteJsonAsync(context, 202, new JsonObject
        {
            ["accepted"] = true,
            ["key"] = _dto.Mask(Channels.Key(Channels.Local, isGroup, id)),
            ["messageId"] = msg.MessageId,
        });
    }
}
