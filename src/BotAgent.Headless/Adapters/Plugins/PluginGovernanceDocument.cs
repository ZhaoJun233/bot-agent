using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace BotAgent.Adapters.Plugins;

internal sealed record PluginGovernanceRecord(string Id, string Version, string Sha256, string Status,
    string Reason, string UpdatedAt);
internal sealed record PluginGovernanceDocument(int SchemaVersion, string GeneratedAt, PluginGovernanceRecord[] Entries)
{
    internal const int MaxBytes = 2 * 1024 * 1024;
    internal const int MaxEntries = 1024;
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal const int MaxCacheBytes = 64 * 1024 * 1024;
    internal static PluginGovernanceDocument Parse(byte[] bytes, bool cache = false)
    {
        if (bytes.Length > (cache ? MaxCacheBytes : MaxBytes)) throw Invalid();
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        var root = doc.RootElement;
        Fields(root, "schemaVersion", "generatedAt", "entries");
        if (!root.GetProperty("schemaVersion").TryGetInt32(out var schema) || schema != 1) throw Invalid();
        var generated = Text(root, "generatedAt", 40);
        var time = Timestamp(generated);
        var entries = root.GetProperty("entries");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > (cache ? 131072 : MaxEntries)) throw Invalid();
        var records = new List<PluginGovernanceRecord>();
        var identities = new HashSet<(string, string, string)>();
        foreach (var item in entries.EnumerateArray())
        {
            Fields(item, "id", "version", "sha256", "status", "reason", "updatedAt");
            var id = Text(item, "id", 80);
            PluginPackageValidation.ValidateId(id);
            var version = Text(item, "version", 96);
            if (!Regex.IsMatch(version, @"^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")) throw Invalid();
            var hash = Text(item, "sha256", 64);
            if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw Invalid();
            var status = Text(item, "status", 16);
            if (status is not ("deprecated" or "withdrawn" or "revoked")) throw Invalid();
            var reason = Text(item, "reason", 500);
            var updated = Text(item, "updatedAt", 40);
            if (Timestamp(updated) > time || !identities.Add((id, version, hash))) throw Invalid();
            records.Add(new(id, version, hash, status, reason, updated));
        }
        return new(1, generated, records.ToArray());
    }

    internal static DateTimeOffset Timestamp(string value)
    {
        if (!Regex.IsMatch(value, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?Z$") ||
            !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) throw Invalid();
        return time;
    }
    private static string Text(JsonElement element, string key, int max)
    {
        var value = element.GetProperty(key);
        if (value.ValueKind != JsonValueKind.String) throw Invalid();
        var text = value.GetString()!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > max || text.Any(char.IsControl)) throw Invalid();
        return text;
    }
    private static void Fields(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!allowed.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name)) throw Invalid();
        if (seen.Count != allowed.Length) throw Invalid();
    }
    internal static InvalidDataException Invalid() => new("plugin_governance_malformed");
}
