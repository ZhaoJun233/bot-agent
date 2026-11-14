using System.Text.Json;
using System.Text.Json.Nodes;
using BotAgent.Domain.Qq;

namespace BotAgent.Adapters.Persistence;

// Typed indexes share the legacy allocator's lock and atomic snapshot publication.
// No second metadata file can commit independently of its aliases.
public sealed partial class OfficialIdMap
{
    private readonly bool _typedOfficial;
    private string? _snapshotSha256;
    private sealed record OfficialBinding(string Encoded, OfficialIdentityKey Identity, long Alias, string? EvidenceSha256);
    private readonly Dictionary<string, OfficialBinding> _officialBindings = new(StringComparer.Ordinal);
    private readonly Dictionary<long, OfficialBinding> _officialAliases = new();

    internal static OfficialIdMap OpenOfficial(string path)
        => new(path, Channels.AliasBase, Channels.AliasBase + 2_000_000_000_000L, false, null, typedOfficial: true);
    internal string? SnapshotSha256 { get { lock (_gate) return _snapshotSha256; } }
    internal int UnclaimedOfficialCount { get { lock (_gate) return _toAlias.Count - _officialBindings.Count; } }

    internal long AliasForOfficial(OfficialIdentityKey identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var encoded = OfficialIdentityCodec.Encode(identity);
        lock (_gate)
        {
            if (_officialBindings.TryGetValue(encoded, out var existing)) return existing.Alias;
            if (_toAlias.TryGetValue(identity.NativeId, out var legacy) && !_officialAliases.ContainsKey(legacy))
                throw new InvalidOperationException("identity_migration_required");
            if (_toAlias.ContainsKey(encoded)) throw new InvalidOperationException("identity_conflict");
            var alias = Allocate(encoded);
            var binding = new OfficialBinding(encoded, identity, alias, null);
            _toAlias.Add(encoded, alias);
            _toOriginal.Add(alias, encoded);
            AddOfficialBinding(binding);
            try { SaveLocked(); }
            catch
            {
                RemoveOfficialBinding(binding);
                _toAlias.Remove(encoded);
                _toOriginal.Remove(alias);
                throw;
            }
            return alias;
        }
    }

