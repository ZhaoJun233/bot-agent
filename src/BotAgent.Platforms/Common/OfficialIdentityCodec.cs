using System.Text.Json;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;
using BotAgent.Domain.Platforms;
using BotAgent.Domain.Qq;

namespace BotAgent.Adapters.Persistence;

internal static class OfficialIdentityCodec
{
    public static string Encode(OfficialIdentityKey identity)
    {
        string[] parts = [identity.MessageIndex is null ? "1" : "2", identity.Scope.AppId, identity.Scope.Kind.ToString(),
            identity.Scope.Conversation?.Kind.ToString() ?? "", identity.Scope.Conversation?.NativeTargetId ?? "",
            identity.Scope.Direction.ToString(), identity.NativeId];
        return JsonSerializer.Serialize(identity.MessageIndex is null ? parts : [.. parts, identity.MessageIndex]);
    }

    public static OfficialIdentityKey Decode(string encoded)
    {
        try
        {
            var parts = JsonSerializer.Deserialize<string[]>(encoded);
            if (parts is null || parts.Any(part => part is null)
                || !(parts is { Length: 7 } && parts[0] == "1" || parts is { Length: 8 } && parts[0] == "2")
                || !Enum.TryParse<OfficialIdentityKind>(parts[2], out var kind)
                || !Enum.TryParse<OfficialMessageDirection>(parts[5], out var direction)) throw Invalid();
            ConversationId? conversation = null;
            if (parts[3].Length != 0)
            {
                if (!Enum.TryParse<ConversationKind>(parts[3], out var conversationKind)) throw Invalid();
                conversation = new(PlatformId.QqOfficial, parts[1], conversationKind, parts[4]);
            }
            else if (parts[4].Length != 0) throw Invalid();
            var identity = new OfficialIdentityKey(new(parts[1], kind, conversation, direction), parts[6], parts.Length == 8 ? parts[7] : null);
            if (Encode(identity) != encoded) throw Invalid();
            return identity;
        }
        catch (Exception error) when (error is JsonException or ArgumentException)
        {
            throw Invalid();
        }
    }

    public static bool IsDigest(string? digest) => digest is { Length: 64 }
        && digest.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
    public static InvalidDataException Invalid() => new("identity_index_invalid");
}
