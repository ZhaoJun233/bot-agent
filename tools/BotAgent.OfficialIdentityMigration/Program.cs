using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using BotAgent.Adapters.Persistence;
using BotAgent.Domain.Qq;

return MigrationCommand.Run(args, Console.Out);

public static class MigrationCommand
{
    private sealed record Binding([property: JsonRequired] OfficialIdentityKey Identity, [property: JsonRequired] long Alias);
    private sealed record Index([property: JsonRequired] int Version, [property: JsonRequired] string SourceSha256,
        [property: JsonRequired] Binding[] Bindings);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public static int Run(string[] args, TextWriter output)
    {
        try
        {
            if (args is ["preview", var map, var index]) return Migrate(map, index, null, output);
            if (args is ["migrate", var source, var proof, var target]) return Migrate(source, proof, target, output);
            if (args is ["rollback", var current, var legacy])
            {
                var ids = new OfficialIdentityMap(current);
                var candidate = Encoding.UTF8.GetBytes(ids.ExportLegacySnapshot());
                PublishNew(legacy, candidate);
                Print(output, new { status = "candidate_created", count = ids.Count, sha256 = Digest(candidate) });
                return 0;
            }
            Print(output, new { status = "rejected", reason = "usage", commands = new[] {
                "preview <map> <verified-index>", "migrate <map> <verified-index> <new-candidate>", "rollback <typed-map> <new-candidate>" } });
            return 2;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Print(output, new { status = "rejected", reason = "io_unavailable" });
            return 3;
        }
        catch (Exception error) when (error is InvalidDataException or JsonException or ArgumentException or InvalidOperationException)
        {
            var known = new[] { "identity_snapshot_changed", "identity_claim_invalid", "identity_claim_conflict", "identity_rollback_conflict" };
            Print(output, new { status = "rejected", reason = known.Contains(error.Message) ? error.Message : "invalid_input" });
            return 2;
        }
    }

    private static int Migrate(string mapPath, string indexPath, string? outputPath, TextWriter output)
    {
        var source = File.ReadAllBytes(mapPath);
        var proof = File.ReadAllBytes(indexPath);
        var proofDigest = Digest(proof);
        using (var document = JsonDocument.Parse(proof)) RejectDuplicateProperties(document.RootElement);
        var index = JsonSerializer.Deserialize<Index>(proof, JsonOptions) ?? throw new InvalidDataException();
        if (index.Version != 1 || index.Bindings is null) throw new InvalidDataException();
        if (index.SourceSha256 != Digest(source))
            throw new InvalidOperationException("identity_snapshot_changed");
        var ids = new OfficialIdentityMap(mapPath);
        var claims = index.Bindings.Select(binding => binding?.Identity is not null
            ? new OfficialLegacyBinding(binding.Identity, binding.Alias, proofDigest) : throw new InvalidDataException()).ToArray();
        var attributed = ids.PreviewVerifiedLegacy(index.SourceSha256, claims);
        if (outputPath is not null)
        {
            // The source remains untouched. Even a failed import can only affect this private scratch file.
            var destination = Path.GetFullPath(outputPath);
            var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteNew(temporary, source);
                var candidate = new OfficialIdentityMap(temporary);
                candidate.ImportVerifiedLegacy(index.SourceSha256, claims);
                File.Move(temporary, destination, overwrite: false);
                Print(output, new { status = "candidate_created", count = candidate.Count, attributed, legacy = candidate.LegacyCount,
                    sourceSha256 = index.SourceSha256, evidenceSha256 = proofDigest, sha256 = candidate.SnapshotSha256 });
            }
            finally { DeleteScratch(temporary); }
        }
        else Print(output, new { status = "ready", count = ids.Count, attributed, legacy = ids.LegacyCount - attributed,
            sourceSha256 = index.SourceSha256, evidenceSha256 = proofDigest });
        return 0;
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static void PublishNew(string path, byte[] bytes)
    {
        var destination = Path.GetFullPath(path);
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { WriteNew(temporary, bytes); File.Move(temporary, destination, overwrite: false); }
        finally { DeleteScratch(temporary); }
    }

    private static void WriteNew(string path, byte[] bytes)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
        file.Flush(flushToDisk: true);
    }
    private static void DeleteScratch(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private static string Digest(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static void Print(TextWriter output, object value) => output.WriteLine(JsonSerializer.Serialize(value));
}
