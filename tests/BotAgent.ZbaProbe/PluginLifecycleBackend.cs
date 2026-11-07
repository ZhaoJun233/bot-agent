using BotAgent.Domain.Configuration;
using BotAgent.Panel;

namespace BotAgent.ZbaProbe;

internal sealed partial class SyntheticZbaBackend
{
    public ValueTask<ZbaPluginLifecyclePreview> PreviewPluginLifecycleAsync(ConfigurationCaller caller,
        ZbaPluginLifecyclePreviewRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(new ZbaPluginLifecyclePreview("lifecycle-synthetic", "2026-01-02T03:14:05Z",
            request.ExpectedRevision, request.Kind, request.PluginId, "none", SyntheticManifest, null, request.Candidate?.Manifest,
            null, null, false, "installed", false, true, [], [new("preservation", "passed", "synthetic")],
            new(true, true, true), "synthetic"));
    public ValueTask<ZbaPluginOperationResult> RollbackPluginAsync(ConfigurationCaller caller,
        ZbaPluginMutationRequest request, CancellationToken ct = default)
        => ValueTask.FromResult(Operation(request.IdempotencyKey, request.Manifest.Id, "rollback"));
}