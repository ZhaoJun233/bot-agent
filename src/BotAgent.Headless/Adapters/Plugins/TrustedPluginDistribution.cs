using System.Security.Cryptography;
using System.Text.Json;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>Host-owned release authority. The approved index digest must come from the host
/// distribution, never from an imported ZIP, manifest, URL, or package-state directory.</summary>
public sealed class TrustedPluginDistribution
{
    private readonly Dictionary<(string Id, string Version), string> _releases;
    private readonly HashSet<string> _ids;
    internal static TrustedPluginDistribution Empty { get; } = new(new(), "none");
    public string Fingerprint { get; }
    internal IReadOnlyList<PluginPackageManifest> Releases { get; private init; } = [];
    public PluginResourceAuthorizations Resources { get; private init; } = PluginResourceAuthorizations.Empty;

    private TrustedPluginDistribution(Dictionary<(string Id, string Version), string> releases, string fingerprint)
    { _releases = releases; _ids = releases.Keys.Select(key => key.Id).ToHashSet(StringComparer.Ordinal); Fingerprint = fingerprint; }

    public static TrustedPluginDistribution Read(byte[] indexBytes, string approvedSha256)
    {
        ArgumentNullException.ThrowIfNull(indexBytes);
        // Copy before verifying: caller-owned buffers cannot change between hash and parse.
        if (indexBytes.Length > 262144) throw new InvalidDataException("Distribution index too large.");
        var bytes = indexBytes.ToArray();
        var digest = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(digest, approvedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Distribution index digest differs from host approval.");
        using var document = JsonDocument.Parse(bytes);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid distribution index.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate distribution field.");
        var index = document.Deserialize<DistributionIndex>(PluginPackageValidation.JsonOptions);
        if (index is null || index.FormatVersion != 1 || index.Releases is null || index.Releases.Count > 128)
            throw new InvalidDataException("Unsupported distribution index.");
        var releases = new Dictionary<(string, string), string>();
        foreach (var manifest in index.Releases)
        {
            PluginPackageValidation.Validate(manifest, checkCompatibility: true, allowReserved: true);
            if (!PluginPackageValidation.IsReservedId(manifest.Id) ||
                !releases.TryAdd((manifest.Id, manifest.Version), PluginLifecycleManifest.Fingerprint(manifest)))
                throw new InvalidDataException("Distribution releases require unique reserved identities and versions.");
        }
        return new(releases, digest) { Resources = new PluginResourceAuthorizations(index.Releases), Releases = index.Releases };
    }

    internal void ValidateId(string id) => PluginPackageValidation.ValidateId(id, _ids.Contains(id));

    internal void Validate(PluginPackageManifest manifest, bool checkCompatibility = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var reserved = PluginPackageValidation.IsReservedId(manifest.Id);
        PluginPackageValidation.Validate(manifest, checkCompatibility,
            allowReserved: reserved);
        if (reserved) EnsureApproved(manifest);
    }

    internal void EnsureApproved(PluginPackageManifest manifest)
    {
        if (!IsApprovedRelease(manifest))
            throw new InvalidDataException("Reserved release differs from host-approved distribution.");
    }

    internal bool IsApprovedRelease(PluginPackageManifest manifest)
        => _releases.TryGetValue((manifest.Id, manifest.Version), out var fingerprint)
            && fingerprint == PluginLifecycleManifest.Fingerprint(manifest);

    private sealed record DistributionIndex(int FormatVersion, IReadOnlyList<PluginPackageManifest> Releases);
}
