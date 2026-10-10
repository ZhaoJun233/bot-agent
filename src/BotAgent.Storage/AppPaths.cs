namespace BotAgent.Storage;

/// <summary>The host resolves paths once; modules never rediscover environment or workspace roots.</summary>
public static class StoragePaths
{
    private static string? _runtimeRoot;

    public static string RuntimeRoot => Volatile.Read(ref _runtimeRoot)
        ?? throw new InvalidOperationException("storage_paths_not_initialized");
    public static string DataDir => Path.Combine(RuntimeRoot, "data");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");

    public static void Initialize(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runtimeRoot));
        var previous = Interlocked.CompareExchange(ref _runtimeRoot, full, null);
        if (previous is not null && !string.Equals(previous, full, OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidOperationException("storage_paths_already_initialized");
    }
}

internal static class AppPaths
{
    public static string RuntimeRoot => StoragePaths.RuntimeRoot;
    public static string DataDir => StoragePaths.DataDir;
    public static string SettingsFile => StoragePaths.SettingsFile;
}