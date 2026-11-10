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

    private static JsonElement Fields(PluginPackageManifest m) => m.ApiVersion == 2 ? JsonSerializer.SerializeToElement(new
    {
        m.Id, m.Version, m.ApiVersion, Sha256 = m.Sha256.ToLowerInvariant(),
        DownloadUrl = new Uri(m.DownloadUrl).AbsoluteUri, m.MinimumHostVersion, m.Runtime,
        Permissions = (m.Permissions ?? []).OrderBy(x => x, StringComparer.Ordinal),
        Dependencies = (m.Dependencies ?? []).OrderBy(x => x.Id, StringComparer.Ordinal),
        Settings = (m.Settings ?? []).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
        { x.Key, x.Label, x.Type, x.DefaultValue, x.Required, Choices = x.Choices ?? [] })
    }, Json) : JsonSerializer.SerializeToElement(new
    {
        m.Id, m.Version, m.ApiVersion, m.EntryAssembly, m.EntryType, Sha256 = m.Sha256.ToLowerInvariant(),
        DownloadUrl = new Uri(m.DownloadUrl).AbsoluteUri, m.TargetFramework, m.MinimumHostVersion,
        Permissions = (m.Permissions ?? []).OrderBy(x => x, StringComparer.Ordinal),
        Dependencies = (m.Dependencies ?? []).OrderBy(x => x.Id, StringComparer.Ordinal),
        Settings = (m.Settings ?? []).OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new
        { x.Key, x.Label, x.Type, x.DefaultValue, x.Required, Choices = x.Choices ?? [] })
    }, Json);

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