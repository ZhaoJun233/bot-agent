using BotAgent.Domain.Plugins;

namespace BotAgent.Services.Plugins;

/// <summary>Dispatches enabled reply policies in stable order. Missing/disabled policies have no effect.</summary>
public sealed class PluginReplyPolicies(IPluginRegistry registry)
{
    public string? BlockReason(ReplyPolicyContext context)
    {
        foreach (var plugin in registry.GetAll().Where(p => p.IsEnabled).OrderBy(p => p.Id, StringComparer.Ordinal))
        {
            if (registry.Find(plugin.Id) is not IReplyPolicyPlugin policy) continue;
            try
            {
                if (policy.BlockReason(context) is not { } reason) continue;
                return reason.Length is > 0 and <= 64 && reason.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_')
                    ? reason : "reply_policy_error";
            }
            catch (Exception)
            {
                // Admission failure is fail-closed; never forward extension exception text into a trace/log.
                return "reply_policy_error";
            }
        }
        return null;
    }
}
