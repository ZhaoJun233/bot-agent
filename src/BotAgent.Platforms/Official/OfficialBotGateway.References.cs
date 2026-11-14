using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Qq;

namespace BotAgent.Services.Official;

internal static class OfficialReferenceFields
{
    internal static bool Valid(string? value)
        => value is { Length: > 0 and <= 512 } && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c));

    internal static string? ReadMessageIndex(JsonObject data)
    {
        if (data["message_scene"] is null) return null;
        if (data["message_scene"] is not JsonObject scene) throw new ArgumentException("identity_message_index_invalid");
        if (scene["ext"] is null) return null;
        if (scene["ext"] is not JsonArray { Count: <= 32 } ext) throw new ArgumentException("identity_message_index_invalid");
        string? result = null;
        foreach (var node in ext)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) throw new ArgumentException("identity_message_index_invalid");
            if (!text.StartsWith("msg_idx=", StringComparison.Ordinal)) continue;
            var index = text[8..];
            if (result is not null || !Valid(index)) throw new ArgumentException("identity_message_index_invalid");
            result = index;
        }
        return result;
    }

    internal static string? ReadExt(JsonObject data, string field)
    {
        if (data["message_scene"] is not JsonObject scene || scene["ext"] is not JsonArray { Count: <= 32 } ext) return null;
        string? result = null;
        foreach (var node in ext)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
            if (!text.StartsWith(field + "=", StringComparison.Ordinal)) continue;
            var index = text[(field.Length + 1)..];
            if (result is not null || !Valid(index)) return null;
            result = index;
        }
        return result;
    }

    internal static string? ReadReceipt(JsonElement root)
    {
        if (!root.TryGetProperty("ext_info", out var extra) || extra.ValueKind != JsonValueKind.Object) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        if (extra.EnumerateObject().Any(p => !names.Add(p.Name)) || !extra.TryGetProperty("ref_idx", out var index)
            || index.ValueKind != JsonValueKind.String) return null;
        var text = index.GetString();
        return Valid(text) ? text : null;
    }
}

public sealed partial class OfficialBotGateway
{
    private const int MaxReferences = 4096;
    private const string MissingReferenceText = "（引用原文不可用）";
    private readonly object _referenceGate = new();
    private readonly Dictionary<(ConversationId Conversation, string Index), ReferenceEntry> _references = [];
    private sealed record ReferenceEntry(long Alias, DateTimeOffset ExpiresAt);

    private void PruneReferences()
    {
        foreach (var key in _references.Where(p => p.Value.ExpiresAt <= Clock.Now).Select(p => p.Key).ToArray()) _references.Remove(key);
    }

    private void RememberReference(ConversationId conversation, long alias, string? index)
    {
        if (alias <= 0 || !OfficialReferenceFields.Valid(index)) return;
        lock (_referenceGate)
        {
            PruneReferences();
            var key = (conversation, index!);
            var expires = Clock.Now.AddHours(1);
            if (_references.TryGetValue(key, out var previous))
            {
                // A conflicting index remains a tombstone for its entire retention horizon.
                _references[key] = new(previous.Alias == alias ? alias : 0,
                    previous.ExpiresAt > expires ? previous.ExpiresAt : expires);
            }
            else if (_references.Count < MaxReferences) _references.Add(key, new(alias, expires));
        }
    }

    private void InvalidateReferences(string? group = null)
    {
        if (group is not null) { InvalidateRelationshipReferences(true, group); return; }
        lock (_referenceGate) _references.Clear();
    }

    private void InvalidateRelationshipReferences(bool isGroup, string target)
    {
        var conversation = NativeConversation(isGroup, target);
        lock (_referenceGate)
        {
            foreach (var key in _references.Keys.Where(k => k.Conversation == conversation).ToArray()) _references.Remove(key);
        }
    }

    private (bool Known, string? Index) ResolveReference(ConversationId conversation, long alias)
    {
        var inbound = new OfficialIdentityScope(_applicationId, OfficialIdentityKind.Message, conversation, OfficialMessageDirection.Inbound);
        var outbound = new OfficialIdentityScope(_applicationId, OfficialIdentityKind.Message, conversation, OfficialMessageDirection.Outbound);
        if (_ids.Value.OriginalOf(alias, inbound) is null && _ids.Value.OriginalOf(alias, outbound) is null) return (false, null);
        lock (_referenceGate)
        {
            PruneReferences();
            var matches = _references.Where(p => p.Key.Conversation == conversation && p.Value.Alias == alias).Take(2).ToArray();
            return (true, matches.Length == 1 ? matches[0].Key.Index : null);
        }
    }

    private (long? Alias, string? Preview) ObserveReferences(OfficialIdentityKey message, long alias, JsonObject data)
    {
        var conversation = message.Scope.Conversation!;
        var own = OfficialReferenceFields.ReadExt(data, "msg_idx");
        var quoted = OfficialReferenceFields.ReadExt(data, "ref_msg_idx");
        RememberReference(conversation, alias, own);
        var isQuote = data["message_type"] is JsonValue value && value.TryGetValue<int>(out var type) && type == 103;
        if (!isQuote) return (null, null);
        long? quotedAlias = null;
        if (quoted is not null && quoted != own)
        {
            lock (_referenceGate)
            {
                PruneReferences();
                if (_references.TryGetValue((conversation, quoted), out var entry) && entry.Alias > 0) quotedAlias = entry.Alias;
            }
        }
        return (quotedAlias, QuotedPreview(data, quoted) ?? (quotedAlias is null ? MissingReferenceText : null));
    }

    private static string? QuotedPreview(JsonObject data, string? index)
    {
        if (index is null || data["msg_elements"] is not JsonArray { Count: > 0 and <= 32 } elements) return null;
        JsonObject? match = null;
        foreach (var element in elements)
        {
            if (element is not JsonObject item || item["msg_idx"] is not JsonValue id || !id.TryGetValue<string>(out var text) || text != index) continue;
            if (match is not null) return null;
            match = item;
        }
        if (match?["content"] is not JsonValue content || !content.TryGetValue<string>(out var preview)
            || string.IsNullOrWhiteSpace(preview) || preview.Length > 2048
            || preview.Any(c => char.IsControl(c) && c is not '\n' and not '\r' and not '\t')) return null;
        return preview;
    }
}
