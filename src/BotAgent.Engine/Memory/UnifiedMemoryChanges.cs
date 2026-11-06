using System.Collections.Immutable;
using BotAgent.Domain.Memory;

namespace BotAgent.Engine.Memory;

internal sealed record UnifiedMemoryChange(UnifiedMemoryState? State, UnifiedMemoryResult Result)
{
    public static UnifiedMemoryChange Deny(UnifiedMemoryStatus status) => new(null, new(status));
    public static UnifiedMemoryChange Accept(UnifiedMemoryState state, UnifiedMemoryEntry? entry = null) => new(state, new(UnifiedMemoryStatus.Success, entry));
}

internal static class UnifiedMemoryChanges
{
    public static UnifiedMemoryChange Request(UnifiedMemoryState state, UnifiedMemoryContext context,
        UnifiedMemoryRequest request, DateTimeOffset now)
    {
        var source = new UnifiedMemoryScope(context.Driver, context.Conversation);
        var status = UnifiedMemoryPolicy.CheckRequest(context, request, source);
        if (status != UnifiedMemoryStatus.Success) return UnifiedMemoryChange.Deny(status);
        if (!state.Enabled) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.Disabled);
        if (state.Suppressions.Contains(new(context.Actor!, source))) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.Suppressed);
        if (state.Entries.Length >= 10000) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.InvalidFact);
        var previous = state.Entries.FirstOrDefault(item => item.Subject == context.Actor && item.Source == source
            && item.Fact.Category == request.Fact.Category);
        if (previous?.Version == int.MaxValue) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.StaleVersion);
        var entry = Candidate(previous?.FactId ?? Guid.NewGuid(), (previous?.Version ?? 0) + 1, context.Actor!, source, request, now);
        return previous is null ? UnifiedMemoryChange.Accept(state with { Entries = state.Entries.Add(entry) }, entry)
            : Replace(state, previous, entry);
    }

    public static UnifiedMemoryChange Confirm(UnifiedMemoryState state, UnifiedMemoryContext context,
        UnifiedMemoryConfirmation confirmation, DateTimeOffset now)
    {
        var entry = state.Entries.FirstOrDefault(item => item.FactId == confirmation.FactId);
        var denial = CheckOwner(context, entry);
        if (denial is not null) return denial;
        if (!state.Enabled) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.Disabled);
        if (entry!.Version != confirmation.Version) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.StaleVersion);
        if (entry.Confirmed || entry.ConfirmationToken != confirmation.Token || confirmation.Token == Guid.Empty
            || entry.ConfirmationExpiresAt <= now || !confirmation.AllowSourceLocal
            || (entry.ProposedTargets.Any(target => target.Conversation.Kind is not Domain.Conversation.ConversationKind.PrivateChat)
                && !confirmation.AcknowledgeGroupAudience)
            || (entry.Source.Conversation.Kind is not Domain.Conversation.ConversationKind.PrivateChat && !confirmation.AcknowledgeGroupAudience))
            return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.InvalidConfirmation);
        var confirmed = entry with { Confirmed = true, AllowSourceLocal = true, SharedTargets = entry.ProposedTargets, ConfirmationToken = Guid.Empty };
        return Replace(state, entry, confirmed);
    }

    public static UnifiedMemoryChange Edit(UnifiedMemoryState state, UnifiedMemoryContext context,
        Guid id, int version, UnifiedMemoryRequest request, DateTimeOffset now)
    {
        var entry = state.Entries.FirstOrDefault(item => item.FactId == id);
        var denial = CheckOwner(context, entry);
        if (denial is not null) return denial;
        if (!state.Enabled) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.Disabled);
        if (entry!.Version != version || version == int.MaxValue) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.StaleVersion);
        var status = UnifiedMemoryPolicy.CheckRequest(context, request, entry.Source);
        if (status != UnifiedMemoryStatus.Success) return UnifiedMemoryChange.Deny(status);
        // Old body and ALL prior grants disappear in the same durable commit. Never inherit consent.
        return Replace(state, entry, Candidate(id, version + 1, entry.Subject, entry.Source, request, now));
    }

    public static UnifiedMemoryChange Revoke(UnifiedMemoryState state, UnifiedMemoryContext context, Guid id, UnifiedMemoryScope target)
    {
        var entry = state.Entries.FirstOrDefault(item => item.FactId == id);
        var denial = CheckOwner(context, entry);
        if (denial is not null) return denial;
        if (!UnifiedMemoryPolicy.ValidScope(target) || target == entry!.Source || !entry.SharedTargets.Contains(target))
            return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.InvalidTarget);
        return Replace(state, entry, entry with { SharedTargets = entry.SharedTargets.Remove(target) });
    }

    public static UnifiedMemoryChange Forget(UnifiedMemoryState state, UnifiedMemoryContext context, Guid id)
    {
        var entry = state.Entries.FirstOrDefault(item => item.FactId == id);
        var denial = CheckOwner(context, entry);
        if (denial is not null) return denial;
        var marker = new UnifiedMemorySuppression(entry!.Subject, entry.Source);
        // Conservative source-wide stop prevents any old material from being re-extracted.
        var markers = state.Suppressions.Contains(marker) ? state.Suppressions : state.Suppressions.Add(marker);
        return UnifiedMemoryChange.Accept(state with { Entries = state.Entries.Remove(entry), Suppressions = markers });
    }

    public static UnifiedMemoryChange Reject(UnifiedMemoryState state, UnifiedMemoryContext context, Guid id, DateTimeOffset now)
    {
        var entry = state.Entries.FirstOrDefault(item => item.FactId == id);
        var denial = CheckOwner(context, entry);
        if (denial is not null) return denial;
        return Replace(state, entry!, entry! with { Confirmed = false, AllowSourceLocal = false, SharedTargets = [],
            ConfirmationToken = Guid.NewGuid(), ConfirmationExpiresAt = now });
    }

    private static UnifiedMemoryChange? CheckOwner(UnifiedMemoryContext context, UnifiedMemoryEntry? entry)
    {
        if (!UnifiedMemoryPolicy.Reliable(context)) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.UnreliableIdentity);
        if (entry is null) return UnifiedMemoryChange.Deny(UnifiedMemoryStatus.NotFound);
        return UnifiedMemoryPolicy.OwnerPrivate(context, entry) ? null : UnifiedMemoryChange.Deny(UnifiedMemoryStatus.NotOwnerPrivateChat);
    }

    private static UnifiedMemoryEntry Candidate(Guid id, int version, Domain.Messaging.ParticipantId subject,
        UnifiedMemoryScope source, UnifiedMemoryRequest request, DateTimeOffset now) =>
        new(id, version, subject, source, request.Fact, request.Targets, Guid.NewGuid(), now.AddMinutes(15), false, false, []);

    private static UnifiedMemoryChange Replace(UnifiedMemoryState state, UnifiedMemoryEntry old, UnifiedMemoryEntry replacement) =>
        UnifiedMemoryChange.Accept(state with { Entries = state.Entries.Replace(old, replacement) }, replacement);
}
