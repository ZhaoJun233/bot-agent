using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Messaging;

namespace BotAgent.Domain.Memory;

/// <summary>Opaque storage identity for a profile with exact, transport-attested provenance.</summary>
public static class LegacyProfileScopeKey
{
    public const string Prefix = "legacy-profile:v1:";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    /// <summary>
    /// Call only with live transport-attested identifiers. This pure key is not an authorization receipt.
    /// Invalid or mismatched identifiers return null, never a scalar UID fallback. Empty instance means
    /// exactly the default driver, not a wildcard. IDs retain their original case and Unicode form.
    /// </summary>
    public static string? TryCreate(DriverKey driver, ParticipantId subject, ConversationId conversation)
    {
        if (subject is null || conversation is null
            || !Atom(driver.PlatformId) || !Atom(driver.AccountScope)
            || driver.InstanceId is null || (driver.InstanceId.Length != 0 && !Atom(driver.InstanceId))
            || subject.PlatformId != driver.PlatformId || subject.AccountScope != driver.AccountScope
            || conversation.PlatformId != driver.PlatformId || conversation.AccountScope != driver.AccountScope
            || !Atom(subject.NativeUserId) || !Atom(conversation.NativeTargetId)
            || (conversation.ThreadId is not null && !Atom(conversation.ThreadId))
            || conversation.Kind is not (ConversationKind.PrivateChat or ConversationKind.GroupChat or ConversationKind.ChannelChat))
            return null;

        // v1 uses these eleven ordered fields. Each is UTF-8 byte-length + ':' + bytes; null is '-1:'.
        // The prefix is outside the digest. Fixed fields and length prefixes make delimiters unambiguous.
        string?[] fields = [driver.PlatformId, driver.AccountScope, driver.InstanceId,
            subject.PlatformId, subject.AccountScope, subject.NativeUserId,
            conversation.PlatformId, conversation.AccountScope,
            ((int)conversation.Kind).ToString(CultureInfo.InvariantCulture),
            conversation.NativeTargetId, conversation.ThreadId];
        try
        {
            using var payload = new MemoryStream();
            foreach (var field in fields)
            {
                var bytes = field is null ? null : StrictUtf8.GetBytes(field);
                var length = (bytes?.Length ?? -1).ToString(CultureInfo.InvariantCulture) + ":";
                payload.Write(Encoding.ASCII.GetBytes(length));
                if (bytes is not null) payload.Write(bytes);
            }
            return Prefix + Convert.ToHexString(SHA256.HashData(payload.ToArray()));
        }
        catch (EncoderFallbackException)
        {
            // Invalid UTF-16 must not become replacement characters and alias a different identity.
            return null;
        }
    }

    /// <summary>Recognizes this storage format for display only; never proves origin or grants access.</summary>
    public static bool IsScopedKey(string? value) => value is not null
        && value.Length == Prefix.Length + 64 && value.StartsWith(Prefix, StringComparison.Ordinal)
        && value.Skip(Prefix.Length).All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static bool Atom(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
        && value == value.Trim() && !value.Any(char.IsControl);
}
