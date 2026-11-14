using System.Text;
using System.Text.Json.Nodes;
using BotAgent.Domain.Messaging;

namespace BotAgent.Services.Official;

public sealed partial class OfficialBotGateway
{
    private sealed record InboundContent(string Text, IReadOnlyList<string>? Images,
        IReadOnlyList<InboundVoiceAttachment>? Voices, bool AllowsCommands);

    private static InboundContent ReadInboundContent(JsonObject data, string content)
    {
        var images = new List<string>();
        var voices = new List<InboundVoiceAttachment>();
        if (data["attachments"] is JsonArray attachments)
        {
            foreach (var item in attachments.Take(16).OfType<JsonObject>())
            {
                var type = MediaText(item["content_type"], 80);
                if (type?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true)
                    AddImage(item["url"]);
                else if (string.Equals(type, "voice", StringComparison.OrdinalIgnoreCase) && voices.Count < 8)
                    voices.Add(new(MediaUrl(item["voice_wav_url"]), MediaText(item["asr_refer_text"], 2000)));
            }
        }
        var card = data["ark_data"] as JsonObject;
        var isCard = data["ark_data"] is not null
            || data["message_type"] is JsonValue value && value.TryGetValue<int>(out var typeNumber) && typeNumber == 3;
        var derived = voices.Count > 0 || isCard;
        if (derived)
        {
            var body = new StringBuilder(isCard ? "[卡片消息（外部内容）]" : "[语音消息（外部内容）]");
            Append("正文", BoundedMediaText(content, 16000));
            foreach (var voice in voices)
                Append("平台参考转写", voice.Transcript ?? "暂无转写");
            if (card is not null)
            {
                Append("卡片类型", MediaText(card["ark_type"], 1000));
                Append("卡片名称", MediaText(card["ark_name"], 1000));
                Append("卡片提示", MediaText(card["prompt"], 1000));
                if (card["fields"] is JsonObject fields)
                {
                    Append("标题", MediaText(fields["title"], 1000));
                    Append("描述", MediaText(fields["desc"], 1000));
                    Append("来源", MediaText(fields["source"], 1000));
                    Append("链接", MediaUrl(fields["jump_url"]));
                    AddImage(fields["preview"]);
                }
            }
            content = BoundedMediaText(body.ToString(), 16000);
            void Append(string label, string? text)
            {
                if (!string.IsNullOrWhiteSpace(text) && body.Length < 16000)
                    body.Append('\n').Append(label).Append(": ").Append(text);
            }
        }
        return new(content, images.Count == 0 ? null : images.AsReadOnly(),
            voices.Count == 0 ? null : voices.AsReadOnly(), !derived);

        void AddImage(JsonNode? node)
        {
            if (images.Count < 8 && MediaUrl(node) is { } url && !images.Contains(url, StringComparer.Ordinal)) images.Add(url);
        }
    }

    private static string? MediaUrl(JsonNode? node)
        => node is JsonValue value && value.TryGetValue<string>(out var url) && TryMediaUrl(url, out _) ? url : null;

    private static bool TryMediaUrl(string? url, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrEmpty(url) || url.Length > 2048 || url.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            || !Uri.TryCreate(url, UriKind.Absolute, out var parsed)
            || parsed.Scheme is not ("https" or "http") || !string.IsNullOrEmpty(parsed.UserInfo) || string.IsNullOrEmpty(parsed.Host)) return false;
        uri = parsed;
        return true;
    }

    private static string? MediaText(JsonNode? node, int limit)
        => node is JsonValue value && value.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text)
            ? BoundedMediaText(text, limit) : null;

    private static string BoundedMediaText(string value, int limit)
    {
        var text = new StringBuilder(Math.Min(value.Length, limit));
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) && rune.Value is not ('\n' or '\t')) continue;
            if (text.Length + rune.Utf16SequenceLength > limit) break;
            text.Append(rune.ToString());
        }
        return text.ToString().Trim();
    }
}
