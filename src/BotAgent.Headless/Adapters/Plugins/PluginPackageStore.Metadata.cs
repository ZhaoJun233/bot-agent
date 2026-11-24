using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

internal sealed record HostDistributionUpgradeCandidate(
    InstalledPluginPackage State,
    string OldSha256Prefix,
    bool KeepUninstalled);

internal sealed partial class PluginPackageStore
{
    private const int MaximumMetadataBytes = PluginStateEnvelope.MaximumBytes;
    private static readonly HashSet<string> StateFields = new(StringComparer.OrdinalIgnoreCase)
        { "id", "active", "previous", "pending", "pendingUninstall" };

    // Execution and mutation always use this strict reader, never the directory observation.
    internal async Task<InstalledPluginPackage> ReadAsync(string id, CancellationToken ct)
    {
        Distribution.ValidateId(id);
        var bytes = await ReadMetadataBytesAsync(id, ct).ConfigureAwait(false);
        return ParseMetadata(id, bytes, out _);
    }

    /// <summary>
    /// Structural verification of a reserved host-distribution envelope whose release digest is no longer
    /// in the current host's approved index (e.g., after a host rebuild). Still enforces envelope format,
    /// reader compatibility, execution SHA-256 integrity, projection matching, and host-distribution:// origin
    /// across all non-null slots. Any tampering, corruption, future reader version, or third-party origin returns null.
    /// </summary>
    internal async Task<HostDistributionUpgradeCandidate?> TryReadUnapprovedHostDistributionAsync(
        string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!PluginPackageValidation.IsReservedId(id)) return null;
        try { Distribution.ValidateId(id); }
        catch (InvalidDataException) { return null; }

        try
        {
            var bytes = await ReadMetadataBytesAsync(id, ct).ConfigureAwait(false);
            if (bytes is null || bytes.Length > MaximumMetadataBytes) return null;
            ReadOnlyMemory<byte> json = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf })
                ? bytes.AsMemory(3) : bytes;
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var root = document.RootElement;
            if (!PluginStateEnvelope.IsEnvelope(root)) return null;
            var recovery = PluginStateEnvelope.Read(id, root);
            if (recovery.MinimumReaderVersion > PluginStateEnvelope.ReaderVersion) return null;
            PluginStateEnvelope.VerifyExecution(recovery, root);
            var state = ParseExecution(id, root.GetProperty("execution"),
                manifest => PluginPackageValidation.Validate(manifest, checkCompatibility: false, allowReserved: true,
                    allowHistoricalHostContractDigest: true));
            PluginStateEnvelope.VerifyProjection(recovery, state);
            var slots = new[] { state.Active, state.Pending, state.Previous }
                .Where(m => m is not null).Select(m => m!).ToArray();
            if (slots.Length == 0 || slots.All(Distribution.IsApprovedRelease)) return null;
            foreach (var slot in slots)
            {
                if (slot.Id != id
                    || !string.Equals(slot.DownloadUrl, $"host-distribution://{id}/{slot.Version}", StringComparison.Ordinal))
                    return null;
            }
            var keepUninstalled = state.PendingUninstall || (state.Active is null && state.Pending is null);
            var primary = state.Active ?? state.Pending ?? state.Previous;
            var oldShaPrefix = primary?.Sha256 is { Length: >= 8 } sha ? sha[..8].ToLowerInvariant() : "unknown";
            return new(state, oldShaPrefix, keepUninstalled);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    internal async Task<InstalledPluginPackage> ReadDirectoryEntryAsync(string id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { Distribution.ValidateId(id); }
        catch (InvalidDataException)
        {
            // An arbitrary directory name is not a plugin identity or safe display text.
            var reference = "unrecognized-" + Digest(Encoding.UTF8.GetBytes(id))[..16];
            return Failed(reference, "plugin_metadata_invalid_id", "sha256:" + Digest(Encoding.UTF8.GetBytes(id)));
        }

        byte[]? bytes = null;
        PluginPackageRecovery? recovery = null;
        try
        {
            bytes = await ReadMetadataBytesAsync(id, ct).ConfigureAwait(false);
            return ParseMetadata(id, bytes, out recovery);
        }
        catch (Exception exception) when (exception is JsonException or InvalidDataException
            or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            // Oversized records are deliberately bounded; the fingerprint describes the read prefix.
            // Neither this observation nor a parser failure may rewrite the original file.
            var fingerprint = bytes is null ? "unreadable" :
                (bytes.Length > MaximumMetadataBytes ? "prefix:" : "sha256:") + Digest(bytes);
            var code = bytes?.Length > MaximumMetadataBytes ? "plugin_metadata_too_large"
                : recovery?.MinimumReaderVersion > PluginStateEnvelope.ReaderVersion
                    ? "plugin_state_reader_incompatible" : "plugin_metadata_unreadable";
            return Failed(id, code, fingerprint, recovery);
        }
    }

    private static InstalledPluginPackage Failed(string id, string code, string fingerprint, PluginPackageRecovery? recovery = null)
        => new(id) { MetadataFailure = new(id, code, fingerprint) { Recovery = recovery } };

    private Task<byte[]?> ReadMetadataBytesAsync(string id, CancellationToken ct)
        => PluginPackageFiles.ReadPrefixAsync(Path.Combine(PackageDirectory(id), "installed.json"), MaximumMetadataBytes, ct);

    private InstalledPluginPackage ParseMetadata(string id, byte[]? bytes, out PluginPackageRecovery? recovery)
    {
        recovery = null;
        if (bytes is null) return new(id);
        if (bytes.Length > MaximumMetadataBytes) throw new InvalidDataException("Metadata too large.");
        // ReadAllText historically accepted the UTF-8 BOM; preserve that disk compatibility.
        ReadOnlyMemory<byte> json = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsMemory(3) : bytes;
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid package metadata.");
        var execution = document.RootElement;
        if (PluginStateEnvelope.IsEnvelope(execution))
        {
            recovery = PluginStateEnvelope.Read(id, execution);
            if (recovery.MinimumReaderVersion > PluginStateEnvelope.ReaderVersion)
                throw new InvalidDataException("plugin_state_reader_incompatible");
            PluginStateEnvelope.VerifyExecution(recovery, execution);
            execution = execution.GetProperty("execution");
        }
        var state = ParseExecution(id, execution, manifest => Distribution.Validate(manifest));
        if (recovery is not null) PluginStateEnvelope.VerifyProjection(recovery, state);
        return state;
    }

    internal static InstalledPluginPackage ParseExecution(string id, JsonElement execution, Action<PluginPackageManifest> validate)
    {
        if (execution.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid package metadata.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in execution.EnumerateObject())
            if (!StateFields.Contains(field.Name) || !seen.Add(field.Name))
                throw new InvalidDataException("Unsupported package metadata field.");
        var state = execution.Deserialize<InstalledPluginPackage>(PluginPackageValidation.JsonOptions)
            ?? throw new InvalidDataException("Invalid package metadata.");
        if (state.Id != id) throw new InvalidDataException("Package metadata identity mismatch.");
        foreach (var manifest in new[] { state.Active, state.Previous, state.Pending })
        {
            if (manifest is null) continue;
            validate(manifest);
            if (manifest.Id != id) throw new InvalidDataException("Release metadata identity mismatch.");
        }
        return state;
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
