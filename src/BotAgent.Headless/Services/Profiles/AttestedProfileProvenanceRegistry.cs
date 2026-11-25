using System.Collections.Concurrent;
using System.Text.Json;
using BotAgent.Domain.Configuration;
using BotAgent.Domain.Memory;
using BotAgent.Domain.Messaging;

namespace BotAgent.Services.Profiles;

/// <summary>Metadata-only provenance for an attested scoped profile key (<c>legacy-profile:v1:...</c>).</summary>
public sealed record AttestedProfileProvenance(
    DriverKey Driver,
    ParticipantId Subject,
    ConversationId Conversation,
    string ConversationKey,
    long UpdatedUnix);

/// <summary>
/// Tracks transport-attested provenance metadata for scoped profile keys without storing message text or nicknames.
/// Backed by optional host meta persistence delegates (zero direct storage references in Services).
/// </summary>
public sealed class AttestedProfileProvenanceRegistry
{
    public const string MetaKey = "attested_profile_provenance:v1";
    private const int MaxEntries = 2048;
    private const int MaxPersistedEntries = 2048;

    private readonly ConcurrentDictionary<string, AttestedProfileProvenance> _entries = new(StringComparer.Ordinal);
    private readonly Func<string?>? _readMeta;
    private readonly Action<string>? _writeMeta;
    private readonly object _persistGate = new();

    public AttestedProfileProvenanceRegistry(Func<string?>? readMeta = null, Action<string>? writeMeta = null)
    {
        _readMeta = readMeta;
        _writeMeta = writeMeta;
        LoadFromMeta();
    }

    public int Count => _entries.Count;

    public void Record(string scopedKey, UnifiedMemoryContext context, string conversationKey)
    {
        if (!context.IsReliableIdentity || string.IsNullOrWhiteSpace(scopedKey)
            || context.Actor is not { } actor || string.IsNullOrWhiteSpace(conversationKey))
            return;
        if (!LegacyProfileScopeKey.IsScopedKey(scopedKey)
            || !string.Equals(LegacyProfileScopeKey.TryCreate(context.Driver, actor, context.Conversation), scopedKey, StringComparison.Ordinal))
            return;

        if (_entries.Count >= MaxEntries && !_entries.ContainsKey(scopedKey))
        {
            foreach (var stale in _entries.OrderBy(kv => kv.Value.UpdatedUnix).Take(64).Select(kv => kv.Key).ToArray())
                _entries.TryRemove(stale, out _);
        }

        var next = new AttestedProfileProvenance(
            context.Driver,
            actor,
            context.Conversation,
            conversationKey.Trim(),
            Clock.UtcNow.ToUnixTimeSeconds());
        var changed = !_entries.TryGetValue(scopedKey, out var existing)
            || existing.Driver != next.Driver
            || existing.Subject != next.Subject
            || existing.Conversation != next.Conversation
            || !string.Equals(existing.ConversationKey, next.ConversationKey, StringComparison.Ordinal);
        _entries[scopedKey] = next;
        if (changed) PersistToMeta();
    }

    public bool TryGet(string scopedKey, out AttestedProfileProvenance provenance)
    {
        provenance = null!;
        return !string.IsNullOrWhiteSpace(scopedKey) && _entries.TryGetValue(scopedKey, out provenance!);
    }

    public void RemoveByConversation(string conversationKey)
    {
        if (string.IsNullOrWhiteSpace(conversationKey)) return;
        var removed = false;
        foreach (var pair in _entries)
        {
            if (string.Equals(pair.Value.ConversationKey, conversationKey, StringComparison.Ordinal)
                && _entries.TryRemove(pair.Key, out _))
            {
                removed = true;
            }
        }
        if (removed) PersistToMeta();
    }

    private sealed record PersistedProvenanceItem(
        string Key,
        DriverKey Driver,
        ParticipantId Subject,
        ConversationId Conversation,
        string ConversationKey,
        long UpdatedUnix);

    private void LoadFromMeta()
    {
        if (_readMeta is null) return;
        try
        {
            var raw = _readMeta();
            if (string.IsNullOrWhiteSpace(raw)) return;
            var items = JsonSerializer.Deserialize<List<PersistedProvenanceItem>>(raw);
            if (items is null) return;
            foreach (var item in items.Take(MaxEntries))
            {
                if (!LegacyProfileScopeKey.IsScopedKey(item.Key)
                    || string.IsNullOrWhiteSpace(item.ConversationKey)
                    || !string.Equals(LegacyProfileScopeKey.TryCreate(item.Driver, item.Subject, item.Conversation), item.Key, StringComparison.Ordinal))
                    continue;
                _entries[item.Key] = new(item.Driver, item.Subject, item.Conversation, item.ConversationKey, item.UpdatedUnix);
            }
        }
        catch
        {
            // Corrupted metadata never manufactures provenance; leave registry empty.
        }
    }

    private void PersistToMeta()
    {
        if (_writeMeta is null) return;
        lock (_persistGate)
        {
            try
            {
                var snapshot = _entries
                    .OrderByDescending(kv => kv.Value.UpdatedUnix)
                    .Take(MaxPersistedEntries)
                    .Select(kv => new PersistedProvenanceItem(
                        kv.Key, kv.Value.Driver, kv.Value.Subject, kv.Value.Conversation, kv.Value.ConversationKey, kv.Value.UpdatedUnix))
                    .ToList();
                _writeMeta(JsonSerializer.Serialize(snapshot));
            }
            catch
            {
                // Best-effort durable cache; in-memory entries remain bound to live traffic.
            }
        }
    }
}
