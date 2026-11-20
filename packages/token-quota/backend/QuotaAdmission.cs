using BotAgent.Domain.Plugins;

namespace BotAgent.Packages.TokenQuota;

internal static class QuotaAdmission
{
    internal static string? BlockReason(bool energySaving, ReplyPolicyContext context)
        => energySaving && (context.Proactive || !context.DirectMention) ? "quota_energy_saving" : null;
}
