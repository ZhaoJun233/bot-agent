using System.Numerics;
using System.Text.Json;
using BotAgent.Domain.Plugins;
using BotAgent.Panel;

namespace BotAgent.Adapters.Plugins;

internal static class PluginLifecycleManifest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal static IReadOnlyList<ZbaPluginManifestChange> Changes(PluginPackageManifest current, PluginPackageManifest? target)
    {
        var before = Fields(current); JsonElement? after = target is null ? null : Fields(target);
        var keys = before.EnumerateObject().Select(p => p.Name).Concat(after?.EnumerateObject().Select(p => p.Name) ?? []);
        return keys.Distinct(StringComparer.Ordinal).OrderBy(name => name, StringComparer.Ordinal)
            .Select(name => new ZbaPluginManifestChange(name, before.TryGetProperty(name, out var oldValue) ? oldValue.GetRawText() : null,
                after is { } next && next.TryGetProperty(name, out var newValue) ? newValue.GetRawText() : null))
            .Where(p => p.Before != p.After).ToArray();
    }
    internal static string Fingerprint(PluginPackageManifest? manifest)
        => manifest is null ? "-" : PluginPackageRevision.Hash(Fields(manifest).GetRawText());
    internal static string PackageState(InstalledPluginPackage package) => PluginPackageRevision.Hash(
        string.Join("|", package.Id, Fingerprint(package.Active), Fingerprint(package.Previous), Fingerprint(package.Pending), package.PendingUninstall));

    private static JsonElement Fields(PluginPackageManifest m)
    {
        var fields = LegacyFields(m);
        if (m.SharedContracts is not { Count: > 0 } && m.Data is null && !m.Control && m.ToolApiVersion is null
            && m.Configuration is null && m.Ui is null && m.Adoption is null && m.ExclusiveResources is not { Count: > 0 }) return fields;
        var values = fields.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        if (m.SharedContracts is { Count: > 0 })
            values["sharedContracts"] = JsonSerializer.SerializeToElement(m.SharedContracts
                .OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => c with { Sha256 = c.Sha256.ToLowerInvariant() }), Json);
        if (m.Data is { } data) values["data"] = JsonSerializer.SerializeToElement(PluginDataValidation.Normalize(data), Json);
        if (m.Control) values["control"] = JsonSerializer.SerializeToElement(true);
        if (m.ToolApiVersion is { } tools) values["toolApiVersion"] = JsonSerializer.SerializeToElement(tools);
        if (m.Configuration is { } configuration) values["configuration"] = JsonSerializer.SerializeToElement(configuration with
            { Bindings = configuration.Bindings.OrderBy(binding => binding.Key, StringComparer.Ordinal).ToArray() }, Json);
        if (m.Ui is { } ui) values["ui"] = JsonSerializer.SerializeToElement(PluginUiValidation.Normalize(ui), Json);
        if (m.Adoption is { } adoption) values["adoption"] = JsonSerializer.SerializeToElement(adoption, Json);
        if (m.ExclusiveResources is { Count: > 0 } exclusive)
            values["exclusiveResources"] = JsonSerializer.SerializeToElement(exclusive.OrderBy(x => x, StringComparer.Ordinal).ToArray(), Json);
        return JsonSerializer.SerializeToElement(values, Json);
    }

    private static JsonElement LegacyFields(PluginPackageManifest m) => m.ApiVersion == 2 ? JsonSerializer.SerializeToElement(new
    {
        m.Id, m.Version, m.ApiVersion, Sha256 = m.Sha256.ToLowerInvariant(),
        DownloadUrl = new Uri(m.DownloadUrl).AbsoluteUri, m.MinimumHostVersion, m.Runtime,
        Permissions = (m.Permissions ?? []).OrderBy(x => x, StringComparer.Ordinal),
        Dependencies = (m.Dependencies ?? []).OrderBy(x => x.Id, StringComparer.Ordinal),
        Settings = SettingFields(m.Settings)
    }, Json) : JsonSerializer.SerializeToElement(new
    {
        m.Id, m.Version, m.ApiVersion, m.EntryAssembly, m.EntryType, Sha256 = m.Sha256.ToLowerInvariant(),
        DownloadUrl = new Uri(m.DownloadUrl).AbsoluteUri, m.TargetFramework, m.MinimumHostVersion,
        Permissions = (m.Permissions ?? []).OrderBy(x => x, StringComparer.Ordinal),
        Dependencies = (m.Dependencies ?? []).OrderBy(x => x.Id, StringComparer.Ordinal),
        Settings = SettingFields(m.Settings)
    }, Json);

    private static JsonElement[] SettingFields(IReadOnlyList<PluginSettingDefinition>? settings)
        => (settings ?? []).OrderBy(field => field.Key, StringComparer.Ordinal).Select(field =>
        {
            var legacy = JsonSerializer.SerializeToElement(new
                { field.Key, field.Label, field.Type, field.DefaultValue, field.Required, Choices = field.Choices ?? [] }, Json);
            if (field.NumberConstraints is null) return legacy;
            var values = legacy.EnumerateObject().ToDictionary(property => property.Name, property => property.Value.Clone());
            values["numberConstraints"] = JsonSerializer.SerializeToElement(field.NumberConstraints, Json);
            return JsonSerializer.SerializeToElement(values, Json);
        }).ToArray();

    internal static bool IsUpgrade(string current, string target)
    {
        var a = Parse(current); var b = Parse(target);
        if (a is null || b is null) return false;
        for (var i = 0; i < 3; i++)
        { var order = b.Value.Main[i].CompareTo(a.Value.Main[i]); if (order != 0) return order > 0; }
        if (a.Value.Pre.Length == 0 || b.Value.Pre.Length == 0)
            return a.Value.Pre.Length != 0 && b.Value.Pre.Length == 0;
        for (var i = 0; i < Math.Min(a.Value.Pre.Length, b.Value.Pre.Length); i++)
        {
            var x = a.Value.Pre[i]; var y = b.Value.Pre[i];
            var xn = BigInteger.TryParse(x, out var xv); var yn = BigInteger.TryParse(y, out var yv);
            var order = xn && yn ? yv.CompareTo(xv) : xn != yn ? (xn ? 1 : -1) : string.CompareOrdinal(y, x);
            if (order != 0) return order > 0;
        }
        return b.Value.Pre.Length > a.Value.Pre.Length;
    }
    private static (BigInteger[] Main, string[] Pre)? Parse(string value)
    {
        var pieces = value.Split('-', 2); var main = pieces[0].Split('.');
        if (main.Length != 3 || main.Any(x => !Number(x))) return null;
        var pre = pieces.Length == 1 ? [] : pieces[1].Split('.');
        if (pre.Any(x => x.Length == 0 || x.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-') ||
            x.All(char.IsAsciiDigit) && !Number(x))) return null;
        return (main.Select(BigInteger.Parse).ToArray(), pre);
    }
    private static bool Number(string value) => value.Length > 0 && value.All(char.IsAsciiDigit) &&
        (value.Length == 1 || value[0] != '0');
}
