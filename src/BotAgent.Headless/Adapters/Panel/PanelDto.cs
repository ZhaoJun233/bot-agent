using System.Text.Json.Nodes;
using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Qq;
using BotAgent.Services;
using BotAgent.Services.Agent;
using BotAgent.Services.Conversations;
using BotAgent.Services.OneBot;

namespace BotAgent.Adapters.Panel;

/// <summary>
/// 面板的 DTO 映射层（§5.2 的"每域 handler + DTO 映射"那一步）：**领域对象 / 内部字段 → 面板看得懂的 JSON**
/// 只在这一个文件里发生，脱敏规则也收在这里。
///
/// 为什么要有它：以前"这段文字要不要遮"在面板里手写了三遍（各域 handler 各一份），
/// 而"会话 / 消息怎么变成 JSON"散在 <c>WebUiServer.Sessions</c> 里 —— 想确认"面板到底回了什么给前端"
/// 要把整个 partial 目录翻一遍。现在一条规则、一处映射，面板其余部分只负责路由与 IO。
///
/// 三条规矩（V3 §5.3 与 AGENTS.md §4，别在重构里弄丢）：
///   ① **只遮显示层** —— 会话 <c>key</c>、可编辑真名（<c>nameRaw</c>）、协议端要用的 id 一律原样，
///      遮了就点不动按钮、也改不了名字；
///   ② **脱敏看同一个开关**（<c>AgentMaskSensitive</c>），且群里的 <c>//</c> 命令走同一套
///      <see cref="MaskingRules" /> —— 两边口径不允许跑偏；
///   ③ **字段形状不改**：前端 <c>app.js</c> 认的字段名一个都不能换、也不能少（无构建步骤，改不动就得跟着改前端）。
/// </summary>
internal sealed class PanelDto
{
    private readonly SettingsBox _box;
    private readonly ConversationRegistry _registry;

    public PanelDto(SettingsBox box, ConversationRegistry registry)
    {
        _box = box;
        _registry = registry;
    }

    /// <summary>配置读取入口：指向**当前发布版**（脱敏开关会热更新，所以不能缓存那一份实例）。</summary>
    private AppSettings _settings => _box.Current;

    // ══════════════ 显示层脱敏 ══════════════

    /// <summary>
    /// 按开关遮盖一段文本（会话名 / 昵称 / QQ 号）。给 <paramref name="sourceKey" /> 时会把
    /// **这个会话里出现过的名字**也一起遮（不只是 QQ 号），与群里 <c>//</c> 命令同一套口径。
    /// </summary>
    public string Mask(string text, string? sourceKey = null)
        => MaskingRules.Text(_settings.AgentMaskSensitive, text,
            sourceKey is null ? null : _registry.KnownNames(sourceKey));

    /// <summary>
    /// 只按开关遮一段名字，不查"这个会话里出现过哪些名字"（设备上报的会话标题就是这种）。
    /// 与 <see cref="Mask" /> 的差别只有这一点，语义完全一致。
    /// </summary>
    public string MaskName(string name) => _settings.AgentMaskSensitive ? AgentMask.Text(name) : name;

    /// <summary>
    /// 设备上报的 pi 会话条目：**只遮 title**（id / cwd 是机器字段，面板还要拿它们去"接用"）。
    /// 返回的是深拷贝 —— 上游那份是桥的缓存，改了会把桥也弄脏。
    /// </summary>
    public JsonObject PiSession(JsonObject raw)
    {
        var node = (JsonObject)raw.DeepClone();
        if (_settings.AgentMaskSensitive && node["title"] is JsonNode t && t.GetValueKind() == JsonValueKind.String)
        {
            node["title"] = MaskName(t.GetValue<string>());
        }

        return node;
    }

    // ══════════════ 领域对象 → 面板 JSON ══════════════

    /// <summary>一条消息（会话视图与 SSE 推的是同一个形状）。</summary>
    public static JsonObject Message(ChatMessage m) => new()
    {
        ["seq"] = m.Seq,
        ["role"] = m.Role switch
        {
            MessageRole.Self => "Self",
            MessageRole.System => "System",
            _ => "Peer"
        },
        ["text"] = m.Text,
        ["senderName"] = m.SenderName,
        ["senderId"] = m.SenderId,
        ["time"] = m.Timestamp.ToUnixTimeMilliseconds(),
        ["images"] = m.ImageUrls is { Count: > 0 }
            ? new JsonArray(m.ImageUrls.Select(u => (JsonNode)u!).ToArray())
            : null,
        ["qqMessageId"] = m.QqMessageId,
        // 已撤回的消息：面板把它划掉并注明“模型看到的是 [已撤回]”
        ["recalled"] = m.Recalled ? true : null
    };

    /// <summary>
    /// 会话列表里的一项。注意 <c>key</c> 与 <c>name</c> 都是**原样**给的：
    /// 面板要拿 key 去调接口（遮了按钮就废了），而列表显示的脱敏由前端按自己的开关做
    /// （与"可编辑真名用 nameRaw"是同一条规矩：存储与 key 保持原样）。
    /// </summary>
    public static JsonObject Conversation(BotConversation c)
    {
        var (isGroup, id) = c.Target;
        return new JsonObject
        {
            ["key"] = c.SourceKey,
            ["kind"] = isGroup ? "Group" : "Private",
            ["channel"] = c.Channel,
            ["channelTag"] = Channels.Tag(c.Channel),
            ["name"] = c.Name,
            ["id"] = id,
            ["avatarUrl"] = AvatarUrl(isGroup, id),
            ["avatarText"] = FirstChar(c.Name),
            ["unread"] = c.Unread,
            ["thinking"] = c.Thinking,
            ["preview"] = c.Preview,
            ["messageCount"] = c.MessageCount,
            ["lastTime"] = c.LastTime.ToUnixTimeMilliseconds()
        };
    }

    /// <summary>
    /// 一条归档消息。字段名（t/role/sender/uid/mid/text）**保持与老版一致** ——
    /// 归档以前是 archive/*.jsonl、现在是 messages 表 archived=1，前端一直认这几个名字。
    /// </summary>
    public static JsonObject Archived(ArchivedMessage m) => new()
    {
        ["t"] = m.TimeUnix,
        ["role"] = m.Role,
        ["sender"] = m.SenderName,
        ["uid"] = m.SenderId,
        ["mid"] = m.QqMessageId,
        ["text"] = m.Text
    };

    /// <summary>QQ 头像：群 <c>p.qlogo.cn/gh/{群号}/{群号}/0</c>；用户 <c>q1.qlogo.cn/g?b=qq&amp;nk={QQ}&amp;s=640</c>。</summary>
    private static string AvatarUrl(bool isGroup, long id) => isGroup
        ? $"https://p.qlogo.cn/gh/{id}/{id}/0"
        : $"https://q1.qlogo.cn/g?b=qq&nk={id}&s=640";

    private static string FirstChar(string name)
    {
        var trimmed = name?.Trim() ?? string.Empty;
        return trimmed.Length == 0 ? "?" : trimmed[..1];
    }
}
