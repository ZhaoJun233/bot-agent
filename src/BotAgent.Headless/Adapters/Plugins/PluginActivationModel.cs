using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>A host-selected model handle bound to one approved runtime generation.</summary>
internal sealed class PluginActivationModel(IGroundedTextModel model) : IGroundedTextModel
{
    private PluginManager.InvocationGate? _gate;

    internal static bool Requested(PluginPackageManifest? manifest)
        => manifest?.Permissions?.Contains(IGroundedTextModel.Permission, StringComparer.Ordinal) == true;

    internal static PluginActivationModel? Create(PluginPackageManifest manifest, PluginContext context)
        => !Requested(manifest) ? null : new(context.Services.GetService(typeof(IGroundedTextModel)) as IGroundedTextModel
            ?? throw new InvalidOperationException("plugin_model_missing"));

    internal PluginActivationModel ControlView() => new(model);
    internal void Publish(PluginManager.InvocationGate gate) => Volatile.Write(ref _gate, gate);

    public async Task<GroundedTextResult> GenerateAsync(GroundedTextRequest request, CancellationToken ct)
    {
        using var lease = Volatile.Read(ref _gate)?.TryAcquire()
            ?? throw new InvalidOperationException("plugin_model_not_ready");
        return await model.GenerateAsync(request, ct).ConfigureAwait(false);
    }
}
