using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

internal sealed partial class SyntheticZbaBackend
{
    public ValueTask<ZbaPluginDiscovery> DiscoverPluginSourceAsync(ConfigurationCaller caller, string sourceUrl, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaPluginDiscovery("discovery-synthetic", sourceUrl, new string('a', 40),
            "2026-01-02T03:14:05Z", [new(SyntheticManifest.Id, SyntheticManifest.Version, null)]));
    public ValueTask<ZbaPluginPreview> PreviewPluginAsync(ConfigurationCaller caller, ZbaPluginPreviewRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaPluginPreview("preview-synthetic", "2026-01-02T03:14:05Z", request.ExpectedRevision,
            SyntheticManifest, new(request.Origin), null, null, false, true,
            [new("artifact", "pending", "synthetic archive not downloaded")], "synthetic trust notice"));
}