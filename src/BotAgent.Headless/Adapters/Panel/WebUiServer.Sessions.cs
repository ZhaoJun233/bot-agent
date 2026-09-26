using System.Net;
using System.Net.WebSockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Conversation;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.NapCat;
using BotAgent.Services.OneBot;
using BotAgent.Services.Ops;
using BotAgent.Services.Conversations;
using BotAgent.Services.Qq;
using BotAgent.Adapters.Persistence;

namespace BotAgent.Adapters.Panel;

public sealed partial class WebUiServer
{
    private async Task HandleConversationAsync(HttpListenerContext context, string rest, string method)
    {
        var parts = rest.Split('/', 2);
        var key = Uri.UnescapeDataString(parts[0]);
        var action = parts.Length > 1 ? parts[1].ToLowerInvariant() : "messages";

        switch (action, method)
        {
            case ("messages", "GET"):
            {
                var conversation = _registry.Find(key);
                if (conversation is null)
                {
                    await WriteJsonAsync(context, 404, new JsonObject { ["error"] = "no such conversation" });
                    return;
                }

                var limit = int.TryParse(context.Request.QueryString["limit"], out var n)
                    ? Math.Clamp(n, 1, 2000)
                    : 300;

                var messages = conversation.TakeLast(limit).Select(PanelDto.Message).ToList();
                await WriteJsonAsync(context, 200, new JsonObject
                {
                    ["key"] = key,
                    ["messages"] = new JsonArray(messages.Select(m => (JsonNode)m).ToArray())
                });
                return;
            }

            case ("send", "POST"):
            {
                var body = await ReadJsonAsync(context);
                var text = body?["text"]?.GetValue<string>() ?? string.Empty;
                var ok = await _agent.SendAsBotAsync(key, text);
                await WriteJsonAsync(context, ok ? 200 : 400, new JsonObject
                {
                    ["ok"] = ok,
                    ["error"] = ok ? null : (_gateway.IsConnected ? "发送失败" : "QQ 未连接")
                });
                return;
            }

            case ("read", "POST"):
                _registry.MarkRead(key);
                await WriteJsonAsync(context, 200, new JsonObject { ["ok"] = true });
                return;

            case ("delete", "POST"):
                // 删会话是"注册表 + 各台账"一起的事：台账的清理挂在 ConversationRegistry.Deleted 上
                // （装配点接线），这里只负责删，然后如实回报删没删掉。
                var deleted = _registry.Delete(key) is not null;
                await WriteJsonAsync(context, deleted ? 200 : 404, new JsonObject { ["ok"] = deleted });
                return;

            case ("rename", "POST"):
            {
                var body = await ReadJsonAsync(context);
                var name = body?["name"]?.GetValue<string>() ?? string.Empty;
                var renamed = _registry.Rename(key, name);
                await WriteJsonAsync(context, renamed ? 200 : 400, new JsonObject
                {
                    ["ok"] = renamed,
                    ["message"] = renamed ? "会话已改名" : "名称不能为空或会话不存在"
                });
                return;
            }

            case ("clear", "POST"):
            {
                var cleared = _registry.ClearMessages(key);
                await WriteJsonAsync(context, cleared ? 200 : 404, new JsonObject
                {
                    ["ok"] = cleared,
                    ["message"] = cleared ? "会话历史已清空" : "会话不存在"
                });
                return;
            }
            default:
                await WriteJsonAsync(context, 405, new JsonObject { ["error"] = "method not allowed" });
                return;
        }
    }

    private List<JsonObject> BuildConversations()
    {
        var list = new List<JsonObject>();
        foreach (var c in _registry.Snapshot())
        {
            list.Add(_dto.Conversation(c));
        }

        return list;
    }

    /// <summary>
    /// 两条通道的状态（面板顶部那两个板块用）：启用没启用、连上没有。
    /// 数据来自通道台账（<see cref="Services.Qq.ChannelRouter"/>）；单通道部署时官方那条会是
    /// <c>enabled=false</c>——面板就把它显示成“未启用”，而不是“离线”（那是两回事：
    /// 一个是没配，一个是配了但断了）。
    /// </summary>
    /// <summary>
    /// 官方通道见过的会话（别名号 + 名字），给面板做“一键填白名单”用。
    /// 为什么要它：官方白名单存的是**别名号**（8e15 起），填真实群号 = 静默全拦（踩过）；
    /// 与其让人猜格式，不如把见过的会话列出来点一下。
    /// </summary>
    private JsonArray BuildOfficialConversations()
    {
        var arr = new JsonArray();
        foreach (var c in _registry.Snapshot())
        {
            if (!Domain.Qq.Channels.IsOfficial(c.Channel))
            {
                continue;
            }

            var (isGroup, id) = c.Target;
            if (id <= 0)
            {
                continue;
            }

            arr.Add(new JsonObject
            {
                ["key"] = c.SourceKey,
                ["id"] = id.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["isGroup"] = isGroup,
                ["name"] = c.Name,
            });
        }

        return arr;
    }

    private JsonArray BuildChannelStatus()
    {
        var registry = _source as IChannelRegistry;
        var arr = new JsonArray();
        foreach (var channel in new[] { Domain.Qq.Channels.Private, Domain.Qq.Channels.Official })
        {
            var src = registry?.Get(channel);

            // 没启用多通道时没有台账（registry 为 null）——这时候**私域就是网关自己**，
            // 不能因为“没登记”就报成离线（踩过：面板显示“私域 离线”，实际 QQ 连着好好的）。
            var connected = channel == Domain.Qq.Channels.Private
                ? (src?.IsConnected ?? _gateway.IsConnected)
                : (src?.IsConnected ?? false);

            arr.Add(new JsonObject
            {
                ["channel"] = channel,
                ["name"] = Domain.Qq.Channels.Display(channel),
                ["tag"] = Domain.Qq.Channels.Tag(channel),
                ["enabled"] = src is not null || !Domain.Qq.Channels.IsOfficial(channel),
                ["connected"] = connected
            });
        }

        return arr;
    }

    /// <summary>
    /// 读取某会话的归档（已溢出滚动窗口的旧消息）。
    /// 现在归档在 SQLite 里（messages 表 archived=1），不再是 archive/*.jsonl 文件；
    /// 返回给面板的字段保持与老版一致（t/role/sender/uid/mid/text），前端不用改。
    /// </summary>
    private JsonObject ReadArchive(string sourceKey, int limit)
    {
        var result = new JsonObject
        {
            ["key"] = sourceKey,
            ["messages"] = new JsonArray(),
            ["totalLines"] = 0
        };

        if (string.IsNullOrWhiteSpace(sourceKey))
        {
            result["error"] = "缺少 key";
            return result;
        }

        try
        {
            var rows = _registry.ReadArchive(sourceKey, limit);
            var array = new JsonArray();
            foreach (var m in rows)
            {
                array.Add(PanelDto.Archived(m));
            }

            result["messages"] = array;
            result["totalLines"] = rows.Count;
            if (rows.Count == 0)
            {
                result["error"] = "该会话尚无归档";
            }
        }
        catch (Exception ex)
        {
            result["error"] = ex.Message;
        }

        return result;
    }
}
