using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Platforms;

namespace BotAgent.Packages.PlatformLocal;

/// <summary>Exclusive-connection platform driver package for Local Test Channel.</summary>
public sealed class PlatformLocalPlugin : IBotPlugin, IPluginContributionSource, IPluginTwoPhaseLifecycle
{
    private LocalDriver? _driver;

    public string Id => "system.platform-local";
    public string Name => "本地测试通道";
    public string Version => typeof(PlatformLocalPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "本地合成回归与面板调试通道（白名单为空默认关闭、冷采用 + 两阶段激活）。";
    public PluginCategory Category => PluginCategory.Management;
    public bool RequiresTwoPhaseActivation => true;

    public Task PrepareAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _driver = new LocalDriver();
        return Task.CompletedTask;
    }

    public Task ActivateAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    public Task InitializeAsync(PluginContext context, CancellationToken ct)
        => PrepareAsync(context, ct);

    public object? GetContribution(Type contract)
        => contract == typeof(IPlatformDriverContribution) ? _driver : null;

    public Task ShutdownAsync(CancellationToken ct)
    {
        _driver = null;
        return Task.CompletedTask;
    }

    private sealed class LocalDriver : IPlatformDriverContribution
    {
        public PlatformDriverDescriptor Descriptor { get; } = new(
            "local", "本地通道", ExclusiveConnection: true,
            SupportsText: true, SupportsImage: false, SupportsVoice: false, SupportsQuote: true);
        public bool RequiresExclusiveConnection => true;
        public bool AllowsTarget(string nativeTargetId, IReadOnlySet<string> allowlist)
            => !string.IsNullOrWhiteSpace(nativeTargetId) && allowlist is not null && allowlist.Count > 0 && allowlist.Contains(nativeTargetId.Trim());
    }
}
