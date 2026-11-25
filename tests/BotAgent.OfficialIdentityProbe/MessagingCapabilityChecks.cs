using System.Reflection;
using BotAgent.Adapters.Panel;
using BotAgent.Domain.Management;
using BotAgent.Domain.Platforms;

internal static class MessagingCapabilityChecks
{
    private static readonly MethodInfo ProjectMethod = typeof(WebUiServer).Assembly
        .GetType("BotAgent.Adapters.Panel.Zba.OfficialMessagingCapabilities", throwOnError: true)!
        .GetMethod("Project", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static MessagingCapabilitySnapshot Project(EffectivePlatformPolicy? policy, DateTimeOffset? time = null)
        => (MessagingCapabilitySnapshot)ProjectMethod.Invoke(null, [policy, time ?? DateTimeOffset.UnixEpoch])!;
    private static EffectivePlatformPolicy Ready() => new(new(PlatformId.QqOfficial, "synthetic-app", "synthetic-instance"),
        true, true, true, true, PlatformCapabilities.QqOfficial, new HashSet<string> { "send", "recall" },
        new Dictionary<string, bool>(), []) { ActionAllowlistConfigured = true };
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }

    public static async Task RunAsync(Func<string, Func<Task>, Task> test)
    {
        await test("messaging connected driver never implies application or conversation authorization", () =>
        {
            var snapshot = Project(Ready());
            Require(snapshot.Items.Length == 13, "capability coverage changed");
            Require(snapshot.Items.All(item => item.ApplicationGrant.State == MessagingConditionState.Unknown
                && item.Conversation.State == MessagingConditionState.Unknown && item.Actor.State == MessagingConditionState.Unknown),
                "connection promoted external permission");
            Require(snapshot.Items.Count(item => item.Implementation == MessagingImplementation.Deferred) == 7, "restricted work falsely delivered");
            Require(snapshot.Items.Single(item => item.Id == "recall").Conversation.ReasonCode == "own_message_proof_required", "recall lost own-message boundary");
            return Task.CompletedTask;
        });
        foreach (var (name, policy, state, reason) in new (string, EffectivePlatformPolicy?, MessagingConditionState, string)[]
        {
            ("missing policy", null, MessagingConditionState.Unknown, "platform_policy_unavailable"),
            ("wrong platform", Ready() with { Context = new(PlatformId.QqPrivate, "synthetic-app") }, MessagingConditionState.Unknown, "platform_policy_unavailable"),
            ("not loaded", Ready() with { Registered = false }, MessagingConditionState.Unavailable, "platform_unregistered"),
            ("ambiguous instance", Ready() with { Registered = false, Reasons = ["ambiguous_platform_instance"] }, MessagingConditionState.Unavailable, "ambiguous_platform_instance"),
            ("disconnected", Ready() with { Connected = false }, MessagingConditionState.Unavailable, "platform_disconnected"),
            ("disabled platform", Ready() with { Enabled = false }, MessagingConditionState.Blocked, "platform_chat_disabled"),
            ("disabled chat", Ready() with { ChatEnabled = false }, MessagingConditionState.Blocked, "platform_chat_disabled"),
            ("text revoked", Ready() with { FeatureOverrides = new Dictionary<string, bool> { ["text"] = false } }, MessagingConditionState.Blocked, "feature_disabled")
        })
        {
            await test("messaging preserves " + name, () =>
            {
                var text = Project(policy).Items.Single(item => item.Id == "text");
                Require(text.Policy.State == state && text.Policy.ReasonCode == reason, "policy restriction misrepresented");
                return Task.CompletedTask;
            });
        }
        await test("messaging refresh observes action revocation without changing snapshot ownership", () =>
        {
            var first = Project(Ready());
            var second = Project(Ready() with { AllowedActions = new HashSet<string>() }, DateTimeOffset.UnixEpoch.AddSeconds(2));
            Require(first.Items.Single(item => item.Id == "send").Policy.State == MessagingConditionState.Satisfied, "initial policy denied");
            Require(second.Items.Single(item => item.Id == "send").Policy.ReasonCode == "action_not_allowed"
                && second.Items.Single(item => item.Id == "recall").Policy.State == MessagingConditionState.Blocked, "explicit empty allowlist ignored");
            Require(second.Items.Single(item => item.Id == "text").Policy.State == MessagingConditionState.Satisfied, "tool allowlist disabled ordinary reply");
            Require(second.ObservedAt > first.ObservedAt && first.Items.Single(item => item.Id == "send").Policy.State == MessagingConditionState.Satisfied, "refresh mutated old snapshot");
            return Task.CompletedTask;
        });
    }
}
