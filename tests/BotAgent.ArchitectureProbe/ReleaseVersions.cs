using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace BotAgent.ArchitectureProbe;

internal sealed record ReleaseVersion(string Project, string Kind, string Version);
internal sealed record ReleaseVersionSet(string Host, IReadOnlyList<ReleaseVersion> Independent);

/// <summary>One literal version per owner; registration grants no dependency or runtime privileges.</summary>
internal static class ReleaseVersions
{
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "publish", "node_modules", ".git", "dist" };
    private static readonly HashSet<string> VersionProperties = new(StringComparer.OrdinalIgnoreCase)
        { "Version", "VersionPrefix", "VersionSuffix", "AssemblyVersion", "FileVersion", "InformationalVersion" };

    internal static ReleaseVersionSet Read(string root)
    {
        try { return ReadCore(root); }
        catch (Exception error) when (error is JsonException or System.Xml.XmlException or KeyNotFoundException
            or FormatException or OverflowException)
        { throw Fault("invalid_document"); }
    }

    private static ReleaseVersionSet ReadCore(string root)
    {
        var host = LiteralVersion(XDocument.Load(Path.Combine(root, "Directory.Build.props")), "host");
        if (!Regex.IsMatch(host, @"^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)$"))
            throw Fault("invalid_host_version");
        using var registration = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "build/release-projects.json")));
        Fields(registration.RootElement, "schemaVersion", "projects");
        if (registration.RootElement.GetProperty("schemaVersion").GetInt32() != 1) throw Fault("unsupported_schema");
        var entries = registration.RootElement.GetProperty("projects");
        if (entries.ValueKind != JsonValueKind.Array) throw Fault("invalid_projects");
        var registered = new Dictionary<string, string>(StringComparer.Ordinal);
        var identities = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            Fields(entry, "project", "kind");
            var path = entry.GetProperty("project").GetString() ?? "";
            var kind = entry.GetProperty("kind").GetString() ?? "";
            var pattern = kind switch
            {
                "package" => @"^packages/[a-z][a-z0-9-]*/backend/[A-Za-z][A-Za-z0-9.]*[.]csproj$",
                "contract" => @"^contracts/[A-Za-z][A-Za-z0-9.]*/[A-Za-z][A-Za-z0-9.]*[.]csproj$",
                _ => throw Fault("invalid_owner_kind")
            };
            if (!Regex.IsMatch(path, pattern) || !identities.Add(path) || !registered.TryAdd(path, kind))
                throw Fault("invalid_or_duplicate_project");
        }
        var solution = XDocument.Load(Path.Combine(root, "BotAgent.slnx")).Descendants("Project")
            .Select(entry => entry.Attribute("Path")?.Value.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var releases = new List<ReleaseVersion>();
        foreach (var file in Files(root))
        {
            if (!file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)) continue;
            var path = Path.GetRelativePath(root, file).Replace('\\', '/');
            var xml = XDocument.Load(file);
            if (!registered.TryGetValue(path, out var kind))
            {
                if (xml.Descendants().Any(element => VersionProperties.Contains(element.Name.LocalName)))
                    throw Fault("unregistered_version:" + path);
                if (path.StartsWith("packages/", StringComparison.Ordinal) || path.StartsWith("contracts/", StringComparison.Ordinal))
                    throw Fault("unregistered_owner:" + path);
                continue;
            }
            if (!solution.Contains(path)) throw Fault("project_missing_from_solution:" + path);
            releases.Add(new(path, kind, LiteralVersion(xml, path)));
        }
        if (releases.Count != registered.Count) throw Fault("registered_project_missing");
        return new(host, releases.OrderBy(entry => entry.Project, StringComparer.Ordinal).ToArray());
    }

    private static string LiteralVersion(XDocument document, string owner)
    {
        var versions = document.Descendants().Where(element => VersionProperties.Contains(element.Name.LocalName)).ToArray();
        if (versions.Length != 1 || versions[0].Name != "Version") throw Fault("single_version_required:" + owner);
        var version = versions[0];
        if (version.HasElements || version.Attributes().Any() || version.Parent?.Name != "PropertyGroup"
            || version.Parent.Parent != document.Root || version.Ancestors().Any(element =>
                element.Attributes().Any(attribute => attribute.Name.LocalName == "Condition")))
            throw Fault("conditional_or_indirect_version:" + owner);
        var value = version.Value.Trim();
        if (!Regex.IsMatch(value, @"^(0|[1-9][0-9]*)[.](0|[1-9][0-9]*)[.](0|[1-9][0-9]*)(-[0-9A-Za-z-]+([.][0-9A-Za-z-]+)*)?$")
            || value.Split('-', 2).Skip(1).SelectMany(part => part.Split('.')).Any(part =>
                part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit)))
            throw Fault("nonliteral_or_invalid_version:" + owner);
        return value;
    }

    private static void Fields(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object) throw Fault("invalid_registration");
        var actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        if (actual.Length != names.Length || actual.Distinct(StringComparer.Ordinal).Count() != names.Length
            || actual.Any(name => !names.Contains(name, StringComparer.Ordinal))) throw Fault("invalid_registration_fields");
    }

    private static IEnumerable<string> Files(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw Fault("linked_source_directory");
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw Fault("linked_source_file");
            yield return file;
        }
        foreach (var child in Directory.EnumerateDirectories(directory))
            if (!ExcludedDirectories.Contains(Path.GetFileName(child)))
                foreach (var file in Files(child)) yield return file;
    }

    private static InvalidOperationException Fault(string code) => new("release_version:" + code);
}
