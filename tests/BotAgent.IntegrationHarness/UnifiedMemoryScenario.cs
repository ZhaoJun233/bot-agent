using System.Text.Json.Nodes;

namespace BotAgent.IntegrationHarness;

public static partial class Program
{
    /// <summary>OneBot commands through a real Host, persisted memory, and chat/Agent model requests.</summary>
    private static async Task RunUnifiedMemoryScenarioAsync()
    {
        Section("MEMORY153 real-host authorization, lifecycle, derived-state isolation");
        using var f = new UnifiedMemoryFixture();
        await f.StartAsync();

        var initial = await f.ChatAsync("M153_DEFAULT", UnifiedMemoryFixture.Target);
        Check("M153.01 default Host has no unified memory", !HasMemory(initial));
        await f.NaturalAgentAttemptAsync("M153_NATURAL_BASELINE", UnifiedMemoryFixture.Target, expectBlocked: false);
        var legacy = await f.ChatAsync("M153_LEGACY_BASELINE", UnifiedMemoryFixture.Source);
        Check("M153.01b exact provenance legacy profile is observable before forget", RequestText(legacy).Contains(UnifiedMemoryFixture.LegacySentinel));
        Check("M153.01c unscoped scalar legacy profile is not model-readable", !RequestText(legacy).Contains(UnifiedMemoryFixture.UnscopedLegacySentinel));
        Check("M153.01d foreign-account legacy profile is not model-readable", !RequestText(legacy).Contains(UnifiedMemoryFixture.ForeignAccountSentinel));
        Check("M153.01e foreign-instance legacy profile is not model-readable", !RequestText(legacy).Contains(UnifiedMemoryFixture.ForeignInstanceSentinel));
        Check("M153.01f opaque legacy storage key is not model-visible", !RequestText(legacy).Contains("legacy-profile:v1:"));
        await f.NaturalAgentAttemptAsync("M153_LEGACY_AGENT_ATTEMPT", UnifiedMemoryFixture.Source);
        var scope = await f.CommandAsync("//memory scope");
        UnifiedMemoryFixture.Require("M153.02 default driver identity is usable and exact",
            scope == "scope:qq.private/legacy//private/10002/~");
        var disabled = await f.CommandAsync("//memory remember length brief");
        Check("M153.03 disabled Host cannot create candidates", !disabled.Contains("//memory confirm"));
        var generic = await f.CommandAsync("//memory list", UnifiedMemoryFixture.Source);
        Check("M153.04 group has only generic private handoff", generic.Contains("本人私聊")
            && !generic.Contains("scope:") && !generic.Contains("brief") && !generic.Contains("//memory confirm"));
        Check("M153.05 group malformed and list responses indistinguishable",
            generic == await f.CommandAsync("//memory show invalid", UnifiedMemoryFixture.Source));

        var start = await f.CommandAsync("//memory start");
        UnifiedMemoryFixture.Require("M153.06 exact private governor can enable", start.Contains("已完成"));
        var requested = await f.CommandAsync("//memory remember length brief to " + UnifiedMemoryFixture.TargetScope, UnifiedMemoryFixture.Source);
        Check("M153.07 group request does not disclose creation or fact", requested == generic);
        var list = await f.CommandAsync("//memory list");
        var id = UnifiedMemoryFixture.FactId(list);
        var review = await f.CommandAsync("//memory show " + id);
        var confirm = UnifiedMemoryFixture.Confirmation(review);
        Check("M153.08 review binds source and complete exact target", review.Contains(UnifiedMemoryFixture.SourceScope)
            && review.Contains(UnifiedMemoryFixture.TargetScope) && review.Contains("ReplyLength = brief"));
        Check("M153.09 review warns future group audience and source consent", review.Contains("未来加入成员")
            && confirm.Contains("allow-source accept-group-audience"));
        Check("M153.10 pending source cannot inject", !HasMemory(await f.ChatAsync("M153_PENDING_SOURCE", UnifiedMemoryFixture.Source)));
        Check("M153.11 pending target cannot inject", !HasMemory(await f.ChatAsync("M153_PENDING_TARGET", UnifiedMemoryFixture.Target)));
        var stranger = await f.CommandAsync("//memory show " + id, user: UnifiedMemoryFixture.Other);
        Check("M153.12 another participant cannot review", !stranger.Contains(id) && !stranger.Contains("brief"));
        Check("M153.13 another participant cannot confirm", !(await f.CommandAsync(confirm, user: UnifiedMemoryFixture.Other)).Contains("已完成"));
        Check("M153.14 group confirmation stays generic", generic == await f.CommandAsync(confirm, UnifiedMemoryFixture.Source));
        Check("M153.15 group confirmation grants nothing", !HasMemory(await f.ChatAsync("M153_GROUP_CONFIRM", UnifiedMemoryFixture.Target)));

        await CheckUnifiedMemoryAgentBaselineAsync(f);
        UnifiedMemoryFixture.Require("M153.16 private owner confirmation succeeds", (await f.CommandAsync(confirm)).Contains("已完成"));
        var target = await f.ChatAsync("M153_CONFIRMED_TARGET", UnifiedMemoryFixture.Target, poison: true);
        Check("M153.17 confirmed target injects exact preference", HasPreference(target));
        Check("M153.18 model prompt excludes management handles", !SystemText(target).Contains(id)
            && !SystemText(target).Contains(UnifiedMemoryFixture.SourceScope) && !SystemText(target).Contains("//memory confirm"));
        var unrelated = await f.ChatAsync("M153_UNRELATED", UnifiedMemoryFixture.Unrelated);
        Check("M153.19 same member unrelated group receives no grant", !HasMemory(unrelated));
        Check("M153.20 memory-derived Mood cannot enter another conversation", !RequestText(unrelated).Contains(UnifiedMemoryFixture.MoodSentinel));
        var other = await f.ChatAsync("M153_OTHER_SUBJECT", UnifiedMemoryFixture.Target, user: UnifiedMemoryFixture.Other);
        Check("M153.21 same nickname other subject cannot use grant", !HasMemory(other));
        Check("M153.22 memory-derived VibeNote cannot leak to another subject", !RequestText(other).Contains(UnifiedMemoryFixture.VibeSentinel));
        Check("M153.23 explicitly confirmed source is usable", HasPreference(await f.ChatAsync("M153_CONFIRMED_SOURCE", UnifiedMemoryFixture.Source)));
        await f.NaturalAgentAttemptAsync("M153_GRANTED_AGENT_ATTEMPT", UnifiedMemoryFixture.Target);
        await CheckUnifiedMemoryAgentAsync(f);
        await CheckUnifiedMemoryLifecycleAsync(f, id);
    }

    private static bool HasMemory(JsonObject request) => SystemText(request).Contains("当前发言人明确确认、仅限本会话使用的交互偏好");
    private static bool HasPreference(JsonObject request) => HasMemory(request) && SystemText(request).Contains(UnifiedMemoryFixture.Preference);
    private static bool HasUntrustedLegacy(JsonObject request)
    {
        var text = RequestText(request);
        return text.Contains(UnifiedMemoryFixture.UnscopedLegacySentinel)
            || text.Contains(UnifiedMemoryFixture.ForeignAccountSentinel)
            || text.Contains(UnifiedMemoryFixture.ForeignInstanceSentinel);
    }
    private static string RequestText(JsonObject request)
        => string.Join("\n", (request["messages"] as JsonArray ?? new JsonArray()).Select(message =>
            message?["content"] is JsonValue value && value.TryGetValue<string>(out var text) ? text
                : message?["content"] is JsonArray parts ? string.Join("\n", parts.Select(part => part?["text"]?.GetValue<string>() ?? "")) : ""));
}
