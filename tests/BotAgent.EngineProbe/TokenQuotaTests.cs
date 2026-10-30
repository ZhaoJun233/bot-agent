using BotAgent.Domain.Ops;
using BotAgent.Domain.Plugins;
using BotAgent.Domain.Ports;
using BotAgent.Services.Plugins;
using BotAgent.Services.Plugins.Presets;

namespace BotAgent.EngineProbe;

internal static class TokenQuotaTests
{
    internal static Task Run()
    {
        var ledger = new Ledger();
        var registry = new PluginManager(_ => { });
        var policies = new PluginReplyPolicies(registry);
        var ordinary = new ReplyPolicyContext("group:10001", false, false);
        Ensure(policies.BlockReason(ordinary) is null, "absent policy must not limit replies");
        registry.Register(new TokenQuotaPresetPlugin(ledger));
        Ensure(policies.BlockReason(ordinary) is null, "below quota must allow replies");
        ledger.RecordUsage(ordinary.SourceKey, 70, 30);
        Ensure(policies.BlockReason(ordinary) == "quota_energy_saving", "threshold blocks ordinary reply");
        Ensure(policies.BlockReason(ordinary with { Proactive = true }) == "quota_energy_saving", "threshold blocks proactive reply");
        Ensure(policies.BlockReason(ordinary with { DirectMention = true }) is null, "direct mention bypasses quota");
        Ensure(policies.BlockReason(ordinary with { DirectMention = true, Proactive = true }) == "quota_energy_saving", "proactive cannot bypass using an old mention");
        Ensure(policies.BlockReason(ordinary with { SourceKey = "official:group:10001" }) is null, "source/channel isolation");
        registry.SetEnabled(TokenQuotaPresetPlugin.PluginId, false);
        Ensure(policies.BlockReason(ordinary) is null, "disabled policy must allow replies");
        ITokenUsageRecorder recorder = ledger;
        recorder.RecordUsage(ordinary.SourceKey, 10, 5);
        Ensure(ledger.GetQuota(ordinary.SourceKey).UsedTotalTokens == 115, "disabled enforcement must not erase or pause accounting");
        registry.SetEnabled(TokenQuotaPresetPlugin.PluginId, true);
        Ensure(policies.BlockReason(ordinary) == "quota_energy_saving", "re-enabled policy uses accumulated usage");
        ledger.SetDailyTokenLimit(ordinary.SourceKey, 200);
        Ensure(policies.BlockReason(ordinary) is null, "raising limit releases silence without resetting usage");
        var faultyRegistry = new PluginManager(_ => { });
        faultyRegistry.Register(new SyntheticPolicy(() => throw new InvalidOperationException("synthetic-private-exception")));
        Ensure(new PluginReplyPolicies(faultyRegistry).BlockReason(ordinary) == "reply_policy_error", "policy failures must fail closed without exception text");
        foreach (var reason in new[] { "", "synthetic private text", new string('a', 65) })
        {
            var malformedRegistry = new PluginManager(_ => { });
            malformedRegistry.Register(new SyntheticPolicy(() => reason));
            Ensure(new PluginReplyPolicies(malformedRegistry).BlockReason(ordinary) == "reply_policy_error", "policy reasons must be bounded machine codes");
        }
        return Task.CompletedTask;
    }

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class SyntheticPolicy(Func<string?> block) : IReplyPolicyPlugin
    {
        public string Id => "synthetic.policy";
        public string Name => "Synthetic policy";
        public string Version => "1.0.0";
        public string Description => "Synthetic fault boundary";
        public PluginCategory Category => PluginCategory.Management;
        public string? BlockReason(ReplyPolicyContext context) => block();
        public Task ShutdownAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class Ledger : ITenantQuotaLedger, ITokenUsageRecorder
    {
        private readonly Dictionary<string, TenantQuotaSnapshot> _items = new();
        public TenantQuotaSnapshot GetQuota(string tenantId) => _items.TryGetValue(tenantId, out var value)
            ? value : new(tenantId, 100, 0, 0, "2030-01-01", false);
        public TenantQuotaSnapshot SetDailyTokenLimit(string tenantId, int dailyTokenLimit)
        {
            var quota = GetQuota(tenantId);
            return _items[tenantId] = quota with { DailyTokenLimit = dailyTokenLimit, EnergySaving = quota.UsedTotalTokens >= dailyTokenLimit };
        }
        public bool IsEnergySaving(string tenantId) => GetQuota(tenantId).EnergySaving;
        public void RecordUsage(string tenantId, int promptTokens, int completionTokens)
        {
            var quota = GetQuota(tenantId);
            _items[tenantId] = quota with { UsedPromptTokens = quota.UsedPromptTokens + promptTokens,
                UsedCompletionTokens = quota.UsedCompletionTokens + completionTokens,
                EnergySaving = quota.UsedTotalTokens + promptTokens + completionTokens >= quota.DailyTokenLimit };
        }
    }
}
