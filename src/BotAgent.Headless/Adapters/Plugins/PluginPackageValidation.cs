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

    public static void Validate(PluginPackageManifest manifest) => Validate(manifest, checkCompatibility: true);

    internal static void Validate(PluginPackageManifest manifest, bool checkCompatibility)
    {
        if (manifest is null) throw new InvalidDataException("Manifest missing.");
        ValidateId(manifest.Id);
        if (manifest.ApiVersion is not (1 or 2) || !Regex.IsMatch(manifest.Version ?? "",
            @"^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z]+([.-][0-9A-Za-z]+)*)?$"))
            throw new InvalidDataException("Unsupported API or non-fixed version.");
        if (!Regex.IsMatch(manifest.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
            throw new InvalidDataException("SHA256 required.");
        if (manifest.ApiVersion == 1)
        {
            ValidateRelativePath(manifest.EntryAssembly!);
            if (manifest.Runtime is not null || !manifest.EntryAssembly!.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(manifest.EntryType) || manifest.EntryType.Length > 512)
                throw new InvalidDataException("Managed entry point required.");
        }
        else ValidatePython(manifest);
        ValidateUrl(manifest.DownloadUrl);
        if (checkCompatibility && !IsCompatible(manifest))
            throw new InvalidDataException("Incompatible host or framework.");
        if (manifest.Permissions?.Count > 64 || manifest.Dependencies?.Count > 64 || manifest.Settings?.Count > 128)
            throw new InvalidDataException("Manifest collection limit exceeded.");
        foreach (var permission in manifest.Permissions ?? [])
            if (!Regex.IsMatch(permission ?? "", "^[a-z][a-z0-9.-]{0,79}$"))
                throw new InvalidDataException("Invalid permission declaration.");
        var dependencyIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dependency in manifest.Dependencies ?? [])
        {
            if (dependency is null) throw new InvalidDataException("Invalid fixed dependency.");
            ValidateId(dependency.Id);
            if (dependency.Id == manifest.Id || !dependencyIds.Add(dependency.Id) ||
                !Regex.IsMatch(dependency.Version ?? "", @"^[0-9]+[.][0-9]+[.][0-9]+(-[0-9A-Za-z.-]+)?$"))
                throw new InvalidDataException("Invalid fixed dependency.");
        }
        var settingKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var setting in manifest.Settings ?? [])
        {
            if (setting is null) throw new InvalidDataException("Invalid declarative setting.");
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

    private static void ValidatePython(PluginPackageManifest manifest)
    {
        var runtime = manifest.Runtime;
        if (manifest.EntryAssembly is not null || manifest.EntryType is not null || manifest.TargetFramework is not null ||
            runtime is null || runtime.Kind != "python" || runtime.ProtocolVersion != 1)
            throw new InvalidDataException("Invalid Python runtime descriptor.");
        var entry = runtime.EntryPoint;
        ValidateRelativePath(entry);
        if (entry.Length > 240 || entry.Equals("botagent_worker.py", StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(entry, @"^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*\.py\z") ||
            entry.Split('/').Any(part => part.EndsWith('.') ||
                Regex.IsMatch(part.Split('.')[0], @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])\z", RegexOptions.IgnoreCase)))
            throw new InvalidDataException("Entry point must be an ASCII package-relative Python file.");
        if (!Regex.IsMatch(runtime.MinimumPythonVersion ?? "", @"^3\.(?:1[1-9]|[2-9][0-9])\z", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Python minimum version must be 3.11 through 3.99.");
        if (runtime.DependencyMode != "stdlib-only") throw new InvalidDataException("Python dependencies are not prepared: only stdlib-only is supported.");
        if (runtime.Tools is null || runtime.Tools.Count is < 1 or > 64 || runtime.Tools.Distinct(StringComparer.Ordinal).Count() != runtime.Tools.Count ||
            runtime.Tools.Any(tool => !Regex.IsMatch(tool ?? "", @"^[A-Za-z0-9_-]{1,64}\z", RegexOptions.CultureInvariant)))
            throw new InvalidDataException("Python tools must be an explicit unique bounded allowlist.");
    }

    internal static bool IsCompatible(PluginPackageManifest manifest)
        => (manifest.ApiVersion == 2 || PluginPackageManifest.SupportedTargetFrameworks.Contains(manifest.TargetFramework, StringComparer.Ordinal))
            && (manifest.MinimumHostVersion is null ||
                (System.Version.TryParse(manifest.MinimumHostVersion, out var minimum) &&
                 minimum <= typeof(PluginPackageValidation).Assembly.GetName().Version));

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