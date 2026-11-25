using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Platforms;

namespace BotAgent.Packages.PlatformQqPrivate;

/// <summary>Exclusive-connection platform driver package for QQ Private (OneBot / NapCat).</summary>
public sealed class PlatformQqPrivatePlugin : IBotPlugin, IPluginContributionSource, IPluginTwoPhaseLifecycle
{
    private QqPrivateDriver? _driver;

    public string Id => "system.platform-qq-private";
    public string Name => "QQ 私域协议端（OneBot）";
    public string Version => typeof(PlatformQqPrivatePlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "独占 QQ 私域 OneBot/NapCat 连接生命周期与收发能力声明（冷采用 + 两阶段激活）。";
    public PluginCategory Category => PluginCategory.Management;
    public bool RequiresTwoPhaseActivation => true;

    public Task PrepareAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _driver = new QqPrivateDriver();
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

    private sealed class QqPrivateDriver : IPlatformDriverContribution
    {
        public PlatformDriverDescriptor Descriptor { get; } = new(
            "qq.private", "QQ 私域（OneBot）", ExclusiveConnection: true,
            SupportsText: true, SupportsImage: true, SupportsVoice: true, SupportsQuote: true);
        public bool RequiresExclusiveConnection => true;
        public bool AllowsTarget(string nativeTargetId, IReadOnlySet<string> allowlist)
            => !string.IsNullOrWhiteSpace(nativeTargetId) && allowlist is not null && allowlist.Contains(nativeTargetId.Trim());
    }
}
