using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;

namespace BotAgent.Packages.TokenQuota;

public sealed class QuotaPlugin : IReplyPolicyPlugin, IPluginContributionSource, IPluginControlModule
{
    private QuotaLedger? _ledger;
    private QuotaLedger? _management;
    private QuotaOperations? _operations;
    public const string PluginId = "preset.feature.token-quota";
    public string Id => PluginId;
    public string Name => "Token 配额";
    public string Version => ((System.Reflection.AssemblyInformationalVersionAttribute)Attribute.GetCustomAttribute(
        typeof(QuotaPlugin).Assembly, typeof(System.Reflection.AssemblyInformationalVersionAttribute))!).InformationalVersion.Split('+', 2)[0];
    public string Description => "按会话设置每日 Token 上限；超额后暂停普通与主动回复，直接提及仍可回复。用量按 UTC 日重置。";
    public PluginCategory Category => PluginCategory.Management;
    public Task InitializeAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var data = context.Services.GetService(typeof(IPackageDataStore)) as IPackageDataStore
            ?? throw new InvalidOperationException("quota_data_unavailable");
        _ledger = new QuotaLedger(data);
        return Task.CompletedTask;
    }
    public Task PrepareControlAsync(PluginContext context, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _management = new QuotaLedger(context.Services.GetService(typeof(IPackageDataStore)) as IPackageDataStore
            ?? throw new InvalidOperationException("quota_control_data_unavailable"));
        _operations = new QuotaOperations(_management);
        return Task.CompletedTask;
    }
    public object? GetControlContribution(Type contract) => contract == typeof(ITenantQuotaManagement) ? _management
        : contract == typeof(IQuotaOperations) ? _operations : null;
    public Task ShutdownControlAsync(CancellationToken ct) { _operations = null; _management = null; return Task.CompletedTask; }
    public object? GetContribution(Type contract) => contract == typeof(ITenantQuotaManagement) ? _ledger : null;
    public string? BlockReason(ReplyPolicyContext context)
        => QuotaAdmission.BlockReason((_ledger ?? throw new InvalidOperationException("quota_not_ready"))
            .GetQuota(context.SourceKey).EnergySaving, context);
    public Task ShutdownAsync(CancellationToken ct) { _ledger = null; return Task.CompletedTask; }
}
