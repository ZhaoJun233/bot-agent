using BotAgent.Domain.Qq;

namespace BotAgent.Adapters.Persistence;

/// <summary>A claim from an operator-verified offline index. Its digest binds evidence; it does not verify that evidence.</summary>
public sealed record OfficialLegacyBinding(OfficialIdentityKey Identity, long Alias, string EvidenceSha256)
{
    public override string ToString() => "OfficialLegacyBinding";
}

/// <summary>
/// App/type-aware official identities with legacy reservations. A single process owns the file.
/// Runtime callers allocate/resolve typed identities; only an offline migration may claim old bindings.
/// </summary>
public sealed class OfficialIdentityMap : BotAgent.Domain.Ports.IOfficialIdentityMap
{
    private readonly OfficialIdMap _store;
    public OfficialIdentityMap(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _store = OfficialIdMap.OpenOfficial(path);
    }

    public int Count => _store.Count;
    public int LegacyCount => _store.UnclaimedOfficialCount;
    public string? SnapshotSha256 => _store.SnapshotSha256;
    public long AliasFor(OfficialIdentityKey identity) => _store.AliasForOfficial(identity);
    public bool TryGetAlias(OfficialIdentityKey identity, out long alias) => _store.TryGetOfficialAlias(identity, out alias);
    public string? OriginalOf(long alias, OfficialIdentityScope expectedScope) => _store.OfficialOriginalOf(alias, expectedScope);
    public OfficialIdentityKey? IdentityOf(long alias, OfficialIdentityScope expectedScope) => _store.OfficialIdentityOf(alias, expectedScope);

    /// <summary>Validate an offline index without changing memory or disk; returns newly attributed bindings.</summary>
    public int PreviewVerifiedLegacy(string expectedSnapshotSha256, IReadOnlyCollection<OfficialLegacyBinding> bindings)
        => _store.PreviewOfficialLegacy(expectedSnapshotSha256, bindings);

    /// <summary>
    /// Offline-only operation: caller must stop all writers and verify the source index before calling.
    /// Current settings, events, names and a digest alone are not ownership evidence.
    /// </summary>
    public void ImportVerifiedLegacy(string expectedSnapshotSha256, IReadOnlyCollection<OfficialLegacyBinding> bindings)
        => _store.ImportOfficialLegacy(expectedSnapshotSha256, bindings);

    /// <summary>Pure downgrade preview including every binding; throws if the old format would merge identities.</summary>
    public string ExportLegacySnapshot() => _store.ExportOfficialLegacy();
}
