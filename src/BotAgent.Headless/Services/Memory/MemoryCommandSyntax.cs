using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;

namespace BotAgent.Services.Memory;

/// <summary>Closed command grammar. Scope text describes a destination, never an actor or lease.</summary>
internal static class MemoryCommandSyntax
{
    public const string Prefix = "//memory";
    public static bool IsCommand(string? text) => text?.TrimStart().StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) == true;

    public static string[] Words(string text) => text.Length <= 24000
        ? text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries) : [];

    public static bool TryRequest(string[] words, int start, UnifiedMemoryContext context,
        [NotNullWhen(true)] out UnifiedMemoryRequest? request)
    {
        request = null;
        if (words.Length < start + 2 || !TryFact(words[start], words[start + 1], out var fact)) return false;
        var targets = ImmutableArray.CreateBuilder<UnifiedMemoryScope>();
        if (words.Length > start + 2)
        {
            if (words[start + 2] != "to" || words.Length < start + 4 || words.Length > start + 11) return false;
            foreach (var word in words.Skip(start + 3))
            {
                if (!TryScope(word, context, out var target) || targets.Contains(target)) return false;
                targets.Add(target);
            }
        }
        request = new(fact, targets.ToImmutable(), ExplicitSelfExpression: true);
        return true;
    }

    private static bool TryFact(string category, string value, [NotNullWhen(true)] out UnifiedMemoryFact? fact)
    {
        fact = (category, value) switch
        {
            ("language", "zh" or "en" or "ja") => new(UnifiedMemoryFactCategory.ReplyLanguage, value),
            ("length", "brief" or "detailed") => new(UnifiedMemoryFactCategory.ReplyLength, value),
            ("explanation", "examples" or "step-by-step") => new(UnifiedMemoryFactCategory.ExplanationStyle, value),
            _ => null
        };
        return fact is not null;
    }

    // Every field is individually escaped. '~' is a null thread; a present thread begins '!'.
    // Strict round-trip prevents alternative encodings, delimiter tricks and lossy normalization.
    public static string ScopeToken(UnifiedMemoryScope scope)
    {
        var c = scope.Conversation;
        var kind = c.Kind switch
        {
            ConversationKind.PrivateChat => "private", ConversationKind.GroupChat => "group",
            ConversationKind.ChannelChat => "channel", _ => throw new ArgumentException("invalid_memory_scope")
        };
        return "scope:" + string.Join('/', Uri.EscapeDataString(scope.Driver.PlatformId),
            Uri.EscapeDataString(scope.Driver.AccountScope), Uri.EscapeDataString(scope.Driver.InstanceId), kind,
            Uri.EscapeDataString(c.NativeTargetId), c.ThreadId is null ? "~" : "!" + Uri.EscapeDataString(c.ThreadId));
    }

    public static bool TryScope(string token, UnifiedMemoryContext context, [NotNullWhen(true)] out UnifiedMemoryScope? scope)
    {
        scope = null;
        if (!token.StartsWith("scope:", StringComparison.Ordinal) || token.Length > 8192) return false;
        var parts = token[6..].Split('/');
        if (parts.Length != 6) return false;
        var kind = parts[3] switch
        {
            "private" => ConversationKind.PrivateChat, "group" => ConversationKind.GroupChat,
            "channel" => ConversationKind.ChannelChat, _ => (ConversationKind)(-1)
        };
        if ((int)kind == -1 || (parts[5] != "~" && !parts[5].StartsWith('!'))) return false;
        try
        {
            var driver = new DriverKey(Uri.UnescapeDataString(parts[0]), Uri.UnescapeDataString(parts[1]), Uri.UnescapeDataString(parts[2]));
            var native = Uri.UnescapeDataString(parts[4]);
            var thread = parts[5] == "~" ? null : Uri.UnescapeDataString(parts[5][1..]);
            if (driver != context.Driver || !Instance(driver.InstanceId) || !Atom(native) || native.Contains('*')
                || (thread is not null && (!Atom(thread) || thread.Contains('*')))) return false;
            var parsed = new UnifiedMemoryScope(driver, new(driver.PlatformId, driver.AccountScope, kind, native, thread));
            if (ScopeToken(parsed) != token) return false;
            scope = parsed;
            return true;
        }
        catch (UriFormatException) { return false; }
    }

    // Empty identifies the existing default driver instance; it is never a wildcard.
    public static bool Instance(string? value) => value is not null && (value.Length == 0 || Atom(value));

    public static bool Atom(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
        && value == value.Trim() && !value.Any(char.IsControl);

    public static bool TryId(string text, out Guid id) => Guid.TryParseExact(text, "N", out id) && id != Guid.Empty;
    public static bool TryVersion(string text, out int version) => int.TryParse(text,
        System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out version) && version > 0;
}
