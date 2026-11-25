using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BotAgent.Domain.Messaging;

namespace BotAgent.Adapters.Platforms.Feishu;

internal sealed record FeishuParsedInbound(
    string Text,
    bool MentionedSelf,
    bool AllowsCommands,
    IReadOnlyList<string>? ImageUrls,
    IReadOnlyList<InboundVoiceAttachment>? Voices,
    IReadOnlyList<InboundFileAttachment>? Files);

/// <summary>
/// 飞书入站消息内容解析器：支持 <c>text</c>、<c>post</c>、<c>image</c>、<c>file</c>、<c>audio</c>、<c>interactive</c>，
/// 并精确比对 <c>mentions[].id.open_id == botOpenId</c> 与清理 <c>@_user_N</c> 占位符。
/// </summary>
internal static class FeishuInboundRichParser
{
    public static FeishuParsedInbound Parse(
        string? messageType,
        string contentJson,
        string messageId,
        JsonArray? mentions,
        string? botOpenId)
    {
        var kind = string.IsNullOrWhiteSpace(messageType) ? "text" : messageType.Trim().ToLowerInvariant();
        var images = new List<string>();
        var voices = new List<InboundVoiceAttachment>();
        var files = new List<InboundFileAttachment>();
        var postMentionedBot = false;
        var allowsCommands = true;
        string text;

        switch (kind)
        {
            case "text":
                text = ParseTextContent(contentJson);
                break;

            case "post":
                text = ParsePostContent(contentJson, messageId, mentions, botOpenId, images, out postMentionedBot);
                break;

            case "image":
                text = ParseImageContent(contentJson, messageId, images);
                break;

            case "file":
                text = ParseFileContent(contentJson, messageId, files);
                allowsCommands = false;
                break;

            case "audio":
                text = ParseAudioContent(contentJson, messageId, voices);
                allowsCommands = false;
                break;

            default:
                text = ParseCardOrFallbackContent(kind, contentJson);
                allowsCommands = false;
                break;
        }

        var (resolvedText, mentionHit) = ResolveMentions(text, mentions, botOpenId);
        var mentioned = postMentionedBot || mentionHit;
        return new FeishuParsedInbound(
            resolvedText,
            mentioned,
            allowsCommands,
            images.Count > 0 ? images.AsReadOnly() : null,
            voices.Count > 0 ? voices.AsReadOnly() : null,
            files.Count > 0 ? files.AsReadOnly() : null);
    }

    public static string ParseQuotedSummary(string? messageType, string contentJson)
    {
        var parsed = Parse(messageType, contentJson, "quoted", null, null);
        return parsed.Text;
    }

