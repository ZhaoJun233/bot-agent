using System.Text.Json.Nodes;
using BotAgent.Domain.Mcp;
using BotAgent.Domain.Plugins;
using BotAgent.Mcp;

namespace BotAgent.Adapters.Plugins;

/// <summary>Package lifecycle adapter; no Python code runs while installed but disabled.</summary>
internal sealed class PluginPythonPackage(PluginPackageManifest manifest, PluginPythonSources sources, PythonWorkerToolSource? worker, string directory) : IBotPlugin
{
    public string Id => manifest.Id;
    public string Name => manifest.Id;
    public string Version => manifest.Version;
    public string Description => "Local Python process (not a sandbox)";
    public PluginCategory Category => PluginCategory.Feature;
    public bool IsPreset => false;
    public Task InitializeAsync(PluginContext context, CancellationToken ct) => Task.CompletedTask;
    internal void Publish() { if (worker is not null) sources.Attach(Id, worker); }
    public async Task ShutdownAsync(CancellationToken ct)
    {
        if (worker is not null)
        {
            sources.Detach(Id, worker);
            await worker.DisposeAsync().ConfigureAwait(false);
        }
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); } catch { }
    }
    internal static async Task<IBotPlugin> StartAsync(PluginPackageManifest manifest, string directory,
        PluginPythonSources sources, string? settings, bool enabled, CancellationToken ct)
    {
        if (!enabled) return new PluginPythonPackage(manifest, sources, null, directory);
        var runtime = manifest.Runtime!;
        var worker = new PythonWorkerToolSource(new PythonWorkerOptions(PluginPythonSources.SourceId(manifest.Id), Executable(), directory,
            runtime.EntryPoint, runtime.Tools, TimeSpan.FromSeconds(30), 64 * 1024)
        {
            RequiredPythonVersion = runtime.MinimumPythonVersion,
            Configuration = string.IsNullOrWhiteSpace(settings) ? new() : JsonNode.Parse(settings) as JsonObject ?? new(),
            GrantedCapabilities = manifest.Permissions ?? []
        });
        try
        {
            await worker.DiscoverAsync(ct).ConfigureAwait(false);
            if (worker.State != McpConnectionState.Ready) throw new InvalidDataException(worker.LastErrorCode ?? "python_unavailable");
            return new PluginPythonPackage(manifest, sources, worker, directory);
        }
        catch { await worker.DisposeAsync().ConfigureAwait(false); throw; }
    }
    internal static string Executable() => Environment.GetEnvironmentVariable("BOTAGENT_PYTHON_EXECUTABLE")
        ?? Environment.GetEnvironmentVariable("QQCHAT_PYTHON_EXECUTABLE") ?? string.Empty;
    internal static async Task<string?> CompatibilityAsync(PluginPackageManifest manifest, CancellationToken ct)
    {
        if (!PluginPackageValidation.IsCompatible(manifest)) return "Host version or runtime is incompatible.";
        if (manifest.Runtime is null) return null;
        var result = await PythonWorkerInterpreter.CheckAsync(Executable(), manifest.Runtime.MinimumPythonVersion, ct).ConfigureAwait(false);
        return result.Compatible ? null : result.ReasonCode;
    }
}