    internal bool TryGetOfficialAlias(OfficialIdentityKey identity, out long alias)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            var found = _officialBindings.TryGetValue(OfficialIdentityCodec.Encode(identity), out var binding);
            alias = found ? binding!.Alias : 0;
            return found;
        }
    }

    internal string? OfficialOriginalOf(long alias, OfficialIdentityScope expectedScope)
        => OfficialIdentityOf(alias, expectedScope)?.NativeId;

    internal OfficialIdentityKey? OfficialIdentityOf(long alias, OfficialIdentityScope expectedScope)
    {
        ArgumentNullException.ThrowIfNull(expectedScope);
        lock (_gate) return _officialAliases.TryGetValue(alias, out var binding) && binding.Identity.Scope == expectedScope
            ? binding.Identity : null;
    }

    internal void ImportOfficialLegacy(string expectedSnapshotSha256, IReadOnlyCollection<OfficialLegacyBinding> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        lock (_gate)
        {
            if (!OfficialIdentityCodec.IsDigest(expectedSnapshotSha256) || expectedSnapshotSha256 != _snapshotSha256)
                throw new InvalidOperationException("identity_snapshot_changed");
            var additions = ValidateOfficialClaims(claims.ToArray());
            if (additions.Count == 0) return;
            foreach (var binding in additions) AddOfficialBinding(binding);
            try { SaveLocked(); }
            catch
            {
                foreach (var binding in additions) RemoveOfficialBinding(binding);
                throw;
            }
        }
    }

    internal int PreviewOfficialLegacy(string expectedSnapshotSha256, IReadOnlyCollection<OfficialLegacyBinding> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        lock (_gate)
        {
            if (!OfficialIdentityCodec.IsDigest(expectedSnapshotSha256) || expectedSnapshotSha256 != _snapshotSha256)
                throw new InvalidOperationException("identity_snapshot_changed");
            return ValidateOfficialClaims(claims.ToArray()).Count;
        }
    }

    private List<OfficialBinding> ValidateOfficialClaims(OfficialLegacyBinding[] claims)
    {
        var additions = new List<OfficialBinding>();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new HashSet<long>();
        foreach (var claim in claims)
        {
            if (claim?.Identity is null || !OfficialIdentityCodec.IsDigest(claim.EvidenceSha256))
                throw new InvalidOperationException("identity_claim_invalid");
            var encoded = OfficialIdentityCodec.Encode(claim.Identity);
            if (!keys.Add(encoded) || !aliases.Add(claim.Alias)
                || !_toOriginal.TryGetValue(claim.Alias, out var raw) || raw != claim.Identity.NativeId)
                throw new InvalidOperationException("identity_claim_conflict");
            var binding = new OfficialBinding(encoded, claim.Identity, claim.Alias, claim.EvidenceSha256);
            if (_officialBindings.TryGetValue(encoded, out var prior))
            {
                if (prior != binding) throw new InvalidOperationException("identity_claim_conflict");
                continue;
            }
            if (_officialAliases.ContainsKey(claim.Alias)) throw new InvalidOperationException("identity_claim_conflict");
            additions.Add(binding);
        }
        return additions;
    }

    internal string ExportOfficialLegacy()
    {
        lock (_gate)
        {
            var map = new JsonObject();
            foreach (var (stored, alias) in _toAlias)
            {
                var raw = _officialAliases.TryGetValue(alias, out var binding) ? binding.Identity.NativeId : stored;
                if (binding?.Identity.MessageIndex is not null) throw new InvalidOperationException("identity_rollback_conflict");
                if (map.ContainsKey(raw)) throw new InvalidOperationException("identity_rollback_conflict");
                map.Add(raw, JsonValue.Create(alias));
            }
            return new JsonObject { ["aliasBase"] = _aliasBase, ["map"] = map }.ToJsonString();
        }
    }

    private void ReadOfficialIndex(JsonElement root)
    {
        if (!root.TryGetProperty("officialIdentities", out var index)) return;
        RequireProperties(index, "version", "bindings");
        if (index.GetProperty("version").ValueKind != JsonValueKind.Number
            || !index.GetProperty("version").TryGetInt32(out var version) || version != 1
            || index.GetProperty("bindings").ValueKind != JsonValueKind.Array) throw OfficialIdentityCodec.Invalid();
        foreach (var entry in index.GetProperty("bindings").EnumerateArray())
        {
            RequireProperties(entry, "key", "alias", "evidenceSha256");
            if (entry.GetProperty("key").ValueKind != JsonValueKind.String
                || entry.GetProperty("alias").ValueKind != JsonValueKind.Number
                || !entry.GetProperty("alias").TryGetInt64(out var alias)) throw OfficialIdentityCodec.Invalid();
            var encoded = entry.GetProperty("key").GetString()!;
            var identity = OfficialIdentityCodec.Decode(encoded);
            var evidence = entry.GetProperty("evidenceSha256");
            if (evidence.ValueKind is not (JsonValueKind.Null or JsonValueKind.String)) throw OfficialIdentityCodec.Invalid();
            var digest = evidence.ValueKind == JsonValueKind.Null ? null : evidence.GetString();
            if (digest is not null && !OfficialIdentityCodec.IsDigest(digest)
                || !_toOriginal.TryGetValue(alias, out var raw) || raw != (digest is null ? encoded : identity.NativeId)
                || _officialBindings.ContainsKey(encoded) || _officialAliases.ContainsKey(alias)) throw OfficialIdentityCodec.Invalid();
            AddOfficialBinding(new(encoded, identity, alias, digest));
        }
    }

    private JsonObject WriteOfficialIndex()
    {
        var bindings = new JsonArray();
        foreach (var entry in _officialBindings.Values)
            bindings.Add(new JsonObject { ["key"] = entry.Encoded, ["alias"] = entry.Alias, ["evidenceSha256"] = entry.EvidenceSha256 });
        return new JsonObject { ["version"] = 1, ["bindings"] = bindings };
    }

    private static void RequireProperties(JsonElement element, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object) throw OfficialIdentityCodec.Invalid();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!names.Add(property.Name) || !required.Contains(property.Name, StringComparer.Ordinal)) throw OfficialIdentityCodec.Invalid();
        if (names.Count != required.Length) throw OfficialIdentityCodec.Invalid();
    }

    private void AddOfficialBinding(OfficialBinding binding)
    {
        _officialBindings.Add(binding.Encoded, binding);
        _officialAliases.Add(binding.Alias, binding);
    }

    private void RemoveOfficialBinding(OfficialBinding binding)
    {
        _officialBindings.Remove(binding.Encoded);
        _officialAliases.Remove(binding.Alias);
    }
}
