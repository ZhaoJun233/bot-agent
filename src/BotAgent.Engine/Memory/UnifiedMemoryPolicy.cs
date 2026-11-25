using System.Collections.Frozen;
using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;

namespace BotAgent.Engine.Memory;

internal static class UnifiedMemoryPolicy
{
    internal static readonly FrozenSet<string> AllowedTimezones = new[]
    {
        "UTC", "Asia/Shanghai", "Asia/Tokyo", "Asia/Hong_Kong", "Asia/Singapore",
        "Asia/Seoul", "Asia/Taipei", "Europe/London", "Europe/Berlin", "Europe/Paris",
        "America/New_York", "America/Chicago", "America/Denver", "America/Los_Angeles",
        "Australia/Sydney"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly string[] SecretPrefixes =
        ["sk-", "ghp_", "gho_", "glpat-", "Bearer ", "AIza", "-----BEGIN"];

    public static bool Reliable(UnifiedMemoryContext context) =>
        context.IsReliableIdentity && context.Actor is { } actor && ValidParticipant(actor)
        && ValidScope(new(context.Driver, context.Conversation))
        && actor.PlatformId == context.Conversation.PlatformId
        && actor.AccountScope == context.Conversation.AccountScope;

    public static bool ValidParticipant(ParticipantId subject) =>
        Atom(subject.PlatformId) && Atom(subject.AccountScope) && Atom(subject.NativeUserId);

    public static bool ValidScope(UnifiedMemoryScope scope) =>
        scope is not null && Atom(scope.Driver.PlatformId) && Atom(scope.Driver.AccountScope)
        && scope.Driver.InstanceId is not null && (scope.Driver.InstanceId.Length == 0 || Atom(scope.Driver.InstanceId))
        && scope.Conversation is { } conversation && scope.Driver.PlatformId == conversation.PlatformId
        && scope.Driver.AccountScope == conversation.AccountScope
        && Atom(conversation.PlatformId) && Atom(conversation.AccountScope) && Atom(conversation.NativeTargetId)
        && (conversation.ThreadId is null || Atom(conversation.ThreadId))
        && conversation.Kind is ConversationKind.PrivateChat or ConversationKind.GroupChat or ConversationKind.ChannelChat;

    private static bool Atom(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 256
        && value == value.Trim() && !value.Any(char.IsControl);

    public static bool SameBoundary(UnifiedMemoryScope left, UnifiedMemoryScope right) =>
        left.Driver == right.Driver && left.Conversation.PlatformId == right.Conversation.PlatformId
        && left.Conversation.AccountScope == right.Conversation.AccountScope;

    public static bool OwnerPrivate(UnifiedMemoryContext context, UnifiedMemoryEntry entry) =>
        Reliable(context) && context.Conversation.Kind == ConversationKind.PrivateChat
        && context.Actor == entry.Subject && SameBoundary(new(context.Driver, context.Conversation), entry.Source);

    public static bool ValidFact(UnifiedMemoryFact fact) => fact is not null && fact.Category switch
    {
        UnifiedMemoryFactCategory.ReplyLanguage => fact.Value is "zh" or "en" or "ja",
        UnifiedMemoryFactCategory.ReplyLength => fact.Value is "brief" or "detailed",
        UnifiedMemoryFactCategory.ExplanationStyle => fact.Value is "examples" or "step-by-step",
        UnifiedMemoryFactCategory.Timezone => fact.Value is not null && AllowedTimezones.Contains(fact.Value),
        UnifiedMemoryFactCategory.PreferredAddress => ValidBoundedText(fact.Value, 24, disallowBrackets: true),
        UnifiedMemoryFactCategory.PersonalNote => ValidBoundedText(fact.Value, 240, disallowBrackets: false),
        _ => false
    };

    private static bool ValidBoundedText(string? value, int maxLength, bool disallowBrackets)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.Length > maxLength || value != value.Trim()) return false;
        if (value.Any(c => char.IsControl(c) || c is '\r' or '\n' or '<' or '>')) return false;
        if (disallowBrackets && (value.Contains('[') || value.Contains(']'))) return false;
        if (value.Contains("//memory", StringComparison.OrdinalIgnoreCase)
            || value.Contains("scope:", StringComparison.OrdinalIgnoreCase)) return false;
        foreach (var prefix in SecretPrefixes)
        {
            if (value.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    public static UnifiedMemoryStatus CheckRequest(UnifiedMemoryContext context, UnifiedMemoryRequest request,
        UnifiedMemoryScope source)
    {
        if (!Reliable(context)) return UnifiedMemoryStatus.UnreliableIdentity;
        if (request is null || !request.ExplicitSelfExpression) return UnifiedMemoryStatus.ExplicitRequestRequired;
        if (!ValidFact(request.Fact)) return UnifiedMemoryStatus.InvalidFact;
        if (request.Fact.Category == UnifiedMemoryFactCategory.PersonalNote)
        {
            if (source.Conversation.Kind != ConversationKind.PrivateChat)
                return UnifiedMemoryStatus.NotOwnerPrivateChat;
            if (request.Targets.IsDefault || !request.Targets.IsEmpty)
                return UnifiedMemoryStatus.InvalidTarget;
            return UnifiedMemoryStatus.Success;
        }
        if (request.Targets.IsDefault || request.Targets.Length > 8 || request.Targets.Distinct().Count() != request.Targets.Length
            || request.Targets.Any(target => !ValidScope(target) || !SameBoundary(source, target) || target == source))
            return UnifiedMemoryStatus.InvalidTarget;
        return UnifiedMemoryStatus.Success;
    }

    public static bool CanRead(UnifiedMemoryEntry entry, UnifiedMemoryContext context)
    {
        if (!entry.Confirmed || entry.Subject != context.Actor) return false;
        var current = new UnifiedMemoryScope(context.Driver, context.Conversation);
        if (entry.Fact.Category == UnifiedMemoryFactCategory.PersonalNote)
        {
            return context.Conversation.Kind == ConversationKind.PrivateChat
                && entry.AllowSourceLocal
                && entry.SharedTargets.IsEmpty
                && entry.Source == current;
        }
        return (entry.AllowSourceLocal && entry.Source == current) || entry.SharedTargets.Contains(current);
    }

    public static bool Relevant(UnifiedMemoryFact fact, string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return false;
        return fact.Category switch
        {
            UnifiedMemoryFactCategory.ReplyLanguage
                or UnifiedMemoryFactCategory.ReplyLength
                or UnifiedMemoryFactCategory.ExplanationStyle
                or UnifiedMemoryFactCategory.Timezone
                or UnifiedMemoryFactCategory.PreferredAddress => true,
            UnifiedMemoryFactCategory.PersonalNote => NoteRelevanceScore(fact.Value, query) > 0,
            _ => false
        };
    }

    private static readonly FrozenSet<string> AsciiStopwords = new[]
    {
        "the", "and", "for", "are", "but", "not", "you", "all", "can", "was", "one", "our", "out",
        "how", "now", "who", "its", "too", "use", "has", "had", "did", "was", "will", "with", "that",
        "this", "from", "have", "what", "when", "where", "which", "about", "there", "their", "them",
        "to", "in", "is", "it", "of", "on", "at", "by", "an", "be", "as", "or", "so", "if", "no",
        "my", "me", "we", "he", "do"
    }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> CjkStopBigrams = new[]
    {
        "什么", "怎么", "可以", "这个", "那个", "我们", "你们", "他们", "的时", "时候"
    }.ToFrozenSet(StringComparer.Ordinal);

    public static int NoteRelevanceScore(string noteText, string query)
    {
        if (string.IsNullOrWhiteSpace(noteText) || string.IsNullOrWhiteSpace(query)) return 0;
        var score = 0;
        var q = query.Trim();
        if (q.Contains("备忘", StringComparison.Ordinal) || q.Contains("笔记", StringComparison.Ordinal)
            || q.Contains("记的", StringComparison.Ordinal) || q.Contains("记得", StringComparison.Ordinal))
            score += 1;

        var lowerNote = noteText.ToLowerInvariant();
        var lowerQuery = q.ToLowerInvariant();
        if (lowerQuery.Length >= 2 && lowerNote.Contains(lowerQuery, StringComparison.Ordinal))
            score += 5;

        // CJK bigrams
        for (var i = 0; i < q.Length - 1; i++)
        {
            if (IsCjk(q[i]) && IsCjk(q[i + 1]))
            {
                var bg = q.AsSpan(i, 2);
                if (!CjkStopBigrams.Contains(bg.ToString()) && noteText.Contains(bg, StringComparison.Ordinal))
                    score += 3;
            }
        }
        // ASCII tokens >= 2 chars
        var span = lowerQuery.AsSpan();
        var start = -1;
        for (var i = 0; i <= span.Length; i++)
        {
            var isAlnum = i < span.Length && char.IsAsciiLetterOrDigit(span[i]);
            if (isAlnum && start < 0) start = i;
            else if (!isAlnum && start >= 0)
            {
                var tok = span[start..i];
                if (tok.Length >= 2 && !AsciiStopwords.Contains(tok.ToString()))
                {
                    if (tok is "note" or "notes" or "memo")
                        score += 1;
                    else if (lowerNote.Contains(tok, StringComparison.Ordinal))
                        score += 3;
                }
                start = -1;
            }
        }
        return score;
    }

    private static bool IsCjk(char c) => c is (>= '\u4E00' and <= '\u9FFF') or (>= '\u3400' and <= '\u4DBF');

    public static bool ValidState(UnifiedMemoryState state)
    {
        if (state is null || state.Revision < 0 || state.Entries.IsDefault || state.Suppressions.IsDefault) return false;
        if (state.Entries.Length > 10000 || state.Suppressions.Length > 10000
            || state.Entries.Select(entry => entry.FactId).Distinct().Count() != state.Entries.Length) return false;
        return state.Entries.All(ValidEntry) && state.Suppressions.All(marker => marker is not null
            && ValidParticipant(marker.Subject) && ValidScope(marker.Source)
            && marker.Subject.PlatformId == marker.Source.Conversation.PlatformId
            && marker.Subject.AccountScope == marker.Source.Conversation.AccountScope);
    }

    private static bool ValidEntry(UnifiedMemoryEntry entry) => entry is not null
        && entry.FactId != Guid.Empty && entry.Version > 0 && ValidParticipant(entry.Subject)
        && ValidScope(entry.Source) && entry.Subject.PlatformId == entry.Source.Conversation.PlatformId
        && entry.Subject.AccountScope == entry.Source.Conversation.AccountScope && ValidFact(entry.Fact)
        && !entry.ProposedTargets.IsDefault && !entry.SharedTargets.IsDefault && entry.ProposedTargets.Length <= 8
        && entry.ProposedTargets.Distinct().Count() == entry.ProposedTargets.Length
        && entry.ProposedTargets.All(target => ValidScope(target) && SameBoundary(entry.Source, target) && target != entry.Source)
        && entry.SharedTargets.All(entry.ProposedTargets.Contains)
        && (entry.Fact.Category != UnifiedMemoryFactCategory.PersonalNote
            || (entry.Source.Conversation.Kind == ConversationKind.PrivateChat && entry.ProposedTargets.IsEmpty && entry.SharedTargets.IsEmpty))
        && (entry.Confirmed ? entry.ConfirmationToken == Guid.Empty && entry.AllowSourceLocal
            : entry.ConfirmationToken != Guid.Empty && !entry.AllowSourceLocal && entry.SharedTargets.IsEmpty);
}
