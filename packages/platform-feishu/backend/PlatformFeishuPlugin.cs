using System.Reflection;
using BotAgent.Domain.Plugins;
using BotAgent.Services.Platforms;

namespace BotAgent.Packages.PlatformFeishu;

/// <summary>Exclusive-connection platform driver package for Feishu (Lark).</summary>
public sealed class PlatformFeishuPlugin : IBotPlugin, IPluginContributionSource, IPluginTwoPhaseLifecycle
{
    private FeishuDriver? _driver;

    public string Id => "system.platform-feishu";
    public string Name => "飞书机器人通道";
    public string Version => typeof(PlatformFeishuPlugin).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+', 2)[0];
    public string Description => "飞书开放平台 Webhook / 长连接接入、验签去重与文本引用降级（冷采用 + 两阶段激活）。";
    public PluginCategory Category => PluginCategory.Management;
    public bool RequiresTwoPhaseActivation => true;

    public Task PrepareAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _driver = new FeishuDriver();
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

    private sealed class FeishuDriver : IPlatformDriverContribution
    {
        public PlatformDriverDescriptor Descriptor { get; } = new(
            "feishu", "飞书", ExclusiveConnection: true,
            SupportsText: true, SupportsImage: false, SupportsVoice: false, SupportsQuote: true);
        public bool RequiresExclusiveConnection => true;
        public bool AllowsTarget(string nativeTargetId, IReadOnlySet<string> allowlist)
            => !string.IsNullOrWhiteSpace(nativeTargetId) && allowlist is not null && allowlist.Contains(nativeTargetId.Trim());
    }
}
