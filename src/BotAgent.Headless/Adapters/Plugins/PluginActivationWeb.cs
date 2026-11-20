using BotAgent.Domain.Plugins;
using BotAgent.Plugins.Net;
using BotAgent.Services.Plugins;

namespace BotAgent.Adapters.Plugins;

/// <summary>A host-approved transport capability bound to one published runtime generation.</summary>
internal sealed class PluginActivationWeb(IWebPageTransport transport) : IWebPageTransport
{
    private PluginManager.InvocationGate? _gate;

    internal static bool Requested(PluginPackageManifest? manifest)
        => manifest?.Permissions?.Contains(IWebPageTransport.Permission, StringComparer.Ordinal) == true;

    internal static PluginActivationWeb? Create(PluginPackageManifest manifest, PluginContext context)
        => !Requested(manifest) ? null : new(context.Services.GetService(typeof(IWebPageTransport)) as IWebPageTransport
            ?? throw new InvalidOperationException("plugin_web_transport_missing"));

    internal PluginActivationWeb ControlView() => new(transport);
    internal void Publish(PluginManager.InvocationGate gate) => Volatile.Write(ref _gate, gate);

    public bool TryValidate(string? url, out Uri uri, out string reason)
    {
        using var lease = Acquire();
        return transport.TryValidate(url, out uri, out reason);
    }

    public async Task<WebPageResponse> GetAsync(WebPageRequest request, CancellationToken ct)
    {
        using var lease = Acquire();
        return await transport.GetAsync(request, ct).ConfigureAwait(false);
    }

    private IDisposable Acquire() => Volatile.Read(ref _gate)?.TryAcquire()
        ?? throw new InvalidOperationException("plugin_web_not_ready");
}
