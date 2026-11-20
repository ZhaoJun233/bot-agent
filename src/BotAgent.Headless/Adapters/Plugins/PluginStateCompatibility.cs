using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Plugins;

[assembly: AssemblyMetadata("BotAgent.PluginStateReaderVersion", "12")]

namespace BotAgent.Adapters.Plugins;

/// <summary>Read-only disk preflight. The candidate PE is inspected as data, never loaded or started.</summary>
public static class PluginStateCompatibility
{
    private const string ReaderKey = "BotAgent.PluginStateReaderVersion";

    public static async Task<PluginStateCompatibilityReport> InspectAsync(string root, string candidateAssembly,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(root)) throw new DirectoryNotFoundException();
        var (version, digest) = await ReadCandidateAsync(candidateAssembly, ct).ConfigureAwait(false);
        var entries = new List<PluginStateCompatibilityEntry>();
        foreach (var directory in Directory.EnumerateDirectories(root).Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            if (id.StartsWith('.')) continue;
            entries.Add(await InspectEntryAsync(directory, id, version, ct).ConfigureAwait(false));
        }
        return new(version, digest, entries.All(entry => entry.Compatible), entries);
    }

    private static async Task<PluginStateCompatibilityEntry> InspectEntryAsync(string directory, string id,
        int candidateVersion, CancellationToken ct)
    {
        byte[]? bytes = null;
        PluginPackageRecovery? recovery = null;
        try
        {
            try { PluginPackageValidation.ValidateId(id, allowReserved: true); }
            catch (InvalidDataException)
            {
                var digest = PluginStateEnvelope.Digest(Encoding.UTF8.GetBytes(id));
                return new("unrecognized-" + digest[..16], false, "plugin_metadata_invalid_id", "sha256:" + digest, null);
            }
            var path = Path.Combine(directory, "installed.json");
            bytes = await PluginPackageFiles.ReadPrefixAsync(path, PluginStateEnvelope.MaximumBytes, ct).ConfigureAwait(false);
            if (bytes is null) return new(id, true, "no_state", "absent", null);
            if (bytes.Length > PluginStateEnvelope.MaximumBytes)
                return Entry(false, "plugin_metadata_too_large");
            ReadOnlyMemory<byte> json = bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes.AsMemory(3) : bytes;
            using var document = JsonDocument.Parse(json);
            var execution = document.RootElement;
            if (execution.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
            if (PluginStateEnvelope.IsEnvelope(execution))
            {
                recovery = PluginStateEnvelope.Read(id, execution);
                // Deliberately precedes even hashing/deserializing the execution block.
                if (recovery.MinimumReaderVersion > candidateVersion
                    || recovery.MinimumReaderVersion > PluginStateEnvelope.ReaderVersion)
                    return Entry(false, "plugin_state_reader_incompatible");
                PluginStateEnvelope.VerifyExecution(recovery, execution);
                execution = execution.GetProperty("execution");
            }
            var state = PluginPackageStore.ParseExecution(id, execution, manifest =>
                PluginPackageValidation.Validate(manifest, checkCompatibility: false, allowReserved: true));
            if (recovery is not null) PluginStateEnvelope.VerifyProjection(recovery, state);
            else recovery = PluginStateEnvelope.Legacy(state, bytes);
            if (recovery.MinimumReaderVersion > candidateVersion)
                return Entry(false, "plugin_state_reader_incompatible");
            return Entry(true, "readable");
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException
            or UnauthorizedAccessException or InvalidOperationException or FormatException or KeyNotFoundException)
        {
            return Entry(false, "plugin_metadata_unreadable");
        }

        PluginStateCompatibilityEntry Entry(bool compatible, string code) => new(id, compatible, code,
            bytes is null ? "unreadable" : (bytes.Length > PluginStateEnvelope.MaximumBytes ? "prefix:" : "sha256:")
                + PluginStateEnvelope.Digest(bytes), recovery);
    }

    private static async Task<(int Version, string Digest)> ReadCandidateAsync(string path, CancellationToken ct)
    {
        var bytes = await PluginPackageFiles.ReadRequiredAsync(path, 67108864, ct).ConfigureAwait(false);
        using var stream = new MemoryStream(bytes, writable: false);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        int? version = null;
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var member = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (member.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)member.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection"
                || metadata.GetString(type.Name) != nameof(AssemblyMetadataAttribute)) continue;
            var value = metadata.GetBlobReader(attribute.Value);
            if (value.ReadUInt16() != 1 || value.ReadSerializedString() != ReaderKey) continue;
            if (version is not null || !int.TryParse(value.ReadSerializedString(), out var parsed) || parsed < 1)
                throw new InvalidDataException("candidate_reader_version_invalid");
            version = parsed;
        }
        // Historical hosts without the marker support only the legacy installed.json format.
        return (version ?? 0, PluginStateEnvelope.Digest(bytes));
    }
}
