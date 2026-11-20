using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BotAgent.Domain.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>A stable recovery header plus opaque, hash-bound execution bytes in one atomic file.</summary>
internal static class PluginStateEnvelope
{
    internal const int ReaderVersion = 12;
    internal const int MaximumBytes = 131072;
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
        { "formatVersion", "minimumReaderVersion", "id", "active", "previous", "pending",
          "pendingUninstall", "executionSha256", "execution" };

    internal static byte[] Write(InstalledPluginPackage state)
    {
        var execution = JsonSerializer.SerializeToUtf8Bytes(state, PluginPackageValidation.JsonOptions);
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", 1);
            writer.WriteNumber("minimumReaderVersion", RequiredReaderVersion(state));
            writer.WriteString("id", state.Id);
            WriteReference(writer, "active", state.Active);
            WriteReference(writer, "previous", state.Previous);
            WriteReference(writer, "pending", state.Pending);
            writer.WriteBoolean("pendingUninstall", state.PendingUninstall);
            writer.WriteString("executionSha256", Digest(execution));
            writer.WritePropertyName("execution");
            writer.WriteRawValue(execution);
            writer.WriteEndObject();
        }
        var bytes = output.ToArray();
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("plugin_metadata_too_large");
        return bytes;
    }

    internal static bool IsEnvelope(JsonElement root) => root.EnumerateObject()
        .Any(field => field.Name.Equals("formatVersion", StringComparison.OrdinalIgnoreCase));

    // No execution manifest deserialization here: future fields cannot hide the safe recovery header.
    internal static PluginPackageRecovery Read(string id, JsonElement root)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in root.EnumerateObject())
            if (!Fields.Contains(field.Name) || !seen.Add(field.Name))
                throw new InvalidDataException("plugin_state_envelope_invalid");
        if (!seen.SetEquals(Fields) || root.GetProperty("formatVersion").GetInt32() != 1
            || root.GetProperty("minimumReaderVersion").GetInt32() < 1
            || root.GetProperty("id").GetString() != id)
            throw new InvalidDataException("plugin_state_envelope_invalid");
        var sha = root.GetProperty("executionSha256").GetString();
        if (!IsDigest(sha)) throw new InvalidDataException("plugin_state_envelope_invalid");
        return new(id, 1, root.GetProperty("minimumReaderVersion").GetInt32(),
            ReadReference(root.GetProperty("active")), ReadReference(root.GetProperty("previous")),
            ReadReference(root.GetProperty("pending")), root.GetProperty("pendingUninstall").GetBoolean(), sha!);
    }

    internal static void VerifyExecution(PluginPackageRecovery recovery, JsonElement root)
    {
        if (Digest(Encoding.UTF8.GetBytes(root.GetProperty("execution").GetRawText())) != recovery.ExecutionSha256)
            throw new InvalidDataException("plugin_state_execution_digest_mismatch");
    }

    internal static void VerifyProjection(PluginPackageRecovery recovery, InstalledPluginPackage state)
    {
        if (recovery.MinimumReaderVersion < RequiredReaderVersion(state)
            || recovery.Id != state.Id || recovery.Active != Reference(state.Active)
            || recovery.Previous != Reference(state.Previous) || recovery.Pending != Reference(state.Pending)
            || recovery.PendingUninstall != state.PendingUninstall)
            throw new InvalidDataException("plugin_state_recovery_mismatch");
    }

    internal static PluginPackageRecovery Legacy(InstalledPluginPackage state, byte[] execution)
        => new(state.Id, 0, RequiredReaderVersion(state) > 1 ? RequiredReaderVersion(state) : 0,
            Reference(state.Active), Reference(state.Previous), Reference(state.Pending),
            state.PendingUninstall, Digest(execution));

    private static int RequiredReaderVersion(InstalledPluginPackage state) => new[] { state.Active, state.Previous, state.Pending }
        .Select(manifest => PluginUiValidation.RequiresReaderTwelve(manifest) ? 12
            : manifest?.Configuration is not null ? 11
            : manifest?.Settings?.Any(field => field.NumberConstraints is not null) == true ? 10
            : PluginSharedContractValidation.HasIndependentHostReference(manifest) ? 9
            : manifest?.ToolApiVersion is not null ? 8
            : PluginActivationModel.Requested(manifest) || PluginActivationExecution.Requested(manifest) ? 7
            : PluginActivationWeb.Requested(manifest) ? 6
            : manifest?.DownloadUrl?.StartsWith("host-distribution://", StringComparison.Ordinal) == true ? 5
            : manifest?.Control == true ? 4 : manifest?.Data is not null ? 3 : manifest?.SharedContracts is { Count: > 0 } ? 2 : 1).Max();

    private static PluginReleaseReference? Reference(PluginPackageManifest? manifest)
        => manifest is null ? null : new(manifest.Version, manifest.Sha256.ToLowerInvariant());

    private static void WriteReference(Utf8JsonWriter writer, string name, PluginPackageManifest? manifest)
    {
        writer.WritePropertyName(name);
        JsonSerializer.Serialize(writer, Reference(manifest), PluginPackageValidation.JsonOptions);
    }

    private static PluginReleaseReference? ReadReference(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("plugin_state_reference_invalid");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
            if (field.Name is not ("version" or "sha256") || !seen.Add(field.Name))
                throw new InvalidDataException("plugin_state_reference_invalid");
        if (seen.Count != 2) throw new InvalidDataException("plugin_state_reference_invalid");
        var version = value.GetProperty("version").GetString();
        var sha = value.GetProperty("sha256").GetString();
        if (!PluginPackageValidation.IsVersion(version) || !IsDigest(sha))
            throw new InvalidDataException("plugin_state_reference_invalid");
        return new(version!, sha!);
    }

    private static bool IsDigest(string? value) => value?.Length == 64
        && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');
    internal static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
