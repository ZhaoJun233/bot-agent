using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;

namespace BotAgent.Services.Plugins.Presets;

/// <summary>Owns daily-budget admission; the shared ledger continues accounting while this plugin is disabled.</summary>
public sealed class TokenQuotaPresetPlugin(ITenantQuotaLedger ledger) : IReplyPolicyPlugin
{
    public const string PluginId = "preset.feature.token-quota";
    public string Id => PluginId;
    public string Name => "Token 配额";
    public string Version => "1.0.0";
    public string Description => "按会话设置每日 Token 上限；超额后暂停普通与主动回复，直接提及仍可回复。用量按 UTC 日重置。";
    public PluginCategory Category => PluginCategory.Management;

    public string? BlockReason(ReplyPolicyContext context)
        => ledger.IsEnergySaving(context.SourceKey) && (context.Proactive || !context.DirectMention)
            ? "quota_energy_saving" : null;

    public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
}
