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
using BotAgent.Services.Settings;
using BotAgent.Adapters.Persistence;
using System.Collections.Immutable;
using BotAgent.Domain.Configuration;

namespace BotAgent.Adapters.Panel;

internal static class PanelLegacyFieldEdits
{
    internal static FieldEdit<T> LegacyField<T>(JsonObject body, string key, Func<JsonNode, T> parse)
        => body[key] is { } node ? FieldEdit<T>.Set(parse(node)) : FieldEdit<T>.Keep();

    internal static string LegacyTruncate(string value, int maximum)
        => value.Length > maximum ? value[..maximum] : value;

    internal static string LegacyReasoningEffort(string? raw)
    {
        var value = (raw ?? string.Empty).Trim().ToLowerInvariant();
        return value is "" or "default" ? "auto" : value;
    }

    internal static FieldEdit<string> ParseLegacyFastModel(JsonObject body)
        => body["fastModel"] is JsonValue value && value.TryGetValue<string>(out var text)
            ? FieldEdit<string>.Set((text ?? string.Empty).Trim()) : FieldEdit<string>.Keep();

    internal static FieldEdit<string> ParseLegacyThinkingText(JsonObject body, string key, bool lowercase)
    {
        if (body[key] is not { } node || node.GetValue<string>() is not { Length: > 0 } text)
            return FieldEdit<string>.Keep();
        var normalized = text.Trim();
        return FieldEdit<string>.Set(lowercase ? normalized.ToLowerInvariant() : normalized);
    }

    internal static FieldEdit<string> ParseLegacyOverride(JsonObject body, string key, bool isUrl)
    {
        if (body[key] is not { } node) return FieldEdit<string>.Keep();
        var text = (node.GetValue<string>() ?? string.Empty).Trim();
        if (text.Length == 0) return FieldEdit<string>.Clear();
        if (isUrl && (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            throw new ArgumentException("invalid_legacy_model_endpoint");
        return FieldEdit<string>.Set(text);
    }

    internal static FieldEdit<string> ParseLegacyAgentBaseUrl(JsonObject body)
    {
        if (body["agentServerBaseUrl"] is not { } node) return FieldEdit<string>.Keep();
        var text = (node.GetValue<string>() ?? string.Empty).Trim();
        if (text.Length != 0 && (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)))
            return FieldEdit<string>.Keep();
        return FieldEdit<string>.Set(text);
    }

    internal static FieldEdit<string> ParseLegacyHealthClock(JsonObject body)
    {
        if (body["healthReportTime"] is not { } node) return FieldEdit<string>.Keep();
        var text = (node.ToString() ?? string.Empty).Trim();
        if (text.Length == 0 || !(text.Replace('：', ':').Contains(':') || text.Length == 4))
            return FieldEdit<string>.Keep();
        var (hour, minute) = AppSettings.ParseHealthReportClock(text);
        return FieldEdit<string>.Set($"{hour:00}:{minute:00}");
    }

    internal static FieldEdit<string> ParseLegacyQqActions(JsonObject body)
    {
        if (body["agentServerQqActions"] is not { } node) return FieldEdit<string>.Keep();
        var raw = (node.GetValue<string>() ?? string.Empty).Trim();
        if (raw.Length == 0 || raw is "all" or "*" or "全部" or "所有") return FieldEdit<string>.Set(raw);
        var names = new List<string>();
        foreach (var piece in raw.Split(new[] { ',', '，', ';', '；', ' ', '\t', '\n', '\r' },
            StringSplitOptions.RemoveEmptyEntries))
        {
            var canonical = QqActionCatalog.Canonical(piece.Trim());
            if (canonical is not null && !names.Contains(canonical)) names.Add(canonical);
        }
        return FieldEdit<string>.Set(string.Join(",", names));
    }
}