    private static string ParseTextContent(string contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson)) return string.Empty;
        try
        {
            var node = JsonNode.Parse(contentJson);
            return node?["text"]?.GetValue<string>() ?? contentJson;
        }
        catch
        {
            return contentJson;
        }
    }

    private static string ParseImageContent(string contentJson, string messageId, List<string> images)
    {
        if (!string.IsNullOrWhiteSpace(contentJson) && FeishuBotGateway.IsSafeFeishuToken(messageId))
        {
            try
            {
                var key = JsonNode.Parse(contentJson)?["image_key"]?.GetValue<string>()?.Trim();
                if (FeishuBotGateway.IsSafeFeishuToken(key) && images.Count < 8)
                    images.Add($"feishu-media://{messageId}/{key}");
            }
            catch (JsonException) { }
        }
        return "[图片]";
    }

    private static string ParseFileContent(string contentJson, string messageId, List<InboundFileAttachment> files)
    {
        var fileName = "attachment.dat";
        if (!string.IsNullOrWhiteSpace(contentJson))
        {
            try
            {
                var obj = JsonNode.Parse(contentJson) as JsonObject;
                var key = obj?["file_key"]?.GetValue<string>()?.Trim();
                var rawName = obj?["file_name"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrWhiteSpace(rawName)) fileName = Path.GetFileName(rawName.Replace('\\', '/'));
                if (string.IsNullOrWhiteSpace(fileName) || fileName is "." or "..") fileName = "attachment.dat";
                if (FeishuBotGateway.IsSafeFeishuToken(messageId) && FeishuBotGateway.IsSafeFeishuToken(key) && files.Count < 4)
                    files.Add(new InboundFileAttachment(fileName, null, $"{messageId}|{key}"));
            }
            catch (JsonException) { }
        }
        return $"[文件:{fileName}]";
    }

    private static string ParseAudioContent(string contentJson, string messageId, List<InboundVoiceAttachment> voices)
    {
        if (!string.IsNullOrWhiteSpace(contentJson))
        {
            try
            {
                var obj = JsonNode.Parse(contentJson) as JsonObject;
                var key = obj?["file_key"]?.GetValue<string>()?.Trim();
                int? durationSec = null;
                if (obj?["duration"] is JsonValue dv)
                {
                    if (dv.TryGetValue<int>(out var ms) && ms > 0) durationSec = Math.Max(1, ms / 1000);
                    else if (dv.TryGetValue<string>(out var s) && int.TryParse(s, out var sms) && sms > 0) durationSec = Math.Max(1, sms / 1000);
                }
                if (FeishuBotGateway.IsSafeFeishuToken(messageId) && FeishuBotGateway.IsSafeFeishuToken(key) && voices.Count < 4)
                    voices.Add(new InboundVoiceAttachment(null, null, $"{messageId}|{key}", durationSec));
            }
            catch (JsonException) { }
        }
        return "[语音]";
    }

    private static string ParsePostContent(
        string contentJson,
        string messageId,
        JsonArray? mentions,
        string? botOpenId,
        List<string> images,
        out bool mentionedBot)
    {
        mentionedBot = false;
        if (string.IsNullOrWhiteSpace(contentJson)) return "[富文本]";
        try
        {
            if (JsonNode.Parse(contentJson) is not JsonObject root)
                return "[富文本]";

            var postObj = SelectPostLocale(root);
            if (postObj is null) return "[富文本]";

            var mentionOpenIdByKey = BuildMentionOpenIdMap(mentions);
            var sb = new StringBuilder();
            var title = postObj["title"]?.GetValue<string>()?.Trim();
            if (!string.IsNullOrEmpty(title))
                sb.AppendLine(title);

            if (postObj["content"] is JsonArray paragraphs)
            {
                foreach (var para in paragraphs.OfType<JsonArray>())
                {
                    var line = new StringBuilder();
                    foreach (var elem in para.OfType<JsonObject>())
                    {
                        RenderPostElement(elem, messageId, mentionOpenIdByKey, botOpenId, images, line, ref mentionedBot);
                    }
                    if (line.Length > 0)
                        sb.AppendLine(line.ToString().TrimEnd());
                }
            }

            var rendered = sb.ToString().Trim();
            return rendered.Length > 0 ? rendered : (images.Count > 0 ? "[图片]" : "[富文本]");
        }
        catch
        {
            return "[富文本]";
        }
    }

    private static Dictionary<string, (string? OpenId, string? Name)> BuildMentionOpenIdMap(JsonArray? mentions)
    {
        var map = new Dictionary<string, (string?, string?)>(StringComparer.Ordinal);
        if (mentions is null) return map;
        foreach (var item in mentions.OfType<JsonObject>())
        {
            var key = item["key"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrEmpty(key)) continue;
            var name = item["name"]?.GetValue<string>()?.Trim();
            var openId = (item["id"] as JsonObject)?["open_id"]?.GetValue<string>()?.Trim()
                ?? (item["id"] is JsonValue idVal && idVal.TryGetValue<string>(out var rawId) ? rawId.Trim() : null);
            map[key] = (openId, name);
        }
        return map;
    }

    private static JsonObject? SelectPostLocale(JsonObject root)
    {
        if (root["content"] is JsonArray) return root;
        if (root["zh_cn"] is JsonObject zh) return zh;
        if (root["en_us"] is JsonObject en) return en;
        foreach (var kv in root)
        {
            if (kv.Value is JsonObject sub && sub["content"] is JsonArray)
                return sub;
        }
        return null;
    }

    private static void RenderPostElement(
        JsonObject elem,
        string messageId,
        IReadOnlyDictionary<string, (string? OpenId, string? Name)> mentionMap,
        string? botOpenId,
        List<string> images,
        StringBuilder line,
        ref bool mentionedBot)
    {
        var tag = elem["tag"]?.GetValue<string>()?.ToLowerInvariant();
        switch (tag)
        {
            case "text":
                line.Append(elem["text"]?.GetValue<string>());
                break;
            case "md":
                line.Append(elem["text"]?.GetValue<string>());
                break;
            case "a":
                var linkText = elem["text"]?.GetValue<string>()?.Trim();
                var href = elem["href"]?.GetValue<string>()?.Trim();
                if (!string.IsNullOrEmpty(linkText) && !string.IsNullOrEmpty(href))
                    line.Append(linkText).Append(" (").Append(href).Append(')');
                else
                    line.Append(href ?? linkText);
                break;
            case "at":
                var uid = elem["user_id"]?.GetValue<string>()?.Trim();
                var resolved = uid is not null && mentionMap.TryGetValue(uid, out var mapped) ? mapped : default;
                var effectiveOpenId = resolved.OpenId ?? uid;
                var uname = elem["user_name"]?.GetValue<string>()?.Trim() ?? resolved.Name ?? uid ?? "成员";
                if (string.Equals(uid, "all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uid, "@_all", StringComparison.OrdinalIgnoreCase))
                {
                    line.Append("@所有人 ");
                }
                else if (!string.IsNullOrEmpty(botOpenId) && string.Equals(effectiveOpenId, botOpenId, StringComparison.Ordinal))
                {
                    mentionedBot = true;
                }
                else
                {
                    line.Append('@').Append(uname).Append(' ');
                }
                break;
            case "img":
                var imgKey = elem["image_key"]?.GetValue<string>()?.Trim();
                if (FeishuBotGateway.IsSafeFeishuToken(messageId) && FeishuBotGateway.IsSafeFeishuToken(imgKey) && images.Count < 8)
                    images.Add($"feishu-media://{messageId}/{imgKey}");
                line.Append("[图片]");
                break;
            case "code_block":
                var lang = elem["language"]?.GetValue<string>()?.Trim() ?? string.Empty;
                var code = elem["text"]?.GetValue<string>() ?? string.Empty;
                line.Append('\n').Append("```").AppendLine(lang).AppendLine(code).Append("```");
                break;
            case "media":
                line.Append("[视频]");
                break;
        }
    }

    private static string ParseCardOrFallbackContent(string kind, string contentJson)
    {
        if (string.IsNullOrWhiteSpace(contentJson))
            return $"[{kind}消息（外部内容）]";
        try
        {
            if (JsonNode.Parse(contentJson) is JsonObject obj)
            {
                var title = obj["title"]?.GetValue<string>()
                    ?? obj["header"]?["title"]?["content"]?.GetValue<string>();
                var summary = obj["summary"]?.GetValue<string>() ?? obj["text"]?.GetValue<string>();
                var sb = new StringBuilder("[卡片消息（外部内容）]");
                if (!string.IsNullOrWhiteSpace(title)) sb.Append(" 标题: ").Append(title.Trim());
                if (!string.IsNullOrWhiteSpace(summary)) sb.Append(" 摘要: ").Append(summary.Trim());
                return sb.ToString();
            }
        }
        catch { }
        return $"[{kind}消息（外部内容）]";
    }

    private static (string Text, bool Mentioned) ResolveMentions(string text, JsonArray? mentions, string? botOpenId)
    {
        if (mentions is not { Count: > 0 })
        {
            return (text, false);
        }

        var mentioned = false;
        var replacements = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in mentions.OfType<JsonObject>())
        {
            var key = item["key"]?.GetValue<string>()?.Trim();
            var name = item["name"]?.GetValue<string>()?.Trim();
            var openId = (item["id"] as JsonObject)?["open_id"]?.GetValue<string>()?.Trim()
                ?? (item["id"] is JsonValue idVal && idVal.TryGetValue<string>(out var rawId) ? rawId.Trim() : null);

            var isAll = string.Equals(key, "@_all", StringComparison.OrdinalIgnoreCase);
            var isSelf = !isAll && (!string.IsNullOrEmpty(openId)
                ? !string.IsNullOrEmpty(botOpenId) && string.Equals(openId, botOpenId, StringComparison.Ordinal)
                : string.IsNullOrEmpty(botOpenId));

            if (isSelf)
                mentioned = true;

            if (!string.IsNullOrEmpty(key))
            {
                replacements[key] = isSelf && !string.IsNullOrEmpty(botOpenId)
                    ? string.Empty
                    : !string.IsNullOrEmpty(name)
                        ? "@" + name
                        : key;
            }
        }

        if (replacements.Count == 0 || string.IsNullOrEmpty(text))
            return (text.Trim(), mentioned);

        var current = Regex.Replace(text, @"@_user_\d+(?!\d)\s*|@_all\b", match =>
        {
            var raw = match.Value;
            var trimmed = raw.TrimEnd();
            if (!replacements.TryGetValue(trimmed, out var rep))
                return raw;
            if (rep.Length == 0)
                return string.Empty;
            var trailingSpace = raw.Length > trimmed.Length ? raw[trimmed.Length..] : string.Empty;
            return rep + trailingSpace;
        });

        return (current.Trim(), mentioned);
    }
}
