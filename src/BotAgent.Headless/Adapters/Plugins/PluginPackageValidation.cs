using System.Text.Json;
using System.Text.RegularExpressions;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Net;

namespace BotAgent.Adapters.Plugins;

public static class PluginPackageValidation
{
    public static PluginPackageManifest ParseManifest(string json)
    {
        if (json.Length > 32768) throw new InvalidDataException("Manifest too large.");
        var manifest = JsonSerializer.Deserialize<PluginPackageManifest>(json, JsonOptions)
            ?? throw new InvalidDataException("Manifest missing.");
        Validate(manifest);
        return manifest;
    }

    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { WriteIndented = true, UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow };

    public static void Validate(PluginPackageManifest manifest)
    {
        ValidateId(manifest.Id);
        if (manifest.ApiVersion != 1 || !Regex.IsMatch(manifest.Version ?? "",
            @"^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$"))
            throw new InvalidDataException("Unsupported API or non-fixed version.");
        if (!Regex.IsMatch(manifest.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("SHA256 required.");
        ValidateRelativePath(manifest.EntryAssembly);
        if (!manifest.EntryAssembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 512)
            throw new InvalidDataException("Managed entry point required.");
        ValidateUrl(manifest.DownloadUrl);
        if (!PluginPackageManifest.SupportedTargetFrameworks.Contains(manifest.TargetFramework, StringComparer.Ordinal)
            || (manifest.MinimumHostVersion != null &&
            (!System.Version.TryParse(manifest.MinimumHostVersion, out var minimum) ||
             minimum > typeof(PluginPackageValidation).Assembly.GetName().Version)))
            throw new InvalidDataException("Incompatible host or framework.");
        if (manifest.Permissions?.Count > 64 || manifest.Dependencies?.Count > 64 || manifest.Settings?.Count > 128)
            throw new InvalidDataException("Manifest collection limit exceeded.");
        foreach (var permission in manifest.Permissions ?? [])
            if (!Regex.IsMatch(permission ?? "", "^[a-z][a-z0-9.-]{0,79}$"))
                throw new InvalidDataException("Invalid permission declaration.");
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies ?? [])
        {
            ValidateId(dependency.Id);
            if (dependency.Id == manifest.Id || !dependencyIds.Add(dependency.Id) ||
                !Regex.IsMatch(dependency.Version ?? "", @"^[0-9]+[.][0-9]+[.][0-9]+(-[0-9A-Za-z.-]+)?$"))
                throw new InvalidDataException("Invalid fixed dependency.");
        }
        var settingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in manifest.Settings ?? [])
        {
            var key = setting.Key ?? "";
            var label = setting.Label ?? "";
            if (!Regex.IsMatch(key, "^[a-zA-Z][a-zA-Z0-9_.-]{0,79}$") ||
                !settingKeys.Add(key) || string.IsNullOrWhiteSpace(label) || label.Length > 200 ||
                setting.Type is not ("string" or "boolean" or "number" or "secret" or "select") ||
                setting.DefaultValue?.Length > 4096 || setting.Choices?.Count > 100 ||
                (setting.Type == "secret" && setting.DefaultValue != null) ||
                (setting.Type == "select" && (setting.Choices == null || setting.Choices.Count == 0)))
                throw new InvalidDataException("Invalid declarative setting.");
        }
    }

    public static void ValidateId(string id)
    {
        var value = id ?? "";
        if (!Regex.IsMatch(value, "^[a-z][a-z0-9.-]{0,79}$") || value.Contains("..") || value.EndsWith('.') ||
            value.StartsWith("preset.", StringComparison.Ordinal))
            throw new InvalidDataException("Invalid or reserved package ID.");
        ValidateRelativePath(value);
    }

    internal static Uri ValidateUrl(string url)
    {
        if (!SafeUrl.TryValidate(url, false, out var uri, out _) || uri.Scheme != "https" ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidDataException("Public HTTPS package URL required.");
        return uri;
    }

    internal static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 240 || path.Contains('\\') || path.StartsWith('/') ||
            path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                Regex.IsMatch(p.Split('.')[0], "^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])$", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Unsafe package path.");
    }
}
