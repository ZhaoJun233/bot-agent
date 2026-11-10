using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;

namespace BotAgent.Mcp;

/// <summary>Host-approved local worker. Capabilities are declarations, not an OS sandbox.</summary>
public sealed record PythonWorkerOptions(
    string Id,
    string ExecutablePath,
    string PackageDirectory,
    string EntryPoint,
    IReadOnlyList<string> AllowedTools,
    TimeSpan CallTimeout,
    int MaxResultBytes)
{
    public JsonObject Configuration { get; init; } = new();
    public IReadOnlyList<string> GrantedCapabilities { get; init; } = Array.Empty<string>();
    public int MaxStartsPerMinute { get; init; } = 3;
    public string RequiredPythonVersion { get; init; } = "3.11";

    public override string ToString() => $"PythonWorkerOptions {{ Id = {Id}, Tools = {AllowedTools.Count} }}";
}

internal sealed class PythonWorkerException(string code, bool unknownOutcome = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool UnknownOutcome { get; } = unknownOutcome;
}

internal static class PythonWorkerValidation
{
    public static bool IsIdentifier(string? value) => !string.IsNullOrEmpty(value)
        && value.Length <= 64
        && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '_' or '-');

    public static string EntryPath(PythonWorkerOptions options)
    {
        if (!IsIdentifier(options.Id) || string.IsNullOrWhiteSpace(options.ExecutablePath)
            || options.AllowedTools.Any(t => !IsIdentifier(t))
            || options.MaxStartsPerMinute is < 1 or > 10
            || string.IsNullOrWhiteSpace(options.EntryPoint) || options.EntryPoint.Length > 240
            || options.EntryPoint.Any(c => c > 127 || c == '\\' || c == ':')
            || options.EntryPoint.Split('/').Any(part => part is "" or "." or "..")
            || Path.IsPathRooted(options.EntryPoint))
            throw new ArgumentException("Invalid Python worker options.");
        var root = Path.GetFullPath(options.PackageDirectory);
        var entry = Path.GetFullPath(Path.Combine(root, options.EntryPoint));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!entry.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, comparison)
            || !entry.EndsWith(".py", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Python entry must be a package-local .py file.");
        // Reject symlink/junction package roots and every component leading to the entry.
        for (var current = entry; current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Python package paths must not contain links.");
        }
        return entry;
    }
}
