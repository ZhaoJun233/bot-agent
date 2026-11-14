using BotAgent.Domain.Management;
using BotAgent.Domain.Platforms;

namespace BotAgent.Adapters.Panel.Zba;

internal static class OfficialMessagingCapabilities
{
    internal static MessagingCapabilitySnapshot Project(EffectivePlatformPolicy? policy, DateTimeOffset observedAt)
    {
        MessagingCapability Implemented(string id, string feature, string? action = null) => new(id,
            MessagingImplementation.Implemented, PolicyCondition(policy, feature, action),
            new(MessagingConditionState.Unknown, "application_grant_unverified"),
            new(MessagingConditionState.Unknown, id == "recall" ? "own_message_proof_required" : "conversation_not_selected"),
            new(MessagingConditionState.Unknown, "actor_not_selected"));
        MessagingCapability Deferred(string id) => new(id, MessagingImplementation.Deferred,
            new(MessagingConditionState.Unknown, "implementation_deferred"),
            new(MessagingConditionState.Unknown, "application_grant_unverified"),
            new(MessagingConditionState.Unknown, "conversation_not_selected"),
            new(MessagingConditionState.Unknown, "actor_not_selected"));
        return new(observedAt,
        [
            Implemented("text", "text"), Implemented("image", "image"), Implemented("voice", "voice"),
            Implemented("quote", "quote"), Implemented("send", "text", "send"),
            Implemented("recall", "recall", "recall"),
            Deferred("role"), Deferred("bot_state"), Deferred("ban"), Deferred("kick"),
            Deferred("markdown"), Deferred("keyboard"), Deferred("menu")
        ]);
    }

    private static MessagingCondition PolicyCondition(EffectivePlatformPolicy? policy, string feature, string? action)
    {
        if (policy is null || policy.Context.PlatformId != PlatformId.QqOfficial)
            return new(MessagingConditionState.Unknown, "platform_policy_unavailable");
        // The resolver owns instance selection, inheritance and capability checks. This projection
        // checks no conversation, plugin, quota, media lease or platform permission.
        if (!policy.Registered) return new(MessagingConditionState.Unavailable,
            policy.Reasons.Contains("ambiguous_platform_instance") ? "ambiguous_platform_instance" : "platform_unregistered");
        if (!policy.Connected) return new(MessagingConditionState.Unavailable, "platform_disconnected");
        var verdict = policy.Feature(feature);
        if (!verdict.Enabled) return new(MessagingConditionState.Blocked, verdict.ReasonCode);
        if (action is not null && policy.ActionAllowlistConfigured && !policy.CanUseAction(action))
            return new(MessagingConditionState.Blocked, "action_not_allowed");
        return new(MessagingConditionState.Satisfied, "platform_policy_satisfied");
    }
}
