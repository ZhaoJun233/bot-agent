using BotAgent.Domain.Conversation;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;

namespace BotAgent.Engine.Memory;

internal static class UnifiedMemoryPolicy
{
    public static bool Reliable(UnifiedMemoryContext context) =>
        context.IsReliableIdentity && context.Actor is { } actor && ValidParticipant(actor)
        && ValidScope(new(context.Driver, context.Conversation))
        && actor.PlatformId == context.Conversation.PlatformId
        && actor.AccountScope == context.Conversation.AccountScope;

    public static bool ValidParticipant(ParticipantId subject) =>
        Atom(subject.PlatformId) && Atom(subject.AccountScope) && Atom(subject.NativeUserId);

    public static bool ValidScope(UnifiedMemoryScope scope) =>
        scope is not null && Atom(scope.Driver.PlatformId) && Atom(scope.Driver.AccountScope)
        // Empty identifies the canonical default instance; it is never a wildcard for named instances.
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
        _ => false
    };

    public static UnifiedMemoryStatus CheckRequest(UnifiedMemoryContext context, UnifiedMemoryRequest request,
        UnifiedMemoryScope source)
    {
        if (!Reliable(context)) return UnifiedMemoryStatus.UnreliableIdentity;
        if (request is null || !request.ExplicitSelfExpression) return UnifiedMemoryStatus.ExplicitRequestRequired;
        if (!ValidFact(request.Fact)) return UnifiedMemoryStatus.InvalidFact;
        if (request.Targets.IsDefault || request.Targets.Length > 8 || request.Targets.Distinct().Count() != request.Targets.Length
            || request.Targets.Any(target => !ValidScope(target) || !SameBoundary(source, target) || target == source))
            return UnifiedMemoryStatus.InvalidTarget;
        return UnifiedMemoryStatus.Success;
    }

    public static bool CanRead(UnifiedMemoryEntry entry, UnifiedMemoryContext context) =>
        entry.Confirmed && entry.Subject == context.Actor
        && ((entry.AllowSourceLocal && entry.Source == new UnifiedMemoryScope(context.Driver, context.Conversation))
            || entry.SharedTargets.Contains(new UnifiedMemoryScope(context.Driver, context.Conversation)));

    // Finite preference retrieval, not a general personal profile. No query means no injection.
    public static bool Relevant(UnifiedMemoryFact fact, string query) => !string.IsNullOrWhiteSpace(query)
        && fact.Category is UnifiedMemoryFactCategory.ReplyLanguage or UnifiedMemoryFactCategory.ReplyLength
            or UnifiedMemoryFactCategory.ExplanationStyle;

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
        && (entry.Confirmed ? entry.ConfirmationToken == Guid.Empty && entry.AllowSourceLocal
            : entry.ConfirmationToken != Guid.Empty && !entry.AllowSourceLocal && entry.SharedTargets.IsEmpty);
}
