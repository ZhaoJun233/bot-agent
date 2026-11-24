using System.Collections.Immutable;
using System.Text.Json;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Identity;

namespace BotAgent.Storage;

/// <summary>
/// ADR-0006 可选跨平台本人身份关联存储（默认关闭，Schema v15 兼容）：
/// 存入 <c>meta['identity_links:v1']</c>，只存显式确认过的跨端逻辑映射与共享偏好开关，绝不存放聊天记录或群管权限。
/// </summary>
public sealed class IdentityLinkStore : IIdentityLinkStore
{
    private const string MetaKey = "identity_links:v1";
    private readonly object _gate = new();

    private sealed record PersistedIdentityLinks(
        bool Enabled,
        IdentitySharePreferences? Preferences,
        List<IdentityLinkRecord>? Links,
        string? WorkspaceCanonicalPersonId = null);

    public IdentityLinkState Load()
    {
        lock (_gate)
        {
            return Parse(AppDatabase.GetMeta(MetaKey));
        }
    }

    public void Save(IdentityLinkState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_gate)
        {
            var payload = JsonSerializer.Serialize(new PersistedIdentityLinks(
                state.Enabled,
                state.Preferences,
                state.Links.ToList(),
                state.WorkspaceCanonicalPersonId));
            AppDatabase.Write(conn =>
            {
                var existingRaw = AppDatabase.GetMetaInTransaction(conn, MetaKey);
                _ = Parse(existingRaw, out var corrupted);
                if (corrupted && !string.IsNullOrWhiteSpace(existingRaw))
                    AppDatabase.SetMetaInTransaction(conn, MetaKey + ":corrupt", existingRaw);
                AppDatabase.SetMetaInTransaction(conn, MetaKey, payload);
            });
        }
    }

    private static IdentityLinkState Parse(string? raw) => Parse(raw, out _);

    private static IdentityLinkState Parse(string? raw, out bool corrupted)
    {
        corrupted = false;
        if (string.IsNullOrWhiteSpace(raw)) return IdentityLinkState.EmptyDisabled();
        try
        {
            var parsed = JsonSerializer.Deserialize<PersistedIdentityLinks>(raw);
            if (parsed is null)
            {
                corrupted = true;
                return IdentityLinkState.EmptyDisabled();
            }
            var links = (parsed.Links ?? new List<IdentityLinkRecord>())
                .Where(r => r is not null && !string.IsNullOrWhiteSpace(r.CanonicalPersonId) && r.Endpoint is not null && r.Endpoint.IsValid())
                .ToImmutableArray();
            var wsId = string.IsNullOrWhiteSpace(parsed.WorkspaceCanonicalPersonId)
                ? null
                : parsed.WorkspaceCanonicalPersonId.Trim();
            return new IdentityLinkState(
                parsed.Enabled,
                parsed.Preferences ?? new IdentitySharePreferences(),
                links,
                wsId);
        }
        catch (JsonException)
        {
            corrupted = true;
            return IdentityLinkState.EmptyDisabled();
        }
    }
}
