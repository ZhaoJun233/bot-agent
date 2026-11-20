namespace BotAgent.Domain.Plugins;

/// <summary>Directory/recovery facts only. They never authorize loading or mutation.</summary>
public sealed record PluginPackageRecovery(string Id, int FormatVersion, int MinimumReaderVersion,
    PluginReleaseReference? Active, PluginReleaseReference? Previous, PluginReleaseReference? Pending,
    bool PendingUninstall, string ExecutionSha256);

public sealed record PluginReleaseReference(string Version, string Sha256);

public sealed record PluginStateCompatibilityEntry(string Id, bool Compatible, string Code,
    string Fingerprint, PluginPackageRecovery? Recovery);

public sealed record PluginStateCompatibilityReport(int CandidateReaderVersion, string CandidateSha256, bool Compatible,
    IReadOnlyList<PluginStateCompatibilityEntry> Entries);
