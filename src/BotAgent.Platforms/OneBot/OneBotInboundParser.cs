using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;
using BotAgent.Services.Music;
using BotAgent.Services.Qq;

namespace BotAgent.Services.OneBot;

/// <summary>
/// OneBot v11 入站报文的纯解析层：消息段 / CQ 码 / 引用回复 / 表情与音乐段 / 宽容读数字。
///
/// 这里只有纯函数：不持有连接、不读设置、不发动作。网关负责"收到什么"，这里负责"报文长什么样"，
/// 于是协议端字段的兼容写法（同一个 id 既可能是数字也可能是字符串）都收在同一条流水线上，
/// 新增一种写法只需要改这里，不用在网关的事件分支里到处补 if。
/// 日志仍走 OneBot 这一个文件通道，方便排查"协议端到底发了什么"。
/// </summary>
internal static class OneBotInboundParser
{
    /// <summary>
    /// 宽容地读一个数字。
    /// 为什么需要：协议端对同一字段的类型并不统一 —— NapCat 的 <c>level</c> 是字符串（"1"），
    /// 直接用 <c>GetValue&lt;int&gt;()</c> 会抛异常，而异常会把**整次查询**废掉（刚踩过：
    /// 头衔永远拿不到，日志里只有一句 “An element of type 'String' cannot be converted to a 'System.Int32'”）。
    /// </summary>
    internal static int ReadInt(JsonNode? node)
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
    internal static long? ReadLong(JsonNode? node)
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

    /// <summary>数字字段兼容 number / string 两种写法（协议端版本差异），取不到返回 0。</summary>
    internal static long ReadLong(JsonNode root, string name)
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

    /// <summary>这条消息里有没有合并转发段（决定要不要走异步取内容）。</summary>
    internal static bool HasForwardSegment(JsonNode root)
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

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// 取“引用回复”的目标：QQ 的回复会带一个 reply 段（数组形式）或 [CQ:reply,id=…]（CQ 码形式）。
    /// 以前两种都被当成“不认识的段”丢掉 —— 模型只看到一句“我也是”，不知道在回哪条（handoff-4 §27）。
    /// 字段兼容：id / message_id 两种写法都收；有的实现还会直接带 text/summary 摘要，带了就用；
    /// 被引用者的 QQ 号（qq / user_id / sender_id 三种写法）也一并收下 —— 2026-09-19 修“引用机器人的消息被吞”：
    /// 光看本地“我发过哪些”表（内存、上限 200、重启清空）会在部署重启后认不出“他是在回我”。
    /// </summary>
    internal static (long? Id, string? Preview, long? SenderId) ParseReply(JsonArray? segments, string? rawMessage)
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

    /// <summary>把 JsonNode 当文本读（数字/字符串都收）—— 协议端字段类型不稳定，直接 GetValue&lt;string&gt;() 会抛。</summary>
    internal static string? NodeAsText(JsonNode? node)
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
    internal static long? ParseId(string? raw)
        => long.TryParse((raw ?? string.Empty).Trim(), out var id) && id > 0 ? id : null;

    /// <summary>解析消息成纯文本；数组段（text/at/image…）或 raw_message（CQ 码）均支持。</summary>
    internal static (string Text, bool Mentioned, List<string> ImageUrls, List<MusicShare> MusicShares) ParseMessage(
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
                        // 引用回复：这里不拼正文（“在回哪条”由 BotAgentHost 查上下文后标成 [回复 X「…」]）——
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
    internal static bool IsMusicShare(MusicShare share)
        => share.SongId is not null || share.DirectAudioUrl is not null ||
           share.Platform is "netease" or "qq" or "kugou" or "kuwo" or "migu";

    /// <summary>把 CQ 码文本转成可读文本：[CQ:at,qq=123] 保留 @ 目标，图片/表情等替换为占位。</summary>
    internal static string StripCqCode(string raw, long selfId, out bool mentioned, Dictionary<string, JsonNode?>? forwards = null)
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

    internal static string GetArg(string args, string key)
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

    internal static string FirstNonEmpty(params string?[] candidates)
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

    internal static int GetRetcode(JsonNode node)
    {
        var retcode = node?["retcode"]?.GetValue<int>();
        if (retcode is not null)
        {
            return retcode.Value;
        }

        return node?["status"]?.GetValue<string>() == "ok" ? 0 : -1;
    }

    /// <summary>日志里截短（动作失败原因可能很长）。</summary>
    internal static string Shorten(string text, int max)
        => text.Length <= max ? text : text[..max] + "…";

    private static void Log(string message) => FileLog.Write("OneBot", message);
}
